namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// Maps a <see cref="HeartRateDeviceType"/> to the BLE advertisement local-name
/// prefix used to pin the scan to that sensor. Matching is a case-insensitive
/// substring search, so a prefix such as "HRM-Pro" also matches "HRM-Pro+".
///
/// Lives in Core so the mapping is unit-testable; the per-platform BLE sources
/// (which Tests cannot reference) call into it rather than each keeping their own
/// copy of the table.
/// </summary>
public static class DeviceNamePrefix
{
	private static readonly IReadOnlyDictionary<HeartRateDeviceType, string> Prefixes =
		new Dictionary<HeartRateDeviceType, string>
		{
			[HeartRateDeviceType.H10] = "Polar H10",
			[HeartRateDeviceType.VeritySense] = "Polar Sense",
			[HeartRateDeviceType.GarminHrmDual] = "HRM-Dual",
			[HeartRateDeviceType.GarminHrmPro] = "HRM-Pro",
		};

	/// <summary>
	/// The advertisement name prefix to filter on, or <c>null</c> for
	/// <see cref="HeartRateDeviceType.Auto"/> (accept any HR-service device).
	/// </summary>
	public static string? For(HeartRateDeviceType deviceType) =>
		Prefixes.TryGetValue(deviceType, out string? prefix) ? prefix : null;
}
