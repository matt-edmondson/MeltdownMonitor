namespace MeltdownMonitor.Core.Detection;

/// <summary>
/// Which edge of the window of tolerance an alert is about, so dispatchers can respond
/// appropriately. Default <see cref="Hyperarousal"/> keeps every existing call site and the
/// persisted alert history unchanged.
/// </summary>
public enum AlertKind
{
	/// <summary>
	/// Sympathetic over-activation — the meltdown/dysregulation alert raised by
	/// <see cref="DysregulationDetector"/>. The original and default alert kind.
	/// </summary>
	Hyperarousal,

	/// <summary>
	/// Low-arousal collapse/shutdown raised by <see cref="HypoarousalDetector"/>. Should be routed
	/// <i>gently</i> — a jarring stimulus can deepen a shutdown (sensory overload), so dispatchers
	/// pick a softer, non-interrupting cue for this kind.
	/// </summary>
	Hypoarousal,
}
