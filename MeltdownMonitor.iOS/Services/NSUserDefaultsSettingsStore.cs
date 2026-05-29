using Foundation;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.iOS.Services;

/// <summary>
/// Persists the full <see cref="MobileSettings"/> as a single JSON blob in
/// <c>NSUserDefaults</c> (design doc §6.4 / §13(2)). One key means there is
/// only ever one value to migrate, and the JSON shape is owned by
/// <see cref="MobileSettingsSerializer"/> in the platform-neutral assembly so
/// it can be unit-tested off-device.
/// </summary>
public sealed class NSUserDefaultsSettingsStore : IMobileSettingsStore
{
	private const string SettingsKey = "com.thethreethousands.meltdownmonitor.settings";

	private readonly NSUserDefaults _defaults;

	public NSUserDefaultsSettingsStore(NSUserDefaults? defaults = null)
	{
		_defaults = defaults ?? NSUserDefaults.StandardUserDefaults;
	}

	public MobileSettings Load()
	{
		string? json = _defaults.StringForKey(SettingsKey);
		return MobileSettingsSerializer.Deserialize(json);
	}

	public void Save(MobileSettings settings)
	{
		_defaults.SetString(MobileSettingsSerializer.Serialize(settings), SettingsKey);
	}
}
