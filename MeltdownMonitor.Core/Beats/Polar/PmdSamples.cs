namespace MeltdownMonitor.Core.Beats.Polar;

/// <summary>
/// One tri-axial accelerometer sample decoded from a PMD ACC frame. Units are the raw sensor
/// units requested in the start command (milli-g for the H10/Verity default range); only the
/// relative magnitude matters to the movement monitor, so the units are not normalised here.
/// </summary>
public record PmdAccSample(int X, int Y, int Z);

/// <summary>
/// One peak-to-peak interval sample decoded from a PMD PPI frame. Unlike the standard Heart
/// Rate service's bare RR list, PPI carries a per-beat <see cref="ErrorEstimateMs"/> and contact
/// flags — quality information the artifact filter can lean on.
/// </summary>
/// <param name="HeartRate">Instantaneous HR (bpm).</param>
/// <param name="PpiMs">Peak-to-peak interval (ms).</param>
/// <param name="ErrorEstimateMs">Estimated error of <see cref="PpiMs"/> (ms); larger ⇒ less trustworthy.</param>
/// <param name="Blocker">
/// True when the interval is flagged invalid (e.g. movement or poor signal). Blocked intervals
/// should be treated as artifacts.
/// </param>
/// <param name="SkinContact">Whether the optical sensor reports skin contact for this sample.</param>
/// <param name="SkinContactSupported">Whether the sensor reports skin contact at all.</param>
public record PmdPpiSample(
	int HeartRate,
	int PpiMs,
	int ErrorEstimateMs,
	bool Blocker,
	bool SkinContact,
	bool SkinContactSupported);

/// <summary>One ECG sample (microvolts) decoded from a PMD ECG frame.</summary>
public record PmdEcgSample(int MicroVolts);

/// <summary>
/// The decoded header common to every PMD data frame: which measurement it carries, the device
/// timestamp of the frame's <i>last</i> sample, the frame-type/format byte, whether the payload
/// is delta-compressed, and where the payload begins.
/// </summary>
/// <param name="MeasurementType">The measurement type byte.</param>
/// <param name="Timestamp">Device time of the last sample in the frame (UTC).</param>
/// <param name="FrameType">The format variant (lower 7 bits of the frame-type byte).</param>
/// <param name="IsCompressed">True when the payload is delta-compressed (frame-type byte bit 7).</param>
/// <param name="DataOffset">Byte offset at which the payload starts (always 10).</param>
public record PmdFrameHeader(
	PmdMeasurementType MeasurementType,
	DateTimeOffset Timestamp,
	int FrameType,
	bool IsCompressed,
	int DataOffset);
