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
	/// When true, dysregulation episodes are written back to HealthKit as
	/// "Mind &amp; Body" wellness annotations (design doc §6.3). Default off —
	/// Apple's wellness rules mean health write-back is strictly opt-in.
	/// </summary>
	public bool WriteEpisodesToHealthKit { get; set; }

	/// <summary>
	/// CoreBluetooth identifier of the last-connected sensor, persisted so the
	/// app can reconnect without a fresh scan on relaunch (design doc §4.1 /
	/// §6.4). Null until the first successful connection.
	/// </summary>
	public string? PeripheralIdentifier { get; set; }

	/// <summary>
	/// True once the user has acknowledged the first-run disclaimer. Gates the
	/// rest of the app (and any HealthKit ask) per design doc §4.4. Persisted
	/// by the platform head — on iOS that means <c>NSUserDefaults</c>.
	/// </summary>
	public bool IsDisclaimerAccepted { get; set; }

	/// <summary>
	/// When true, a Lock Screen / Dynamic Island Live Activity mirrors the
	/// current state, HR, and RMSSD-vs-baseline ratio (design doc §4.5 /
	/// Phase 8). Opt-in: a persistent on-device status surface is a deliberate
	/// choice, and the activity is the closest mobile analogue to the desktop
	/// tray icon.
	/// </summary>
	public bool EnableLiveActivity { get; set; }

	/// <summary>Number of recent readings drawn as the Regulation Field comet trail
	/// (12–240; clamped at the consumer). Default 48 ≈ 4 min at the 5 s emit cadence.</summary>
	public int RegulationTrailLength { get; set; } = 48;
}
