using Foundation;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.iOS.Services;

/// <summary>
/// Persists the bits of <see cref="MeltdownMonitor.Mobile.MobileSettings"/>
/// that have to survive before the SQLite repository is open — currently
/// just the first-run disclaimer flag. Backed by <c>NSUserDefaults</c>
/// (design doc §13(2)).
/// </summary>
public sealed class NSUserDefaultsSettingsStore : IMobileSettingsStore
{
	private const string DisclaimerKey = "com.thethreethousands.meltdownmonitor.disclaimerAccepted";

	public bool LoadDisclaimerAccepted() =>
		NSUserDefaults.StandardUserDefaults.BoolForKey(DisclaimerKey);

	public void SaveDisclaimerAccepted(bool accepted)
	{
		NSUserDefaults.StandardUserDefaults.SetBool(accepted, DisclaimerKey);
	}
}
