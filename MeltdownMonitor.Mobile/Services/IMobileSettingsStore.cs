namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// Platform facade for persisting <see cref="MobileSettings"/> across
/// relaunches. On iOS the implementation is a thin wrapper over
/// <c>NSUserDefaults</c>; on desktop hosts a JSON file under app data is the
/// equivalent.
///
/// The disclaimer flag has its own accessor because it must be readable
/// before the rest of the app is composed (the disclaimer screen blocks the
/// HealthKit ask, design doc §4.4) — so callers can fetch just that bit
/// without paying for a full deserialize on first launch.
/// </summary>
public interface IMobileSettingsStore
{
	bool LoadDisclaimerAccepted();

	void SaveDisclaimerAccepted(bool accepted);

	/// <summary>
	/// Hydrate every persisted field of <see cref="MobileSettings"/>.
	/// Returns a fresh settings object with defaults applied if nothing has
	/// been saved yet, so callers can use it unconditionally on first launch.
	/// </summary>
	MobileSettings LoadSettings();

	/// <summary>
	/// Persist every field of <see cref="MobileSettings"/>. Idempotent and
	/// safe to call from any thread; intended to be invoked from
	/// <c>SettingsViewModel</c> property setters on every user-driven change.
	/// </summary>
	void SaveSettings(MobileSettings settings);
}
