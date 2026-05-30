using MeltdownMonitor.Core.Beats;

namespace MeltdownMonitor.Tests;

[TestClass]
public class HrMeasurementParserTests
{
	// Flags=0x00: HR uint8, no energy, no RR
	[TestMethod]
	public void Parse_Hr8Bit_NoRr()
	{
		byte[] payload = [0x00, 72];
		var result = HrMeasurementParser.Parse(payload);
		Assert.AreEqual(72, result.HeartRateBpm);
		Assert.AreEqual(0, result.RrIntervals.Count);
	}

	// Flags=0x01: HR uint16, no energy, no RR
	[TestMethod]
	public void Parse_Hr16Bit_NoRr()
	{
		byte[] payload = [0x01, 0x50, 0x00]; // 0x0050 = 80 bpm
		var result = HrMeasurementParser.Parse(payload);
		Assert.AreEqual(80, result.HeartRateBpm);
		Assert.AreEqual(0, result.RrIntervals.Count);
	}

	// Flags=0x10: HR uint8, RR present (single RR)
	[TestMethod]
	public void Parse_Hr8Bit_SingleRr()
	{
		// RR raw = 800 (0x0320) → 800 * 1000/1024 ≈ 781.25 ms
		byte[] payload = [0x10, 65, 0x20, 0x03];
		var result = HrMeasurementParser.Parse(payload);
		Assert.AreEqual(65, result.HeartRateBpm);
		Assert.AreEqual(1, result.RrIntervals.Count);
		Assert.AreEqual(800.0 * 1000.0 / 1024.0, result.RrIntervals[0], 0.001);
	}

	// Flags=0x10: HR uint8, RR present (two RR intervals in one notification)
	[TestMethod]
	public void Parse_Hr8Bit_MultiRr()
	{
		// Two RR values: raw 800 and raw 820
		byte[] payload = [0x10, 70, 0x20, 0x03, 0x34, 0x03];
		var result = HrMeasurementParser.Parse(payload);
		Assert.AreEqual(70, result.HeartRateBpm);
		Assert.AreEqual(2, result.RrIntervals.Count);
		Assert.AreEqual(800.0 * 1000.0 / 1024.0, result.RrIntervals[0], 0.001);
		Assert.AreEqual(820.0 * 1000.0 / 1024.0, result.RrIntervals[1], 0.001);
	}

	// Flags=0x19: HR uint16, energy present, RR present
	[TestMethod]
	public void Parse_Hr16Bit_EnergyAndRr()
	{
		// flags=0x19 = 0b00011001: hr16, energy, rr
		// HR = 0x0060 = 96 bpm
		// Energy = 0x0064 = 100 kJ (skipped)
		// RR raw = 0x0300 = 768 → 750 ms
		byte[] payload = [0x19, 0x60, 0x00, 0x64, 0x00, 0x00, 0x03];
		var result = HrMeasurementParser.Parse(payload);
		Assert.AreEqual(96, result.HeartRateBpm);
		Assert.AreEqual(1, result.RrIntervals.Count);
		Assert.AreEqual(768.0 * 1000.0 / 1024.0, result.RrIntervals[0], 0.001);
	}

	// Sensor contact bits (bits 1-2) must not disturb HR/RR parsing.
	[TestMethod]
	public void Parse_SensorContactBitsDoNotAffectHrOrRr()
	{
		// Flags with sensor contact bits set (0x06 = 0b00000110) but no RR, HR uint8
		byte[] payload = [0x06, 60];
		var result = HrMeasurementParser.Parse(payload);
		Assert.AreEqual(60, result.HeartRateBpm);
		Assert.AreEqual(0, result.RrIntervals.Count);
	}

	// Support bit (0x04) set + status bit (0x02) set → contact detected.
	[TestMethod]
	public void Parse_ContactSupportedAndDetected()
	{
		byte[] payload = [0x06, 60];
		var result = HrMeasurementParser.Parse(payload);
		Assert.AreEqual(SensorContactStatus.Detected, result.SensorContact);
	}

	// Support bit (0x04) set, status bit clear → supported but not in contact.
	[TestMethod]
	public void Parse_ContactSupportedNotDetected()
	{
		byte[] payload = [0x04, 60];
		var result = HrMeasurementParser.Parse(payload);
		Assert.AreEqual(SensorContactStatus.NotDetected, result.SensorContact);
	}

	// Support bit clear → contact unknown, even if the stray status bit is set.
	[TestMethod]
	public void Parse_ContactNotSupportedWhenSupportBitClear()
	{
		Assert.AreEqual(SensorContactStatus.NotSupported,
			HrMeasurementParser.Parse([0x00, 60]).SensorContact);
		Assert.AreEqual(SensorContactStatus.NotSupported,
			HrMeasurementParser.Parse([0x02, 60]).SensorContact);
	}

	[TestMethod]
	public void Parse_PayloadTooShort_Throws()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			HrMeasurementParser.Parse([0x00]));
	}
}
