using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// Platform-neutral seam over a Lock Screen / Dynamic Island Live Activity
/// (design doc §4.5 / Phase 8). The iOS head implements this against
/// ActivityKit; other hosts (desktop, design-time) get a no-op. Keeping the
/// contract here means <see cref="LiveActivityPublisher"/> — and its tests —
/// never reference UIKit or ActivityKit.
/// </summary>
public interface ILiveActivityController
{
	/// <summary>True once an activity has been requested and not yet ended.</summary>
	bool IsActive { get; }

	/// <summary>Begin a new Live Activity showing the supplied content.</summary>
	Task StartAsync(LiveActivityContent content);

	/// <summary>Push fresh content to the running activity.</summary>
	Task UpdateAsync(LiveActivityContent content);

	/// <summary>Dismiss the activity (app paused, monitoring off, or terminating).</summary>
	Task EndAsync();
}

/// <summary>
/// Immutable snapshot handed to the Live Activity each update. Mirrors the
/// in-app state pill (design doc §6): the same state label and palette colour,
/// the current heart rate, and the RMSSD-vs-baseline ratio that drives the
/// Dynamic Island glyph. The colour is pre-resolved to a hex string so the
/// SwiftUI presentation layer reuses the C# palette rather than duplicating it.
/// </summary>
/// <param name="State">Current detector state.</param>
/// <param name="StateLabel">User-facing label ("Watching", "Paused", …).</param>
/// <param name="ColorHex">State colour as <c>#RRGGBB</c> (design doc §12 palette).</param>
/// <param name="HeartRate">Current HR in bpm; 0 when not yet known.</param>
/// <param name="RmssdRatio">RMSSD ÷ baseline; 1.0 = at baseline, &lt;1 = suppressed.</param>
/// <param name="IsPaused">True while monitoring is paused.</param>
public readonly record struct LiveActivityContent(
	DetectorState State,
	string StateLabel,
	string ColorHex,
	int HeartRate,
	double RmssdRatio,
	bool IsPaused);
