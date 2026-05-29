namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// Platform facade for persisting <see cref="MobileSettings"/> across
/// relaunches, independently of the SQLite repository (which holds the
/// time-series, not the user's preferences). On iOS the implementation is a
/// thin wrapper over <c>NSUserDefaults</c> storing a single JSON blob
/// (design doc §6.4); on desktop hosts a JSON file under app data is the
/// equivalent.
/// </summary>
public interface IMobileSettingsStore
{
	/// <summary>
	/// Loads the persisted settings, or a fresh default <see cref="MobileSettings"/>
	/// when nothing has been saved yet.
	/// </summary>
	MobileSettings Load();

	/// <summary>Persists the full settings object, overwriting any prior state.</summary>
	void Save(MobileSettings settings);
}
