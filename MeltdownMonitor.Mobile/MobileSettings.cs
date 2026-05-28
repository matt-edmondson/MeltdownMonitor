using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.Mobile;

/// <summary>
/// Settings surface used by the mobile <see cref="Pipeline"/>. Persistence is
/// deferred to the platform head; on iOS a thin layer over <c>NSUserDefaults</c>
/// is the likely backing store (design doc §13(2)). For Phase 2 the type is a
/// plain POCO so the pipeline can be exercised without an on-disk store.
/// </summary>
public sealed class MobileSettings
{
	public DetectionThresholds Thresholds { get; set; } = new();

	public PolarDeviceType DeviceType { get; set; } = PolarDeviceType.Auto;

	/// <summary>When set, monitoring is paused until this UTC time.</summary>
	public DateTimeOffset? PausedUntil { get; set; }

	public bool EnableChime { get; set; } = true;

	public bool EnableNotifications { get; set; } = true;

	public string AlertSuggestion { get; set; } =
		"Step away. Five minutes. Find something quiet.";

	/// <summary>
	/// True once the user has acknowledged the first-run disclaimer. Gates the
	/// rest of the app (and any HealthKit ask) per design doc §4.4. Persisted
	/// by the platform head — on iOS that means <c>NSUserDefaults</c>.
	/// </summary>
	public bool IsDisclaimerAccepted { get; set; }
}
