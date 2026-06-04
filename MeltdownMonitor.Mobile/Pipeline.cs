using MeltdownMonitor.Core.Baseline;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Core.Regulation;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.Mobile;

/// <summary>
/// Mobile mirror of <c>MeltdownMonitor.App.Pipeline</c> — wires the
/// beat → HRV → baseline → detection pipeline and exposes live state for the
/// Avalonia ViewModels to consume. Takes an <see cref="IBeatSource"/> by
/// constructor so the platform-neutral assembly stays free of CoreBluetooth.
/// </summary>
public sealed class Pipeline : IDisposable
{
	private readonly MobileSettings _settings;
	private readonly MeltdownRepository _repository;
	private readonly IBeatSource _source;
	private readonly ShortWindowHrvCalculator _hrv = new();
	private readonly BaselineHrvTracker _baseline = new();
	private readonly DysregulationDetector _detector;
	private readonly HypoarousalDetector _hypoDetector;
	private readonly RegulationVelocityTracker _velocity = new();
	private readonly RegulationVelocityTracker _hypoVelocity = new();

	private CancellationTokenSource _cts = new();
	private Task? _runTask;

	public DetectorState CurrentState => _detector.State;

	/// <summary>Current low-arousal/shutdown state — a peer signal to <see cref="CurrentState"/>.</summary>
	public HypoarousalState CurrentHypoarousalState => _hypoDetector.State;

	/// <summary>True when the baseline was self-calibrated cold with no personal history anchor —
	/// the UI surfaces this so a possibly-activated baseline isn't presented as confident calm.</summary>
	public bool IsColdCalibrated => _baseline.IsColdCalibrated;

	public HrvSample? LatestSample { get; private set; }

	/// <summary>Latest sensor battery level (0–100), or null until the source reports one.</summary>
	public int? LatestBatteryPercent { get; private set; }

	/// <summary>Latest skin / electrode contact state from the sensor.</summary>
	public SensorContactStatus LatestContact { get; private set; } = SensorContactStatus.NotSupported;

	/// <summary>Sensor identity from the Device Information Service, or null until read.</summary>
	public DeviceInformation? LatestDeviceInfo { get; private set; }

	/// <summary>The thresholds the detector is currently using — the same value
	/// the Regulation Field needs to scale arousal-vs-baseline. Mirrors the
	/// desktop pipeline's accessor.</summary>
	public DetectionThresholds LatestThresholds => _settings.Thresholds;

	/// <summary>Latest arousal-vs-baseline reading for the Regulation Field
	/// (design doc §6). Neutral until the first sample arrives.</summary>
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

	public event Action<AlertPayload>? AlertFired;
	public event Action<HrvSample>? SampleUpdated;
	public event Action<DetectorState>? StateChanged;

	/// <summary>Fires when the low-arousal/shutdown state changes, so the UI can render collapse
	/// distinctly from the cool REST lobe.</summary>
	public event Action<HypoarousalState>? HypoarousalStateChanged;

	/// <summary>Fires when the sensor reports a fresh battery level (design: BLE
	/// Battery Service 0x180F). Only ever raised when the injected source
	/// implements <see cref="IBatterySource"/>.</summary>
	public event Action<BatteryReading>? BatteryUpdated;

	/// <summary>Fires when the sensor's skin / electrode contact state changes.
	/// Only ever raised when the injected source implements <see cref="IContactSource"/>.</summary>
	public event Action<SensorContactStatus>? ContactChanged;

	/// <summary>Fires when the sensor's Device Information is read (typically once on
	/// connect). Only ever raised when the injected source implements <see cref="IDeviceInfoSource"/>.</summary>
	public event Action<DeviceInformation>? DeviceInfoUpdated;

	/// <summary>Fires for each beat the source delivers while not paused, after it is
	/// persisted. Mirrors the desktop pipeline's BeatReceived so the Metrics charts and
	/// the Regulation Field's RR-textured trace can consume the raw RR stream. Handlers
	/// filter <see cref="Beat.IsArtifact"/> themselves, as the desktop consumers do.</summary>
	public event Action<Beat>? BeatReceived;

	/// <summary>Fires after <see cref="SampleUpdated"/> with the Regulation Field
	/// reading derived from the same sample, so the Now screen can drive the
	/// field without recomputing the calculator inputs itself.</summary>
	public event Action<RegulationReading>? ReadingUpdated;

	/// <summary>Fires after <see cref="ReadingUpdated"/> with the velocity/trend of the
	/// arousal index, derived from the same sample. Steady while calibrating or off-contact.</summary>
	public event Action<RegulationDynamics>? DynamicsUpdated;

	/// <summary>Fires after <see cref="DynamicsUpdated"/> with the velocity/trend of the
	/// Hypoarousal scalar, derived from the same sample. Steady while calibrating or off-contact.</summary>
	public event Action<RegulationDynamics>? HypoarousalDynamicsUpdated;

	/// <summary>Fires after <see cref="DynamicsUpdated"/> with the two-stage recovery progress
	/// for the current episode, derived from the same sample. Inactive outside Warning/Alerting.</summary>
	public event Action<RecoveryProgress>? RecoveryUpdated;

	public Pipeline(MobileSettings settings, MeltdownRepository repository, IBeatSource source)
	{
		_settings = settings;
		_repository = repository;
		_source = source;
		_detector = new DysregulationDetector(() => settings.Thresholds);
		_detector.AlertFired += OnAlertFired;
		_detector.StateChanged += OnStateChanged;

		_hypoDetector = new HypoarousalDetector(() => settings.Thresholds.Hypoarousal);
		_hypoDetector.AlertFired += OnAlertFired;
		_hypoDetector.StateChanged += s => HypoarousalStateChanged?.Invoke(s);

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
	}

	/// <summary>
	/// Reconstructs up to <paramref name="count"/> recent Regulation Field readings from persisted
	/// HRV samples (oldest first), so the field's comet trail survives restarts instead of starting
	/// blank. Deterministic: each sample carries its own baseline and detector state, so the
	/// recomputed reading and colour match what was originally drawn. Best-effort — a missing or
	/// locked database yields an empty list. Mirrors the desktop pipeline.
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

	public void Start()
	{
		_cts = new CancellationTokenSource();
		_runTask = RunAsync(_cts.Token);
	}

	/// <summary>Days of own persisted history read for the RMSSD/HR anchor — mirrors the desktop
	/// head's default <c>BaselineTuning.AnchorWindowDays</c>.</summary>
	private static readonly TimeSpan HistoryAnchorLookback = TimeSpan.FromDays(7);

	/// <summary>
	/// Warm-starts the baseline against a personalised reference before live samples flow
	/// (design doc §8). Two best-effort sources, in order:
	/// <list type="number">
	/// <item>the app's own persisted HRV history — real beat-to-beat RMSSD from prior sessions —
	/// supplies the RMSSD/HR anchor, mirroring the desktop head; then</item>
	/// <item>HealthKit heart-rate history fills the HR baseline if it is still cold. HealthKit HR is
	/// averaged seconds-to-minutes apart and carries no beat-to-beat detail, so it seeds HR only — the
	/// parasympathetic RMSSD baseline is never fabricated from it and warms up from real live beats
	/// instead (audit B).</item>
	/// </list>
	/// The RMSSD baseline always derives from real beats: when recent own-history exists (a relaunch
	/// within the warm-start window) the detector arms immediately against it; otherwise — first-ever
	/// launch, or a cold start after a longer gap — RMSSD calibrates from the live warm-up first, with
	/// the history anchor (if any) guarding it. History seeding runs even when <paramref name="healthStore"/>
	/// is null. When neither source yields data the baseline simply stays cold (not a failure). Must run
	/// before <see cref="Start"/>.
	/// </summary>
	public async Task WarmStartAsync(
		IHealthStore? healthStore,
		TimeSpan? lookback = null,
		CancellationToken cancellationToken = default)
	{
		// Anchor from our own real-RMSSD history first, so a genuine beat-to-beat seed wins over the
		// coarser HealthKit HR estimate that follows.
		SeedBaselineFromHistory();

		if (healthStore is null)
		{
			return;
		}

		// Fill the HR baseline from the robust median of recent HealthKit readings — HR only, never RMSSD.
		var window = lookback ?? TimeSpan.FromHours(24);
		var heartRates = new List<double>();
		await foreach (var hr in healthStore.ReadRecentHeartRateAsync(window).WithCancellation(cancellationToken).ConfigureAwait(false))
		{
			if (hr.HeartRateBpm > 0)
			{
				heartRates.Add(hr.HeartRateBpm);
			}
		}

		_baseline.WarmStartHrBaseline(heartRates);
	}

	// Read recent persisted HRV samples (real RMSSD from prior live sessions) and seed the baseline
	// anchor from them, the same warm-start the desktop head performs. Best-effort: a locked or empty
	// database must never block startup, so persistence faults are swallowed.
	private void SeedBaselineFromHistory()
	{
		try
		{
			DateTimeOffset to = DateTimeOffset.UtcNow;
			DateTimeOffset from = to - HistoryAnchorLookback;
			var history = _repository.GetHrvSamples(from, to);
			_baseline.SeedFromHistory(history);
		}
		catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException
			or IOException or InvalidOperationException)
		{
			System.Diagnostics.Debug.WriteLine($"Mobile baseline history seeding skipped: {ex.Message}");
		}
	}

	public async Task StopAsync()
	{
		_cts.Cancel();
		if (_runTask is not null)
		{
			try
			{
				await _runTask.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
		}
	}

	private async Task RunAsync(CancellationToken cancellationToken)
	{
		await foreach (var beat in _source.GetBeatsAsync(cancellationToken).ConfigureAwait(false))
		{
			if (IsPaused())
			{
				continue;
			}

			_repository.InsertBeat(beat);
			BeatReceived?.Invoke(beat);

			var sample = _hrv.AddBeat(
				beat,
				_baseline.BaselineRmssd,
				_baseline.BaselineHr,
				_detector.State,
				_baseline.BaselineLfHfRatio);

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

			var reading = RegulationFieldCalculator.Compute(
				finalSample,
				_settings.Thresholds,
				_baseline.WarmUpProgress,
				_baseline.IsWarm);
			LatestReading = reading;
			ReadingUpdated?.Invoke(reading);

			// Velocity/trend of the arousal index and of the Hypoarousal scalar. Only fold usable
			// samples (baseline warm, sensor in contact) into the trackers; otherwise reset them so
			// the resumed stream re-seeds rather than computing a spike across the gap or off the
			// cold->warm jump. The two trackers move together so the index and collapse trajectories
			// stay phase-aligned.
			if (_baseline.IsWarm && LatestContact != SensorContactStatus.NotDetected)
			{
				_velocity.Update(reading.Index, finalSample.Timestamp);
				_hypoVelocity.Update(reading.Hypoarousal, finalSample.Timestamp);
			}
			else
			{
				_velocity.Reset();
				_hypoVelocity.Reset();
			}

			LatestDynamics = _velocity.Latest;
			DynamicsUpdated?.Invoke(LatestDynamics);

			LatestHypoarousalDynamics = _hypoVelocity.Latest;
			HypoarousalDynamicsUpdated?.Invoke(LatestHypoarousalDynamics);

			RecoveryUpdated?.Invoke(_detector.Recovery);
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
			return false;
		}

		return true;
	}

	private void OnAlertFired(AlertPayload payload)
	{
		_repository.InsertAlert(payload);
		AlertFired?.Invoke(payload);
	}

	private void OnStateChanged(DetectorState state) => StateChanged?.Invoke(state);

	public void Dispose()
	{
		_cts.Cancel();
		_cts.Dispose();
	}
}
