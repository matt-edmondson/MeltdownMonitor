using MeltdownMonitor.Core.Beats.Polar;

namespace MeltdownMonitor.Tests;

[TestClass]
public class PmdControlPointTests
{
	[TestMethod]
	public void BuildGetSettings_IsOpCodeAndType()
	{
		CollectionAssert.AreEqual(new byte[] { 0x01, 0x02 }, PmdControlPoint.BuildGetSettings(PmdMeasurementType.Acc));
	}

	[TestMethod]
	public void BuildStop_IsOpCodeAndType()
	{
		CollectionAssert.AreEqual(new byte[] { 0x03, 0x02 }, PmdControlPoint.BuildStop(PmdMeasurementType.Acc));
	}

	[TestMethod]
	public void BuildStartPpi_TakesNoSettings()
	{
		CollectionAssert.AreEqual(new byte[] { 0x02, 0x03 }, PmdControlPoint.BuildStartPpi());
	}

	[TestMethod]
	public void BuildStartAcc_EncodesCanonicalSettings()
	{
		// op=0x02, type=ACC(0x02), then TLVs: sampleRate=50, resolution=16, range=8, channels=3.
		byte[] expected =
		[
			0x02, 0x02,
			0x00, 0x01, 0x32, 0x00,
			0x01, 0x01, 0x10, 0x00,
			0x02, 0x01, 0x08, 0x00,
			0x04, 0x01, 0x03, 0x00,
		];
		CollectionAssert.AreEqual(expected, PmdControlPoint.BuildStartAcc());
	}

	[TestMethod]
	public void BuildStartEcg_EncodesSampleRateAndResolution()
	{
		// op=0x02, type=ECG(0x00), sampleRate=130 (0x0082), resolution=14 (0x000E).
		byte[] expected =
		[
			0x02, 0x00,
			0x00, 0x01, 0x82, 0x00,
			0x01, 0x01, 0x0E, 0x00,
		];
		CollectionAssert.AreEqual(expected, PmdControlPoint.BuildStartEcg());
	}

	[TestMethod]
	public void ParseSupportedFeatures_DecodesBitmask()
	{
		// 0x0D = 0b1101 → bit0 ECG, bit2 ACC, bit3 PPI.
		var supported = PmdControlPoint.ParseSupportedFeatures([0x0F, 0x0D]);
		Assert.IsTrue(supported.Contains(PmdMeasurementType.Ecg));
		Assert.IsTrue(supported.Contains(PmdMeasurementType.Acc));
		Assert.IsTrue(supported.Contains(PmdMeasurementType.Ppi));
		Assert.IsFalse(supported.Contains(PmdMeasurementType.Ppg));
	}

	[TestMethod]
	public void ParseSupportedFeatures_RejectsNonFeatureResponse()
	{
		Assert.AreEqual(0, PmdControlPoint.ParseSupportedFeatures([0xF0, 0x0D]).Count);
		Assert.AreEqual(0, PmdControlPoint.ParseSupportedFeatures([0x0F]).Count);
	}

	[TestMethod]
	public void ParseResponse_DecodesSuccess()
	{
		var response = PmdControlPoint.ParseResponse([0xF0, 0x02, 0x02, 0x00]);
		Assert.IsNotNull(response);
		Assert.AreEqual(PmdControlOpCode.RequestMeasurementStart, response.OpCode);
		Assert.AreEqual(PmdMeasurementType.Acc, response.MeasurementType);
		Assert.IsTrue(response.IsSuccess);
	}

	[TestMethod]
	public void ParseResponse_DecodesError()
	{
		var response = PmdControlPoint.ParseResponse([0xF0, 0x02, 0x02, 0x05]);
		Assert.IsNotNull(response);
		Assert.IsFalse(response.IsSuccess);
	}

	[TestMethod]
	public void ParseResponse_RejectsNonResponse()
	{
		Assert.IsNull(PmdControlPoint.ParseResponse([0x0F, 0x02, 0x02, 0x00]));
	}
}
