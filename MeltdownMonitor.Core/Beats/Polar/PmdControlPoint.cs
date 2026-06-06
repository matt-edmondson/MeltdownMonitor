namespace MeltdownMonitor.Core.Beats.Polar;

/// <summary>
/// Builds PMD Control Point command writes and parses the responses the device indicates back.
/// All wire details follow the Polar Measurement Data specification.
///
/// Lifecycle: read the control point once to learn the <see cref="ParseSupportedFeatures"/>
/// bitmask, then write a <c>RequestMeasurementStart</c> command for each supported type we want.
/// The device indicates a <see cref="ParseResponse"/> (status 0 = success) and then streams
/// frames on the data characteristic until a <c>StopMeasurement</c> write.
/// </summary>
public static class PmdControlPoint
{
	/// <summary>Leading byte of a control-point <i>read</i> value (the feature bitmask response).</summary>
	public const byte FeatureReadResponseCode = 0x0F;

	/// <summary>Leading byte of a control-point <i>indicated</i> response to a command write.</summary>
	public const byte CommandResponseCode = 0xF0;

	/// <summary>Command: query the settings a measurement type supports.</summary>
	public static byte[] BuildGetSettings(PmdMeasurementType type) =>
		[(byte)PmdControlOpCode.GetMeasurementSettings, (byte)type];

	/// <summary>Command: stop streaming a measurement type.</summary>
	public static byte[] BuildStop(PmdMeasurementType type) =>
		[(byte)PmdControlOpCode.StopMeasurement, (byte)type];

	/// <summary>
	/// Command: start the accelerometer stream. Defaults (50 Hz, 16-bit, ±8 g, 3 channels) are the
	/// canonical H10/Verity ACC configuration — ample for movement detection without flooding the link.
	/// </summary>
	public static byte[] BuildStartAcc(
		int sampleRateHz = 50,
		int resolutionBits = 16,
		int rangeG = 8,
		int channels = 3)
	{
		var command = new List<byte>
		{
			(byte)PmdControlOpCode.RequestMeasurementStart,
			(byte)PmdMeasurementType.Acc,
		};
		AppendSetting(command, PmdSettingType.SampleRate, sampleRateHz);
		AppendSetting(command, PmdSettingType.Resolution, resolutionBits);
		AppendSetting(command, PmdSettingType.Range, rangeG);
		AppendSetting(command, PmdSettingType.Channels, channels);
		return [.. command];
	}

	/// <summary>
	/// Command: start the raw ECG stream (H10). Defaults (130 Hz, 14-bit) are the H10's only
	/// supported ECG configuration.
	/// </summary>
	public static byte[] BuildStartEcg(int sampleRateHz = 130, int resolutionBits = 14)
	{
		var command = new List<byte>
		{
			(byte)PmdControlOpCode.RequestMeasurementStart,
			(byte)PmdMeasurementType.Ecg,
		};
		AppendSetting(command, PmdSettingType.SampleRate, sampleRateHz);
		AppendSetting(command, PmdSettingType.Resolution, resolutionBits);
		return [.. command];
	}

	/// <summary>
	/// Command: start the peak-to-peak interval stream (Verity Sense). PPI takes no settings —
	/// the device computes the intervals internally — so the command is just the op code + type.
	/// </summary>
	public static byte[] BuildStartPpi() =>
		[(byte)PmdControlOpCode.RequestMeasurementStart, (byte)PmdMeasurementType.Ppi];

	/// <summary>
	/// Parses a control-point <i>read</i> value into the set of measurement types the sensor
	/// supports. The response is <c>[0x0F, bitmask, …]</c> where bit <c>n</c> of the bitmask
	/// corresponds to measurement type <c>n</c> (bit 0 = ECG, bit 2 = ACC, bit 3 = PPI, …).
	/// Returns an empty set if the value is too short or not a feature response.
	/// </summary>
	public static IReadOnlySet<PmdMeasurementType> ParseSupportedFeatures(ReadOnlySpan<byte> controlPointReadValue)
	{
		var supported = new HashSet<PmdMeasurementType>();
		if (controlPointReadValue.Length < 2 || controlPointReadValue[0] != FeatureReadResponseCode)
		{
			return supported;
		}

		byte bitmask = controlPointReadValue[1];
		foreach (PmdMeasurementType type in Enum.GetValues<PmdMeasurementType>())
		{
			if ((bitmask & (1 << (int)type)) != 0)
			{
				supported.Add(type);
			}
		}

		return supported;
	}

	/// <summary>
	/// Parses a control-point <i>indicated</i> response to a command write. Returns the echoed
	/// op code, measurement type, and the status byte (0 = success). Returns null if the value
	/// is not a command response.
	/// </summary>
	public static PmdControlResponse? ParseResponse(ReadOnlySpan<byte> indicatedValue)
	{
		if (indicatedValue.Length < 4 || indicatedValue[0] != CommandResponseCode)
		{
			return null;
		}

		return new PmdControlResponse(
			(PmdControlOpCode)indicatedValue[1],
			(PmdMeasurementType)indicatedValue[2],
			indicatedValue[3]);
	}

	// One settings TLV: setting-type byte, array length (always 1 here), value as uint16 LE.
	private static void AppendSetting(List<byte> command, PmdSettingType setting, int value)
	{
		command.Add((byte)setting);
		command.Add(0x01);
		command.Add((byte)(value & 0xFF));
		command.Add((byte)((value >> 8) & 0xFF));
	}
}

/// <summary>A parsed PMD Control Point command response.</summary>
/// <param name="OpCode">The echoed operation.</param>
/// <param name="MeasurementType">The echoed measurement type.</param>
/// <param name="Status">Device status code; 0 = success.</param>
public record PmdControlResponse(PmdControlOpCode OpCode, PmdMeasurementType MeasurementType, byte Status)
{
	/// <summary>True when the device accepted the command.</summary>
	public bool IsSuccess => Status == 0;
}
