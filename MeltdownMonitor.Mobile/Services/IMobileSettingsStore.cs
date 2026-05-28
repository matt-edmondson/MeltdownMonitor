namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// Platform facade for persisting the small bits of state that need to
/// survive a relaunch before the SQLite repository is open — currently just
/// the first-run disclaimer flag. On iOS the implementation is a thin
/// wrapper over <c>NSUserDefaults</c>; on desktop hosts a JSON file under
/// app data is the equivalent.
/// </summary>
public interface IMobileSettingsStore
{
	bool LoadDisclaimerAccepted();

	void SaveDisclaimerAccepted(bool accepted);
}
