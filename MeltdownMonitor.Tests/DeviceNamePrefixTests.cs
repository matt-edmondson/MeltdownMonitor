using MeltdownMonitor.Core.Beats;

namespace MeltdownMonitor.Tests;

[TestClass]
public class DeviceNamePrefixTests
{
	[TestMethod]
	public void Auto_HasNoPrefix()
	{
		Assert.IsNull(DeviceNamePrefix.For(HeartRateDeviceType.Auto));
	}

	[TestMethod]
	[DataRow(HeartRateDeviceType.H10, "Polar H10")]
	[DataRow(HeartRateDeviceType.VeritySense, "Polar Sense")]
	[DataRow(HeartRateDeviceType.GarminHrmDual, "HRM-Dual")]
	[DataRow(HeartRateDeviceType.GarminHrmPro, "HRM-Pro")]
	public void KnownDevices_MapToTheirAdvertisedPrefix(HeartRateDeviceType type, string expected)
	{
		Assert.AreEqual(expected, DeviceNamePrefix.For(type));
	}

	[TestMethod]
	public void EveryNonAutoDevice_HasAPrefix()
	{
		foreach (HeartRateDeviceType type in Enum.GetValues<HeartRateDeviceType>())
		{
			if (type == HeartRateDeviceType.Auto)
			{
				continue;
			}

			Assert.IsNotNull(
				DeviceNamePrefix.For(type),
				$"{type} must have a scan name prefix so it can be pinned.");
		}
	}

	[TestMethod]
	public void HrmProPrefix_AlsoMatchesProPlusAdvertisedName()
	{
		// The Pro Plus advertises "HRM-Pro+:xxxxxxx"; the substring match used by the
		// BLE sources must still accept it under the GarminHrmPro prefix.
		string? prefix = DeviceNamePrefix.For(HeartRateDeviceType.GarminHrmPro);
		Assert.IsNotNull(prefix);
		Assert.IsTrue("HRM-Pro+:1234567".Contains(prefix, StringComparison.OrdinalIgnoreCase));
	}
}
