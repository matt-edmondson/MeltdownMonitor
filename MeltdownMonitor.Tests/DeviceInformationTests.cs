using MeltdownMonitor.Core.Beats;

namespace MeltdownMonitor.Tests;

[TestClass]
public class DeviceInformationTests
{
	[TestMethod]
	public void Summary_CombinesModelAndFirmware()
	{
		var info = new DeviceInformation(ModelNumber: "Polar H10", FirmwareRevision: "3.1.1");
		Assert.AreEqual("Polar H10 · fw 3.1.1", info.Summary);
	}

	[TestMethod]
	public void Summary_ModelOnly_OmitsFirmwarePart()
	{
		var info = new DeviceInformation(ModelNumber: "Polar H10");
		Assert.AreEqual("Polar H10", info.Summary);
	}

	[TestMethod]
	public void Summary_FallsBackToManufacturerWhenModelMissing()
	{
		var info = new DeviceInformation(ManufacturerName: "Polar Electro Oy", FirmwareRevision: "2.0");
		Assert.AreEqual("Polar Electro Oy · fw 2.0", info.Summary);
	}

	[TestMethod]
	public void Summary_TrimsWhitespaceInParts()
	{
		var info = new DeviceInformation(ModelNumber: "  Polar H10 ", FirmwareRevision: " 3.1.1 ");
		Assert.AreEqual("Polar H10 · fw 3.1.1", info.Summary);
	}

	[TestMethod]
	public void Summary_EmptyRecord_IsUnknownDevice()
	{
		Assert.AreEqual("Unknown device", new DeviceInformation().Summary);
	}
}
