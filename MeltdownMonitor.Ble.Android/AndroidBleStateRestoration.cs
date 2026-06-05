using Android.Content;

namespace MeltdownMonitor.Ble.Android;

/// <summary>
/// Persists the address of the last-connected BLE device in
/// <see cref="ISharedPreferences"/> so the source can reconnect by address
/// instead of scanning afresh on every launch (design doc §7). The Android
/// analog of <c>BleStateRestoration</c> on the Apple head — Android has no
/// CoreBluetooth state restoration, but a remembered MAC address is enough to
/// re-open the GATT connection with <c>autoConnect: true</c>.
/// </summary>
public sealed class AndroidBleStateRestoration
{
	private const string PreferencesName = "MeltdownMonitor.Ble.Android";
	private const string AddressKey = "LastDeviceAddress";

	private readonly ISharedPreferences _preferences;

	public AndroidBleStateRestoration(Context context)
	{
		ArgumentNullException.ThrowIfNull(context);

		// MODE_PRIVATE — readable only by this app.
		_preferences = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)
			?? throw new InvalidOperationException("Could not open BLE state SharedPreferences.");
	}

	/// <summary>The MAC address of the last device we connected to, or null if none.</summary>
	public string? LoadDeviceAddress()
	{
		string? stored = _preferences.GetString(AddressKey, null);
		return string.IsNullOrEmpty(stored) ? null : stored;
	}

	public void SaveDeviceAddress(string address)
	{
		if (string.IsNullOrEmpty(address))
		{
			return;
		}

		_preferences.Edit()!.PutString(AddressKey, address)!.Apply();
	}

	public void Clear() => _preferences.Edit()!.Remove(AddressKey)!.Apply();
}
