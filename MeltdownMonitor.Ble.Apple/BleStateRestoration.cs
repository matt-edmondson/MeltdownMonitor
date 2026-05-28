using Foundation;

namespace MeltdownMonitor.Ble.Apple;

/// <summary>
/// Persists the identifier of the last-connected BLE peripheral in
/// <see cref="NSUserDefaults"/> so the central manager can re-attach after
/// iOS suspends and re-launches the app via state restoration
/// (design doc §4.1, §7).
/// </summary>
public sealed class BleStateRestoration
{
	private const string DefaultKey = "MeltdownMonitor.Ble.Apple.PeripheralIdentifier";

	private readonly string _key;
	private readonly NSUserDefaults _defaults;

	public BleStateRestoration(string? key = null, NSUserDefaults? defaults = null)
	{
		_key = key ?? DefaultKey;
		_defaults = defaults ?? NSUserDefaults.StandardUserDefaults;
	}

	public Guid? LoadPeripheralIdentifier()
	{
		string? stored = _defaults.StringForKey(_key);
		if (string.IsNullOrEmpty(stored) || !Guid.TryParse(stored, out var id))
		{
			return null;
		}

		return id;
	}

	public void SavePeripheralIdentifier(Guid id)
	{
		_defaults.SetString(id.ToString(), _key);
	}

	public void Clear()
	{
		_defaults.RemoveObject(_key);
	}
}
