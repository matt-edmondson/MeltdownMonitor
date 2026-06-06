namespace MeltdownMonitor.Core.Beats.Polar;

/// <summary>
/// Decodes PMD data-characteristic frames. All byte-level logic lives here, platform-neutral
/// and unit-tested, so the three BLE heads only have to subscribe to the characteristic and
/// hand the raw bytes over — exactly the split <see cref="HrMeasurementParser"/> uses for the
/// standard Heart Rate service.
///
/// Frame layout (PMD specification):
///   byte 0      measurement type
///   bytes 1..8  device timestamp, uint64 LE, nanoseconds since 2000-01-01 (the LAST sample)
///   byte 9      frame type — bit 7 = delta-compressed flag, bits 0..6 = format variant
///   bytes 10..  payload
///
/// ACC payloads are delta-compressed; PPI and (H10) ECG payloads are flat sample arrays.
/// </summary>
public static class PmdFrameParser
{
	private const int HeaderLength = 10;

	/// <summary>Parses the 10-byte frame header. Throws if the frame is shorter than a header.</summary>
	public static PmdFrameHeader ParseHeader(ReadOnlySpan<byte> frame)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(frame.Length, HeaderLength, nameof(frame));

		var type = (PmdMeasurementType)frame[0];
		ulong nanos = BitConverter.ToUInt64(frame[1..9]);
		byte frameTypeByte = frame[9];
		bool compressed = (frameTypeByte & 0x80) != 0;
		int frameType = frameTypeByte & 0x7F;

		DateTimeOffset timestamp = PmdConstants.Epoch.AddTicks((long)(nanos / 100));

		return new PmdFrameHeader(type, timestamp, frameType, compressed, HeaderLength);
	}

	/// <summary>
	/// Decodes an ACC frame into tri-axial samples. H10/Verity ACC frames are delta-compressed
	/// with a 16-bit reference resolution and 3 channels (X, Y, Z).
	/// </summary>
	public static IReadOnlyList<PmdAccSample> ParseAcc(ReadOnlySpan<byte> frame, int resolutionBits = 16, int channels = 3)
	{
		var header = ParseHeader(frame);
		var samples = new List<PmdAccSample>();
		if (!header.IsCompressed)
		{
			// Uncommon for ACC, but support a flat array of signed little-endian channel triples.
			int bytesPerChannel = resolutionBits / 8;
			int stride = bytesPerChannel * channels;
			ReadOnlySpan<byte> data = frame[header.DataOffset..];
			for (int i = 0; i + stride <= data.Length; i += stride)
			{
				int x = ReadSignedLittleEndian(data.Slice(i, bytesPerChannel));
				int y = ReadSignedLittleEndian(data.Slice(i + bytesPerChannel, bytesPerChannel));
				int z = ReadSignedLittleEndian(data.Slice(i + (2 * bytesPerChannel), bytesPerChannel));
				samples.Add(new PmdAccSample(x, y, z));
			}

			return samples;
		}

		foreach (int[] sample in ParseDeltaFrame(frame[header.DataOffset..], channels, resolutionBits))
		{
			samples.Add(new PmdAccSample(sample[0], sample[1], sample[2]));
		}

		return samples;
	}

	/// <summary>
	/// Decodes a PPI frame. PPI is never compressed: the payload is a flat array of 6-byte
	/// samples (HR, PPI, error estimate, flags).
	/// </summary>
	public static IReadOnlyList<PmdPpiSample> ParsePpi(ReadOnlySpan<byte> frame)
	{
		var header = ParseHeader(frame);
		var samples = new List<PmdPpiSample>();
		ReadOnlySpan<byte> data = frame[header.DataOffset..];

		const int SampleSize = 6;
		for (int i = 0; i + SampleSize <= data.Length; i += SampleSize)
		{
			int hr = data[i];
			int ppi = data[i + 1] | (data[i + 2] << 8);
			int error = data[i + 3] | (data[i + 4] << 8);
			byte flags = data[i + 5];
			bool blocker = (flags & 0x01) != 0;
			bool contactStatus = (flags & 0x02) != 0;
			bool contactSupported = (flags & 0x04) != 0;
			samples.Add(new PmdPpiSample(hr, ppi, error, blocker, contactStatus, contactSupported));
		}

		return samples;
	}

	/// <summary>
	/// Decodes an ECG frame (H10). The H10 streams uncompressed 3-byte signed little-endian
	/// samples in microvolts; delta-compressed frames are also supported for completeness.
	/// </summary>
	public static IReadOnlyList<PmdEcgSample> ParseEcg(ReadOnlySpan<byte> frame)
	{
		var header = ParseHeader(frame);
		var samples = new List<PmdEcgSample>();

		if (header.IsCompressed)
		{
			foreach (int[] sample in ParseDeltaFrame(frame[header.DataOffset..], channels: 1, referenceBits: 24))
			{
				samples.Add(new PmdEcgSample(sample[0]));
			}

			return samples;
		}

		ReadOnlySpan<byte> data = frame[header.DataOffset..];
		const int SampleSize = 3; // int24
		for (int i = 0; i + SampleSize <= data.Length; i += SampleSize)
		{
			samples.Add(new PmdEcgSample(ReadSignedLittleEndian(data.Slice(i, SampleSize))));
		}

		return samples;
	}

	/// <summary>
	/// The generic PMD delta-frame decoder. The payload opens with one full reference sample
	/// (<paramref name="channels"/> signed little-endian integers of <paramref name="referenceBits"/>
	/// bits each, byte-aligned), then a run of delta groups. Each group is:
	///   byte 0  delta bit-width
	///   byte 1  sample count
	///   then    sampleCount × channels signed values bit-packed (LSB first) at that bit-width,
	/// each added to the running previous sample. The reference sample is itself the first output.
	/// </summary>
	private static List<int[]> ParseDeltaFrame(ReadOnlySpan<byte> data, int channels, int referenceBits)
	{
		var samples = new List<int[]>();
		int bytesPerChannel = referenceBits / 8;
		int refSize = bytesPerChannel * channels;
		if (data.Length < refSize)
		{
			return samples;
		}

		var previous = new int[channels];
		for (int c = 0; c < channels; c++)
		{
			previous[c] = ReadSignedLittleEndian(data.Slice(c * bytesPerChannel, bytesPerChannel));
		}

		samples.Add((int[])previous.Clone());

		int byteOffset = refSize;
		while (byteOffset + 2 <= data.Length)
		{
			int deltaBits = data[byteOffset];
			int sampleCount = data[byteOffset + 1];
			byteOffset += 2;

			if (deltaBits == 0 || sampleCount == 0)
			{
				continue;
			}

			var reader = new BitReader(data, byteOffset);
			for (int s = 0; s < sampleCount; s++)
			{
				var current = new int[channels];
				for (int c = 0; c < channels; c++)
				{
					current[c] = previous[c] + reader.ReadSigned(deltaBits);
				}

				samples.Add(current);
				previous = current;
			}

			byteOffset += DivRoundUp(sampleCount * channels * deltaBits, 8);
		}

		return samples;
	}

	private static int DivRoundUp(int numerator, int denominator) => (numerator + denominator - 1) / denominator;

	// Reads a byte-aligned signed little-endian integer of the given byte width (1..4) and
	// sign-extends it to int.
	private static int ReadSignedLittleEndian(ReadOnlySpan<byte> bytes)
	{
		int value = 0;
		for (int i = 0; i < bytes.Length; i++)
		{
			value |= bytes[i] << (8 * i);
		}

		int bits = bytes.Length * 8;
		return SignExtend(value, bits);
	}

	private static int SignExtend(int value, int bits)
	{
		if (bits >= 32)
		{
			return value;
		}

		int mask = 1 << (bits - 1);
		int unsigned = value & ((1 << bits) - 1);
		return (unsigned ^ mask) - mask;
	}

	/// <summary>
	/// Reads bit-packed values LSB-first across a byte span — the packing PMD delta groups use.
	/// </summary>
	private ref struct BitReader(ReadOnlySpan<byte> data, int byteOffset)
	{
		private readonly ReadOnlySpan<byte> _data = data;
		private int _bitPosition = byteOffset * 8;

		public int ReadSigned(int bits)
		{
			int value = 0;
			for (int i = 0; i < bits; i++)
			{
				int byteIndex = _bitPosition >> 3;
				int bitIndex = _bitPosition & 7;
				int bit = byteIndex < _data.Length ? (_data[byteIndex] >> bitIndex) & 1 : 0;
				value |= bit << i;
				_bitPosition++;
			}

			return SignExtend(value, bits);
		}
	}
}
