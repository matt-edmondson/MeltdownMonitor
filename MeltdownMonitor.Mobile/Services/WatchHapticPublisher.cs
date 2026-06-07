using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// Bridges the live <see cref="Pipeline"/> to an <see cref="IWatchSession"/>
/// (design doc <c>docs/watch-haptics.md</c> §6), the somatic-output peer of
/// <see cref="LiveActivityPublisher"/>. It resolves each reading into a felt
/// Regulation Field via the pure <see cref="WatchHapticPlanner"/>, pushes the
/// coalesced state to the watch throttled to ≤ 1 Hz, and fires discrete cues on
/// state transitions (bypassing the throttle). The continuous output is silent
/// while paused, off-skin, or the baseline is cold — a cue the app isn't sure
/// about is worse than none (§2).
///
/// Platform-neutral and fully unit-testable: the session and clock are injected,
/// and every call is fire-and-forget so a slow or unreachable watch can never
/// stall the BLE pipeline.
/// </summary>
public sealed class WatchHapticPublisher : IDisposable
{
	private static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(1);

	private readonly Pipeline _pipeline;
	private readonly IWatchSession _session;
	private readonly MobileSettings _settings;
	private readonly Func<DateTimeOffset> _clock;
	private readonly TimeSpan _minInterval;

	private readonly object _gate = new();
	private DateTimeOffset _lastEmit = DateTimeOffset.MinValue;
	private RegulationReading _latestReading;
	private DetectorState _latestState;
	private bool _started;
	private bool _disposed;

	public WatchHapticPublisher(
		Pipeline pipeline,
		IWatchSession session,
		MobileSettings settings,
		TimeSpan? minUpdateInterval = null,
		Func<DateTimeOffset>? clock = null)
	{
		ArgumentNullException.ThrowIfNull(pipeline);
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(settings);

		_pipeline = pipeline;
		_session = session;
		_settings = settings;
		_minInterval = minUpdateInterval ?? DefaultMinInterval;
		_clock = clock ?? (() => DateTimeOffset.UtcNow);

		_latestReading = pipeline.LatestReading;
		_latestState = pipeline.CurrentState;

		_pipeline.ReadingUpdated += OnReadingUpdated;
		_pipeline.StateChanged += OnStateChanged;
	}

	private void OnReadingUpdated(RegulationReading reading)
	{
		_latestReading = reading;
		Publish(BuildState(reading, _latestState), force: false);
	}

	private void OnStateChanged(DetectorState state)
	{
		DetectorState previous;
		lock (_gate)
		{
			previous = _latestState;
			_latestState = state;
		}

		// Discrete cue first (rare, important) — must-arrive, bypasses the throttle.
		// Gated on the opt-in so a disabled feature stays silent.
		if (_settings.EnableWatchHaptics
			&& WatchHapticPlanner.CueForTransition(previous, state, _settings.WatchHapticMode) is { } cue)
		{
			Invoke(() => _session.SendCueAsync(cue));
		}

		// The colour/intensity should track the new state immediately.
		Publish(BuildState(_latestReading, state), force: true);
	}

	private WatchHapticState BuildState(RegulationReading reading, DetectorState state)
	{
		bool paused = IsPaused();
		// NotDetected is the only "off-skin" verdict; NotSupported just means the
		// sensor doesn't report contact, which must not suppress the cue.
		bool contactOk = _pipeline.LatestContact != SensorContactStatus.NotDetected;

		var plan = WatchHapticPlanner.Plan(
			reading, state, contactOk, paused, WatchHapticOptions.From(_settings));

		return new WatchHapticState(
			state,
			StateColors.LabelFor(state, paused),
			StateColors.HexFor(state, paused),
			plan.Intensity,
			plan.BreathPeriodSeconds,
			paused);
	}

	private bool IsPaused() =>
		_settings.PausedUntil is { } until && _clock() < until;

	private void Publish(WatchHapticState state, bool force)
	{
		if (_disposed)
		{
			return;
		}

		lock (_gate)
		{
			// Honour the opt-in (§9 — somatic output is off until asked for). If it
			// was switched off while running, push one final silent state so the watch
			// stops, then go quiet.
			if (!_settings.EnableWatchHaptics)
			{
				if (_started)
				{
					_started = false;
					Invoke(() => _session.UpdateStateAsync(Silenced(state)));
				}

				return;
			}

			var now = _clock();
			if (!_started)
			{
				_started = true;
				_lastEmit = now;
				Invoke(() => _session.UpdateStateAsync(state));
				return;
			}

			if (!force && now - _lastEmit < _minInterval)
			{
				return; // throttled
			}

			_lastEmit = now;
			Invoke(() => _session.UpdateStateAsync(state));
		}
	}

	private static WatchHapticState Silenced(WatchHapticState state) =>
		state with { Intensity = 0.0 };

	/// <summary>
	/// Sends a final silent state and stops — used on graceful shutdown
	/// (<c>WillTerminate</c>) so the watch doesn't keep buzzing after the phone
	/// stops monitoring.
	/// </summary>
	public async Task StopAsync()
	{
		bool wasStarted;
		WatchHapticState last;
		lock (_gate)
		{
			wasStarted = _started;
			_started = false;
			last = Silenced(BuildState(_latestReading, _latestState));
		}

		if (wasStarted)
		{
			try
			{
				await _session.UpdateStateAsync(last).ConfigureAwait(false);
			}
			catch
			{
				// A failed final push is not worth surfacing on the terminate path.
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
			// A wrist cue is a nicety; never let one break the pipeline.
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_pipeline.ReadingUpdated -= OnReadingUpdated;
		_pipeline.StateChanged -= OnStateChanged;

		if (_started)
		{
			_started = false;
			Invoke(() => _session.UpdateStateAsync(Silenced(BuildState(_latestReading, _latestState))));
		}
	}
}
