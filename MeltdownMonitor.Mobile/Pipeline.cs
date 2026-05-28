using MeltdownMonitor.Core.Baseline;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;

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

	private CancellationTokenSource _cts = new();
	private Task? _runTask;

	public DetectorState CurrentState => _detector.State;
	public HrvSample? LatestSample { get; private set; }

	public event Action<AlertPayload>? AlertFired;
	public event Action<HrvSample>? SampleUpdated;
	public event Action<DetectorState>? StateChanged;

	public Pipeline(MobileSettings settings, MeltdownRepository repository, IBeatSource source)
	{
		_settings = settings;
		_repository = repository;
		_source = source;
		_detector = new DysregulationDetector(settings.Thresholds);
		_detector.AlertFired += OnAlertFired;
		_detector.StateChanged += OnStateChanged;
	}

	public void Start()
	{
		_cts = new CancellationTokenSource();
		_runTask = RunAsync(_cts.Token);
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
