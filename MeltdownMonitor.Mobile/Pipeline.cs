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
	private readonly RegulationVelocityTracker _velocity = new();

	private CancellationTokenSource _cts = new();
	private Task? _runTask;

	public DetectorState CurrentState => _detector.State;
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

	public event Action<AlertPayload>? AlertFired;
	public event Action<HrvSample>? SampleUpdated;
	public event Action<DetectorState>? StateChanged;

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

	/// <summary>Fires after <see cref="SampleUpdated"/> with the Regulation Field
	/// reading derived from the same sample, so the Now screen can drive the
	/// field without recomputing the calculator inputs itself.</summary>
	public event Action<RegulationReading>? ReadingUpdated;

	/// <summary>Fires after <see cref="ReadingUpdated"/> with the velocity/trend of the
	/// arousal index, derived from the same sample. Steady while calibrating or off-contact.</summary>
	public event Action<RegulationDynamics>? DynamicsUpdated;

	public Pipeline(MobileSettings settings, MeltdownRepository repository, IBeatSource source)
	{
		_settings = settings;
		_repository = repository;
		_source = source;
		_detector = new DysregulationDetector(() => settings.Thresholds);
		_detector.AlertFired += OnAlertFired;
		_detector.StateChanged += OnStateChanged;

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

	/// <summary>
	/// Seeds <see cref="BaselineHrvTracker"/> from a HealthKit history pull
	/// (design doc §8) so the user lands in <c>Watching</c> rather than
	/// <c>Idle</c> on first launch. HealthKit HR samples are sparse and lack
	/// beat-to-beat RR detail, so RMSSD is approximated from the
	/// inter-sample variation that <see cref="ShortWindowHrvCalculator"/>
	/// already knows how to compute — close enough to break the cold start;
	/// the live EWMA refines it within minutes once real beats arrive.
	/// Safe to call when <paramref name="healthStore"/> is null (no-op) or
	/// HealthKit returns nothing (auth not granted, no Apple Watch, etc.).
	/// Must run before <see cref="Start"/>.
	/// </summary>
	public async Task WarmStartAsync(
		IHealthStore? healthStore,
		TimeSpan? lookback = null,
		CancellationToken cancellationToken = default)
	{
		if (healthStore is null)
		{
			return;
		}

		var window = lookback ?? TimeSpan.FromHours(24);
		var warmCalculator = new ShortWindowHrvCalculator();

		await foreach (var hr in healthStore.ReadRecentHeartRateAsync(window).WithCancellation(cancellationToken).ConfigureAwait(false))
		{
			if (hr.HeartRateBpm <= 0)
			{
				continue;
			}

			int bpm = (int)Math.Round(hr.HeartRateBpm);
			double rrMs = 60_000.0 / hr.HeartRateBpm;
			var beat = new Beat(hr.Timestamp, rrMs, bpm, IsArtifact: false);

			var sample = warmCalculator.AddBeat(
				beat,
				_baseline.BaselineRmssd,
				_baseline.BaselineHr,
				DetectorState.Idle,
				_baseline.BaselineLfHfRatio);

			if (sample is not null)
			{
				_baseline.Update(sample);
			}
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

			_baseline.Update(sample, LatestContact);

			var state = _detector.Process(sample, _baseline.IsWarm, LatestContact);

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

			// Velocity/trend of the arousal index. Only fold usable samples (baseline warm,
			// sensor in contact) into the tracker; otherwise reset it so the resumed stream
			// re-seeds rather than computing a spike across the gap or off the cold->warm jump.
			if (_baseline.IsWarm && LatestContact != SensorContactStatus.NotDetected)
			{
				_velocity.Update(reading.Index, finalSample.Timestamp);
			}
			else
			{
				_velocity.Reset();
			}

			LatestDynamics = _velocity.Latest;
			DynamicsUpdated?.Invoke(LatestDynamics);
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
