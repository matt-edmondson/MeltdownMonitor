using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// Platform-neutral seam over the phone↔watch link (design doc
/// <c>docs/watch-haptics.md</c> §5). The iOS head implements this against
/// <c>WCSession</c> (WatchConnectivity, which — unlike ActivityKit — has managed
/// bindings, so no Swift bridge is needed on the phone). Other hosts and the
/// tests get a no-op / recording fake. Keeping the contract here means
/// <see cref="WatchHapticPublisher"/> — and its tests — never reference
/// WatchConnectivity or UIKit.
/// </summary>
public interface IWatchSession
{
	/// <summary>True when a live, low-latency message could reach the watch right
	/// now (<c>WCSession.isReachable</c>). Coalesced state still flows when false.</summary>
	bool IsReachable { get; }

	/// <summary>
	/// Push the freshest coalesced haptic state to the watch
	/// (<c>updateApplicationContext</c> — "newest value wins", overwrites,
	/// delivered when the watch next runs). The watch renders it verbatim.
	/// </summary>
	Task UpdateStateAsync(WatchHapticState state);

	/// <summary>
	/// Deliver a discrete, must-arrive cue (<c>transferUserInfo</c> — queued FIFO,
	/// arrives even if late) for an escalation or recovery transition.
	/// </summary>
	Task SendCueAsync(WatchHapticCue cue);
}

/// <summary>
/// Coalesced snapshot pushed to the watch each update. Unlike the design-doc
/// sketch (which carried the raw arousal index), this carries the <b>resolved</b>
/// haptic plan — intensity and breath period already derived by the pure,
/// unit-tested <see cref="WatchHapticPlanner"/>. That keeps the watch app a dumb
/// renderer ("play this intensity at this cadence, show this colour") and the
/// entire when/how-strong decision in tested managed code, per the doc's ethos.
/// The state colour/label come from <see cref="StateColors"/> so the palette
/// stays single-sourced.
/// </summary>
/// <param name="State">Current detector state (for the watch's colour/label).</param>
/// <param name="StateLabel">User-facing label ("Watching", "Paused", …).</param>
/// <param name="ColorHex">State colour as <c>#RRGGBB</c>.</param>
/// <param name="Intensity">Resolved haptic intensity in [0, 1]; 0 = silent.</param>
/// <param name="BreathPeriodSeconds">Paced-breath cycle length; the breathing
/// animation and the haptic swell sync to this. Always a calming cadence.</param>
/// <param name="IsPaused">True while monitoring is paused.</param>
public readonly record struct WatchHapticState(
	DetectorState State,
	string StateLabel,
	string ColorHex,
	double Intensity,
	double BreathPeriodSeconds,
	bool IsPaused);

/// <summary>
/// Discrete, must-arrive cues for state transitions (design doc §7). The watch
/// renders each as a brief, gentle tap — never a jolt (§2).
/// </summary>
public enum WatchHapticCue
{
	/// <summary>Entered Warning — a soft single tap.</summary>
	EscalatedToWarning,

	/// <summary>Entered Alerting — a soft double tap.</summary>
	EscalatedToAlerting,

	/// <summary>De-escalated back toward baseline — a gentle "release".</summary>
	Recovered,
}

/// <summary>
/// No-op <see cref="IWatchSession"/> used until the iOS <c>WCSession</c>
/// implementation lands (and on any host without a paired watch). Lets
/// <see cref="WatchHapticPublisher"/> be wired into the composition root and run
/// harmlessly, exactly like the Live Activity controller's <c>dlsym</c> no-op.
/// </summary>
public sealed class NoOpWatchSession : IWatchSession
{
	public bool IsReachable => false;

	public Task UpdateStateAsync(WatchHapticState state) => Task.CompletedTask;

	public Task SendCueAsync(WatchHapticCue cue) => Task.CompletedTask;
}
