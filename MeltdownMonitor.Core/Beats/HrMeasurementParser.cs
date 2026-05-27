namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// Parses the GATT Heart Rate Measurement characteristic (0x2A37) payload.
///
/// Byte 0 — Flags:
///   bit 0      → HR value format (0 = uint8, 1 = uint16)
///   bit 1-2    → Sensor contact status
///   bit 3      → Energy Expended present
///   bit 4      → RR Interval present
/// HR value:    1 or 2 bytes (depends on bit 0)
/// Energy:      2 bytes (only if bit 3 set)
/// RR Intervals: 2 bytes each, little-endian, units of 1/1024 s
/// </summary>
public static class HrMeasurementParser
{
	private const double RrUnitToMs = 1000.0 / 1024.0;

	public static HrMeasurement Parse(ReadOnlySpan<byte> payload)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(payload.Length, 2, nameof(payload));

		byte flags = payload[0];
		bool hrIs16Bit = (flags & 0x01) != 0;
		bool energyPresent = (flags & 0x08) != 0;
		bool rrPresent = (flags & 0x10) != 0;

		int offset = 1;

		int heartRateBpm;
		if (hrIs16Bit)
		{
			ArgumentOutOfRangeException.ThrowIfLessThan(payload.Length, offset + 2, nameof(payload));
			heartRateBpm = payload[offset] | (payload[offset + 1] << 8);
			offset += 2;
		}
		else
		{
			heartRateBpm = payload[offset];
			offset += 1;
		}

		if (energyPresent)
		{
			offset += 2;
		}

		var rrIntervals = new List<double>();
		if (rrPresent)
		{
			while (offset + 1 < payload.Length)
			{
				int raw = payload[offset] | (payload[offset + 1] << 8);
				rrIntervals.Add(raw * RrUnitToMs);
				offset += 2;
			}
		}

		return new HrMeasurement(heartRateBpm, rrIntervals);
	}
}
