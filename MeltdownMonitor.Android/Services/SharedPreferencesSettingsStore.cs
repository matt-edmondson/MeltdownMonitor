using Android.Content;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.Android.Services;

/// <summary>
/// Persists the full <see cref="MobileSettings"/> as a single JSON blob in
/// <see cref="ISharedPreferences"/> — the Android counterpart to the iOS
/// <c>NSUserDefaultsSettingsStore</c> (design doc §6 / §8). One key means there
/// is only ever one value to migrate, and the JSON shape is owned by the
/// platform-neutral <see cref="MobileSettingsSerializer"/> so it stays
/// unit-testable off-device.
/// </summary>
public sealed class SharedPreferencesSettingsStore : IMobileSettingsStore
{
	private const string PreferencesName = "com.matthewedmondson.meltdownmonitor.settings";
	private const string SettingsKey = "settings";

	private readonly ISharedPreferences _preferences;

	public SharedPreferencesSettingsStore(Context context)
	{
		ArgumentNullException.ThrowIfNull(context);
		_preferences = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)
			?? throw new InvalidOperationException("Could not open settings SharedPreferences.");
	}

	public MobileSettings Load()
	{
		string? json = _preferences.GetString(SettingsKey, null);
		return MobileSettingsSerializer.Deserialize(json);
	}

	public void Save(MobileSettings settings)
	{
		_preferences.Edit()!
			.PutString(SettingsKey, MobileSettingsSerializer.Serialize(settings))!
			.Apply();
	}
}
