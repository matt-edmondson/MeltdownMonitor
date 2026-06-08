using MeltdownMonitor.Core.Beats.Polar;

namespace MeltdownMonitor.Tests;

[TestClass]
public class PmdFrameParserTests
{
	// uint64 LE timestamp for 1_000_000_000 ns (= 1 second after the PMD epoch).
	private static readonly byte[] OneSecondTimestamp = [0x00, 0xCA, 0x9A, 0x3B, 0x00, 0x00, 0x00, 0x00];
	private static readonly byte[] ZeroTimestamp = [0, 0, 0, 0, 0, 0, 0, 0];

	private static byte[] Frame(byte type, byte[] timestamp, byte frameType, params byte[] data) =>
		[type, .. timestamp, frameType, .. data];

	[TestMethod]
	public void ParseHeader_DecodesTypeTimestampAndCompressedFlag()
	{
		byte[] frame = Frame(0x02, OneSecondTimestamp, 0x81);
		var header = PmdFrameParser.ParseHeader(frame);

		Assert.AreEqual(PmdMeasurementType.Acc, header.MeasurementType);
		Assert.IsTrue(header.IsCompressed);
		Assert.AreEqual(1, header.FrameType);
		Assert.AreEqual(PmdConstants.Epoch.AddSeconds(1), header.Timestamp);
		Assert.AreEqual(10, header.DataOffset);
	}

	[TestMethod]
	public void ParseHeader_ThrowsWhenTooShort()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => PmdFrameParser.ParseHeader([0x02, 0x00]));
	}

	[TestMethod]
	public void ParseAcc_DecodesReferenceAndDeltas()
	{
		// Reference sample X=100, Y=-200, Z=4000 (int16 LE), then a 4-bit delta group of 2 samples:
		//   s1 = (+1, -1, +2), s2 = (0, +1, -1)   →  packed LSB-first as F1 02 F1.
		byte[] data =
		[
			0x64, 0x00, 0x38, 0xFF, 0xA0, 0x0F, // reference
			0x04, 0x02,                         // deltaBits=4, sampleCount=2
			0xF1, 0x02, 0xF1,                   // packed deltas
		];
		byte[] frame = Frame(0x02, OneSecondTimestamp, 0x81, data);

		var samples = PmdFrameParser.ParseAcc(frame);

		Assert.AreEqual(3, samples.Count);
		Assert.AreEqual(new PmdAccSample(100, -200, 4000), samples[0]);
		Assert.AreEqual(new PmdAccSample(101, -201, 4002), samples[1]);
		Assert.AreEqual(new PmdAccSample(101, -200, 4001), samples[2]);
	}

	[TestMethod]
	public void ParseAcc_DecodesMultipleDeltaGroups()
	{
		// Reference (100,-200,4000), then TWO 4-bit delta groups of 2 samples each, both carrying the
		// same deltas (s.x=+1, s.y=-1, s.z=+2) / (0,+1,-1) packed LSB-first as F1 02 F1. This exercises
		// the group-to-group byte advancement (DivRoundUp) that a single-group frame never reaches.
		byte[] data =
		[
			0x64, 0x00, 0x38, 0xFF, 0xA0, 0x0F, // reference
			0x04, 0x02, 0xF1, 0x02, 0xF1,       // group 1
			0x04, 0x02, 0xF1, 0x02, 0xF1,       // group 2
		];
		byte[] frame = Frame(0x02, OneSecondTimestamp, 0x81, data);

		var samples = PmdFrameParser.ParseAcc(frame);

		Assert.AreEqual(5, samples.Count);
		Assert.AreEqual(new PmdAccSample(100, -200, 4000), samples[0]);
		Assert.AreEqual(new PmdAccSample(101, -201, 4002), samples[1]);
		Assert.AreEqual(new PmdAccSample(101, -200, 4001), samples[2]);
		Assert.AreEqual(new PmdAccSample(102, -201, 4003), samples[3]);
		Assert.AreEqual(new PmdAccSample(102, -200, 4002), samples[4]);
	}

	[TestMethod]
	public void ParseAcc_DecodesUncompressedFrame()
	{
		// Frame-type byte 0x00 ⇒ not compressed: a flat array of int16 LE channel triples.
		byte[] data =
		[
			0x64, 0x00, 0x38, 0xFF, 0xA0, 0x0F, // (100, -200, 4000)
			0x9C, 0xFF, 0x64, 0x00, 0x00, 0x00, // (-100, 100, 0)
		];
		byte[] frame = Frame(0x02, OneSecondTimestamp, 0x00, data);

		var samples = PmdFrameParser.ParseAcc(frame);

		Assert.AreEqual(2, samples.Count);
		Assert.AreEqual(new PmdAccSample(100, -200, 4000), samples[0]);
		Assert.AreEqual(new PmdAccSample(-100, 100, 0), samples[1]);
	}

	[TestMethod]
	public void ParsePpi_DecodesSamplesAndFlags()
	{
		// Sample 1: HR=60, PPI=800, error=5, flags=0x06 (contact status + supported, no blocker).
		// Sample 2: HR=62, PPI=810, error=9, flags=0x01 (blocker set).
		byte[] data =
		[
			60, 0x20, 0x03, 0x05, 0x00, 0x06,
			62, 0x2A, 0x03, 0x09, 0x00, 0x01,
		];
		byte[] frame = Frame(0x03, ZeroTimestamp, 0x00, data);

		var samples = PmdFrameParser.ParsePpi(frame);

		Assert.AreEqual(2, samples.Count);
		Assert.AreEqual(new PmdPpiSample(60, 800, 5, Blocker: false, SkinContact: true, SkinContactSupported: true), samples[0]);
		Assert.AreEqual(new PmdPpiSample(62, 810, 9, Blocker: true, SkinContact: false, SkinContactSupported: false), samples[1]);
	}

	[TestMethod]
	public void ParseEcg_DecodesSignedInt24Samples()
	{
		// 150 µV (0x000096) and -100 µV (0xFFFF9C), both int24 LE.
		byte[] data = [0x96, 0x00, 0x00, 0x9C, 0xFF, 0xFF];
		byte[] frame = Frame(0x00, ZeroTimestamp, 0x00, data);

		var samples = PmdFrameParser.ParseEcg(frame);

		Assert.AreEqual(2, samples.Count);
		Assert.AreEqual(150, samples[0].MicroVolts);
		Assert.AreEqual(-100, samples[1].MicroVolts);
	}
}
