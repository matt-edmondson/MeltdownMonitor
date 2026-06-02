using MeltdownMonitor.Ble.Windows;
using MeltdownMonitor.Core.Baseline;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.App;

/// <summary>
/// Wires the full beat → HRV → baseline → detection pipeline and exposes
/// live state for the UI and tray icon to read.
/// </summary>
public sealed class Pipeline : IDisposable
{
	private readonly AppSettings _settings;
	private readonly MeltdownRepository _repository;
	private readonly ShortWindowHrvCalculator _hrv = new();
	private readonly BaselineHrvTracker _baseline = new();
	private readonly DysregulationDetector _detector;
	private readonly HypoarousalDetector _hypoDetector;
	private readonly RegulationVelocityTracker _velocity = new();
	private readonly RegulationVelocityTracker _hypoVelocity = new();

	private CancellationTokenSource _cts = new();
	private Task? _pipelineTask;

	public DetectorState CurrentState => _detector.State;

	/// <summary>Current low-arousal/shutdown state — a peer signal to <see cref="CurrentState"/>.</summary>
	public HypoarousalState CurrentHypoarousalState => _hypoDetector.State;
	public DetectionThresholds LatestThresholds => _settings.Thresholds;
	public HrvSample? LatestSample { get; private set; }
	public BaselineHrvTracker Baseline => _baseline;

	/// <summary>Wall-clock time the detector entered <see cref="CurrentState"/>, used by the
	/// status header to show how long the current state has been held. Tracked here rather than
	/// from sample timestamps so the displayed duration ticks smoothly between samples.</summary>
	public DateTimeOffset StateEnteredAt { get; private set; } = DateTimeOffset.UtcNow;

	/// <summary>Latest sensor battery level (0–100), or null until the device reports one.</summary>
	public int? LatestBatteryPercent { get; private set; }

	/// <summary>Latest skin / electrode contact state from the sensor.</summary>
	public SensorContactStatus LatestContact { get; private set; } = SensorContactStatus.NotSupported;

	/// <summary>Sensor identity from the Device Information Service, or null until read.</summary>
	public DeviceInformation? LatestDeviceInfo { get; private set; }

	/// <summary>Latest arousal-vs-baseline reading driving the Regulation Field overlay.</summary>
	public RegulationReading LatestReading { get; private set; } = new(0.0, 1.0, 0.0, 0.5, 0.0);

	/// <summary>Latest escalation/de-escalation velocity + trend of the arousal index.
	/// <see cref="RegulationDynamics.Steady"/> until the baseline is warm.</summary>
	public RegulationDynamics LatestDynamics { get; private set; } = RegulationDynamics.Steady;

	/// <summary>Velocity/trend of the <c>Hypoarousal</c> scalar — the rate of approach to (or
	/// retreat from) low-arousal collapse. <see cref="RegulationDynamics.Steady"/> until the
	/// baseline is warm. Peer to <see cref="LatestDynamics"/> (which tracks the arousal index).</summary>
	public RegulationDynamics LatestHypoarousalDynamics { get; private set; } = RegulationDynamics.Steady;

	/// <summary>How close the body is to clearing the current episode (two-stage: metrics
	/// return to baseline, then hold). <see cref="RecoveryProgress.Inactive"/> outside an
	/// active Warning/Alerting episode.</summary>
	public RecoveryProgress LatestRecovery => _detector.Recovery;

	/// <summary>Configured comet-trail length (clamped 12–2160), read live by the field view.</summary>
	public int RegulationTrailLength => Math.Clamp(_settings.RegulationTrailLength, 12, 2160);

	/// <summary>Configured dwell-heatmap window in readings (clamped 60–17280), read live by the
	/// field view. Usually longer than <see cref="RegulationTrailLength"/>.</summary>
	public int RegulationHeatmapLength => Math.Clamp(_settings.RegulationHeatmapLength, 60, 17280);

	/// <summary>Configured dwell-heatmap overall opacity (clamped 0–1), read live by the field view.</summary>
	public double HeatmapOpacity => Math.Clamp(_settings.HeatmapOpacity, 0.0, 1.0);

	/// <summary>Configured Regulation Field jitter exaggeration multiplier (clamped 0–3),
	/// read live by the field view.</summary>
	public double JitterExaggeration => Math.Clamp(_settings.JitterExaggeration, 0.0, 3.0);

	/// <summary>Configured Regulation Field lobe stroke-thickness multiplier (clamped 0.5–3),
	/// read live by the field view.</summary>
	public double LobeThickness => Math.Clamp(_settings.LobeThickness, 0.5, 3.0);

	/// <summary>Configured Regulation Field lobe opacity (clamped 0–1), read live by the field
	/// view to tame additive-blend saturation of the live trace.</summary>
	public double LobeOpacity => Math.Clamp(_settings.LobeOpacity, 0.0, 1.0);

	public event Action<AlertPayload>? AlertFired;
	public event Action<HrvSample>? SampleUpdated;
	public event Action<Beat>? BeatReceived;

	/// <summary>Fires when the sensor reports a fresh battery level via the BLE
	/// Battery Service (0x180F). Raised only when the source supports it.</summary>
	public event Action<BatteryReading>? BatteryUpdated;

	/// <summary>Fires when the sensor's skin / electrode contact state changes.
	/// Raised only when the source supports it.</summary>
	public event Action<SensorContactStatus>? ContactChanged;

	/// <summary>Fires when the sensor's Device Information is read (typically once on
	/// connect). Raised only when the source supports it.</summary>
	public event Action<DeviceInformation>? DeviceInfoUpdated;

	public Pipeline(AppSettings settings, MeltdownRepository repository)
	{
		_settings = settings;
		_repository = repository;
		_detector = new DysregulationDetector(() => _settings.Thresholds);
		_detector.AlertFired += OnAlertFired;
		_detector.StateChanged += _ => StateEnteredAt = DateTimeOffset.UtcNow;

		_hypoDetector = new HypoarousalDetector(() => _settings.Thresholds.Hypoarousal);
		_hypoDetector.AlertFired += OnAlertFired;
	}

	public void Start()
	{
		StateEnteredAt = DateTimeOffset.UtcNow;
		SeedBaselineFromHistory();
		_cts = new CancellationTokenSource();
		_pipelineTask = RunAsync(_cts.Token);
	}

	// Warm-start the baseline from recent persisted history before live samples flow.
	// Best-effort: a missing or locked database must never prevent startup.
	private void SeedBaselineFromHistory()
	{
		ApplyTuning();
		try
		{
			DateTimeOffset to = DateTimeOffset.UtcNow;
			DateTimeOffset from = to.AddDays(-_settings.BaselineTuning.AnchorWindowDays);
			var history = MeltdownRepository.ReadHistory(_settings.DatabasePath, from, to);
			_baseline.SeedFromHistory(history);
		}
		catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException
			or IOException or InvalidOperationException)
		{
			System.Diagnostics.Debug.WriteLine($"Baseline seeding skipped: {ex.Message}");
		}
	}

	/// <summary>
	/// Re-applies tuning, re-reads history, and re-seeds the baseline live — for the
	/// Settings "Re-seed baseline now" action. Thread-safe against the live update loop.
	/// </summary>
	public void ReseedBaseline() => SeedBaselineFromHistory();

	/// <summary>
	/// Reconstructs up to <paramref name="count"/> recent Regulation Field readings from persisted
	/// HRV samples (oldest first), so the field's comet trail and dwell heatmap survive restarts
	/// instead of starting blank. Deterministic: each sample carries its own baseline and detector
	/// state, so the recomputed reading and colour match what was originally drawn. Best-effort — a
	/// missing or locked database yields an empty list rather than blocking the field.
	/// </summary>
	public IReadOnlyList<RegulationTrailPoint> LoadRecentRegulationTrail(int count)
	{
		if (count <= 0)
		{
			return [];
		}

		try
		{
			var samples = _repository.ReadRecentHrvSamples(count);
			var points = new List<RegulationTrailPoint>(samples.Count);
			foreach (HrvSample s in samples)
			{
				// The stored baseline was warm when the sample was written iff it has usable values;
				// pass full warm-up so the historical reading isn't dimmed as if calibrating.
				bool warm = double.IsFinite(s.BaselineRmssd) && s.BaselineRmssd > 0
					&& double.IsFinite(s.BaselineHr) && s.BaselineHr > 0;
				var reading = RegulationFieldCalculator.Compute(s, _settings.Thresholds, warmUpProgress: 1.0, baselineWarm: warm);
				points.Add(new RegulationTrailPoint(reading, s.State));
			}

			return points;
		}
		catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException
			or IOException or InvalidOperationException)
		{
			System.Diagnostics.Debug.WriteLine($"Regulation trail seeding skipped: {ex.Message}");
			return [];
		}
	}

	// Push user tuning into the calculator and baseline tracker. Responsiveness windows
	// (minutes) convert to a per-sample EWMA alpha using the relevant sample cadence.
	private void ApplyTuning()
	{
		_hrv.EmitIntervalSeconds = _settings.HrvEmitIntervalSeconds;
		_hrv.WindowSeconds = _settings.HrvTuning.ShortWindowSeconds;
		_hrv.ExtendedWindowSeconds = _settings.HrvTuning.ExtendedWindowSeconds;
		_hrv.ExtendedComputeIntervalSeconds = _settings.HrvTuning.ExtendedComputeIntervalSeconds;

		BaselineTuning bt = _settings.BaselineTuning;
		_baseline.RmssdHrAlpha = AlphaFromWindow(_settings.HrvEmitIntervalSeconds, bt.RmssdHrWindowMinutes);
		_baseline.LfHfAlpha = AlphaFromWindow(_settings.HrvTuning.ExtendedComputeIntervalSeconds, bt.LfHfWindowMinutes);
		_baseline.WarmUpMinutes = bt.WarmUpMinutes;
		_baseline.WarmStartWindowMinutes = bt.WarmStartWindowMinutes;
		_baseline.MinWarmStartSamples = bt.MinWarmStartSamples;
		_baseline.MaxAnchorDrift = bt.MaxAnchorDrift;
	}

	// Convert an EWMA "memory window" (minutes) to a per-sample alpha at the given cadence.
	private static double AlphaFromWindow(double cadenceSeconds, double windowMinutes)
	{
		double windowSeconds = windowMinutes * 60.0;
		if (windowSeconds <= 0 || cadenceSeconds <= 0)
		{
			return 1.0;
		}

		return Math.Clamp(cadenceSeconds / windowSeconds, 0.0001, 1.0);
	}

	public void Stop()
	{
		_cts.Cancel();
		try
		{
			_pipelineTask?.GetAwaiter().GetResult();
		}
		catch (OperationCanceledException)
		{
			// Expected on shutdown: cancelling the token unwinds the BLE await chain
			// (e.g. channel ReadAllAsync / retry Task.Delay). Mirrors the mobile
			// Pipeline.StopAsync, which also swallows this. Other faults still surface.
		}
	}

	private async Task RunAsync(CancellationToken cancellationToken)
	{
		// `using` releases the BluetoothLEDevice native handle deterministically when
		// the loop unwinds — on both clean cancellation and an exception path.
		using var source = new PolarHrSource(_settings.DeviceType);

		// Battery and contact are optional source capabilities — wire each only when supported.
		if (source is IBatterySource batterySource)
		{
			batterySource.BatteryLevelChanged += OnBatteryLevelChanged;
		}

		if (source is IContactSource contactSource)
		{
			contactSource.SensorContactChanged += OnSensorContactChanged;
		}

		if (source is IDeviceInfoSource deviceInfoSource)
		{
			deviceInfoSource.DeviceInformationChanged += OnDeviceInfoChanged;
		}

		await foreach (var beat in source.GetBeatsAsync(cancellationToken))
		{
			if (IsPaused())
			{
				continue;
			}

			_repository.InsertBeat(beat);
			BeatReceived?.Invoke(beat);

			ApplyTuning();
			var sample = _hrv.AddBeat(beat, _baseline.BaselineRmssd, _baseline.BaselineHr, _detector.State, _baseline.BaselineLfHfRatio);
			if (sample is null)
			{
				continue;
			}

			// Freeze the baseline during a hypoarousal episode so it can't re-normalise toward the
			// shutdown and blind the detectors. (Hyperarousal episodes are frozen inside Update via
			// sample.State.) Gated on the prior-sample episode state, which keeps the dysregulation
			// detector's IsWarm timing identical; a one-sample lag is negligible for a minutes-long EWMA.
			if (!_hypoDetector.IsEpisodeActive)
			{
				_baseline.Update(sample, LatestContact);
			}

			var state = _detector.Process(sample, _baseline.IsWarm, LatestContact);
			_hypoDetector.Process(sample, _baseline.IsWarm, LatestContact);

			var finalSample = sample with
			{
				BaselineRmssd = _baseline.BaselineRmssd,
				BaselineHr = _baseline.BaselineHr,
				State = state,
				SensorContact = LatestContact,
			};

			_repository.InsertHrvSample(finalSample);
			LatestSample = finalSample;
			SampleUpdated?.Invoke(finalSample);

			LatestReading = RegulationFieldCalculator.Compute(
				finalSample,
				_settings.Thresholds,
				_baseline.WarmUpProgress,
				_baseline.IsWarm);

			// Velocity/trend of the arousal index and the Hypoarousal scalar — see the mobile
			// pipeline for the gating rationale. The two trackers move together so the index and
			// collapse trajectories stay phase-aligned.
			if (_baseline.IsWarm && LatestContact != SensorContactStatus.NotDetected)
			{
				_velocity.Update(LatestReading.Index, finalSample.Timestamp);
				_hypoVelocity.Update(LatestReading.Hypoarousal, finalSample.Timestamp);
			}
			else
			{
				_velocity.Reset();
				_hypoVelocity.Reset();
			}

			LatestDynamics = _velocity.Latest;
			LatestHypoarousalDynamics = _hypoVelocity.Latest;
		}
	}

	private bool IsPaused()
	{
		if (_settings.PausedUntil is null)
		{
			return false;
		}

		if (DateTimeOffset.UtcNow >= _settings.PausedUntil)
		{
			_settings.PausedUntil = null;
			_settings.Save();
			return false;
		}

		return true;
	}

	private void OnAlertFired(AlertPayload payload)
	{
		_repository.InsertAlert(payload);
		AlertFired?.Invoke(payload);
	}

	// Battery notifications arrive on a background BLE thread; the repository
	// serialises the write internally, so we just persist and fan out.
	private void OnBatteryLevelChanged(BatteryReading reading)
	{
		LatestBatteryPercent = reading.Percent;
		_repository.InsertBattery(reading);
		BatteryUpdated?.Invoke(reading);
	}

	// Contact is a live quality signal, not persisted — just track and fan out.
	private void OnSensorContactChanged(SensorContactStatus status)
	{
		LatestContact = status;
		ContactChanged?.Invoke(status);
	}

	// Static identity, read once on connect — track and fan out, not persisted.
	private void OnDeviceInfoChanged(DeviceInformation info)
	{
		LatestDeviceInfo = info;
		DeviceInfoUpdated?.Invoke(info);
	}

	public void Dispose()
	{
		_cts.Dispose();
		_repository.Dispose();
	}
}
