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

	private CancellationTokenSource _cts = new();
	private Task? _pipelineTask;

	public DetectorState CurrentState => _detector.State;
	public DetectionThresholds LatestThresholds => _settings.Thresholds;
	public HrvSample? LatestSample { get; private set; }
	public BaselineHrvTracker Baseline => _baseline;

	/// <summary>Latest arousal-vs-baseline reading driving the Regulation Field overlay.</summary>
	public RegulationReading LatestReading { get; private set; } = new(0.0, 1.0, 0.0);

	public event Action<AlertPayload>? AlertFired;
	public event Action<HrvSample>? SampleUpdated;
	public event Action<Beat>? BeatReceived;

	/// <summary>Fires after <see cref="SampleUpdated"/> with the recomputed Regulation Field reading.</summary>
	public event Action<RegulationReading>? ReadingUpdated;

	public Pipeline(AppSettings settings, MeltdownRepository repository)
	{
		_settings = settings;
		_repository = repository;
		_detector = new DysregulationDetector(() => _settings.Thresholds);
		_detector.AlertFired += OnAlertFired;
	}

	public void Start()
	{
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
		_pipelineTask?.GetAwaiter().GetResult();
	}

	private async Task RunAsync(CancellationToken cancellationToken)
	{
		var source = new PolarHrSource(_settings.DeviceType);

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

			_baseline.Update(sample);

			var state = _detector.Process(sample, _baseline.IsWarm);

			var finalSample = sample with
			{
				BaselineRmssd = _baseline.BaselineRmssd,
				BaselineHr = _baseline.BaselineHr,
				State = state,
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

	public void Dispose()
	{
		_cts.Dispose();
		_repository.Dispose();
	}
}
