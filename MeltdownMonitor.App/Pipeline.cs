using MeltdownMonitor.Ble.Windows;
using MeltdownMonitor.Core.Baseline;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;

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
	public HrvSample? LatestSample { get; private set; }
	public BaselineHrvTracker Baseline => _baseline;
	public event Action<AlertPayload>? AlertFired;
	public event Action<HrvSample>? SampleUpdated;
	public event Action<Beat>? BeatReceived;

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
		try
		{
			DateTimeOffset to = DateTimeOffset.UtcNow;
			DateTimeOffset from = to.AddDays(-BaselineHrvTracker.AnchorWindowDays);
			var history = MeltdownRepository.ReadHistory(_settings.DatabasePath, from, to);
			_baseline.SeedFromHistory(history);
		}
		catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException
			or IOException or InvalidOperationException)
		{
			System.Diagnostics.Debug.WriteLine($"Baseline seeding skipped: {ex.Message}");
		}
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

			_hrv.EmitIntervalSeconds = _settings.HrvEmitIntervalSeconds;
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
