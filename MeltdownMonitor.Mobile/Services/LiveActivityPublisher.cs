using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// Bridges the live <see cref="Pipeline"/> to an <see cref="ILiveActivityController"/>
/// (design doc Phase 8). Starts the activity on the first event while the
/// feature is enabled, mirrors the in-app state pill on every state change, and
/// throttles the frequent sample updates to ≤ 1 Hz so we stay inside Apple's
/// budget for background activity refreshes. State transitions bypass the
/// throttle — the Lock Screen colour should flip the instant the state does.
///
/// Platform-neutral and fully unit-testable: the controller and clock are
/// injected, and controller calls are fire-and-forget so a slow or failing
/// ActivityKit request can never stall the BLE pipeline.
/// </summary>
public sealed class LiveActivityPublisher : IDisposable
{
	private static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(1);

	private readonly Pipeline _pipeline;
	private readonly ILiveActivityController _controller;
	private readonly MobileSettings _settings;
	private readonly Func<DateTimeOffset> _clock;
	private readonly TimeSpan _minInterval;

	private readonly object _gate = new();
	private DateTimeOffset _lastEmit = DateTimeOffset.MinValue;
	private bool _started;
	private bool _disposed;

	public LiveActivityPublisher(
		Pipeline pipeline,
		ILiveActivityController controller,
		MobileSettings settings,
		TimeSpan? minUpdateInterval = null,
		Func<DateTimeOffset>? clock = null)
	{
		ArgumentNullException.ThrowIfNull(pipeline);
		ArgumentNullException.ThrowIfNull(controller);
		ArgumentNullException.ThrowIfNull(settings);

		_pipeline = pipeline;
		_controller = controller;
		_settings = settings;
		_minInterval = minUpdateInterval ?? DefaultMinInterval;
		_clock = clock ?? (() => DateTimeOffset.UtcNow);

		_pipeline.SampleUpdated += OnSampleUpdated;
		_pipeline.StateChanged += OnStateChanged;
	}

	private void OnSampleUpdated(HrvSample sample) =>
		Publish(BuildContent(sample, sample.State), force: false);

	private void OnStateChanged(DetectorState state) =>
		// Rare and important — bypass the throttle. Use the latest sample for the
		// numeric fields; before any sample arrives the content is state-only.
		Publish(BuildContent(_pipeline.LatestSample, state), force: true);

	private LiveActivityContent BuildContent(HrvSample? sample, DetectorState state)
	{
		bool paused = IsPaused();
		double rmssd = sample?.Rmssd ?? 0;
		double baseline = sample?.BaselineRmssd ?? 0;
		double ratio = baseline > 0 ? rmssd / baseline : 1.0;
		int hr = sample is { MeanHr: > 0 } ? (int)Math.Round(sample.MeanHr) : 0;

		return new LiveActivityContent(
			state,
			StateColors.LabelFor(state, paused),
			StateColors.HexFor(state, paused),
			hr,
			ratio,
			paused);
	}

	private bool IsPaused() =>
		_settings.PausedUntil is { } until && _clock() < until;

	private void Publish(LiveActivityContent content, bool force)
	{
		if (_disposed)
		{
			return;
		}

		lock (_gate)
		{
			// Honour the opt-in (design doc §4.5 — Live Activity is opt-in). If it
			// was switched off while running, tear the activity down on next event.
			if (!_settings.EnableLiveActivity)
			{
				if (_started)
				{
					_started = false;
					Invoke(_controller.EndAsync);
				}

				return;
			}

			var now = _clock();
			if (!_started)
			{
				_started = true;
				_lastEmit = now;
				Invoke(() => _controller.StartAsync(content));
				return;
			}

			if (!force && now - _lastEmit < _minInterval)
			{
				return; // throttled
			}

			_lastEmit = now;
			Invoke(() => _controller.UpdateAsync(content));
		}
	}

	/// <summary>
	/// Ends the activity and awaits the controller — used on graceful shutdown
	/// (<c>WillTerminate</c>) so the Lock Screen doesn't keep a stale card.
	/// </summary>
	public async Task StopAsync()
	{
		bool wasStarted;
		lock (_gate)
		{
			wasStarted = _started;
			_started = false;
		}

		if (wasStarted)
		{
			try
			{
				await _controller.EndAsync().ConfigureAwait(false);
			}
			catch
			{
				// A failed dismissal is not worth surfacing on the terminate path.
			}
		}
	}

	private static void Invoke(Func<Task> action)
	{
		try
		{
			_ = action();
		}
		catch
		{
			// A Live Activity is a nicety; never let one break the pipeline.
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_pipeline.SampleUpdated -= OnSampleUpdated;
		_pipeline.StateChanged -= OnStateChanged;

		if (_started)
		{
			_started = false;
			Invoke(_controller.EndAsync);
		}
	}
}
