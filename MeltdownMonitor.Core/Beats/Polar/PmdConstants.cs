namespace MeltdownMonitor.Core.Beats.Polar;

/// <summary>
/// Constants for Polar's vendor-specific <b>Polar Measurement Data</b> (PMD) BLE service —
/// the proprietary channel a Polar H10 / Verity Sense exposes alongside the standard Heart
/// Rate service. It streams the high-resolution sensor data the standard GATT profiles never
/// surface: tri-axial accelerometer (ACC), peak-to-peak intervals with a per-beat error
/// estimate (PPI), and raw ECG.
///
/// UUIDs and the wire format are from the official Polar Measurement Data specification
/// (polarofficial/polar-ble-sdk, <c>technical_documentation</c>). The values here are the
/// stable subset MeltdownMonitor consumes — gyro/magnetometer/temperature/pressure are
/// deliberately omitted as they carry no autonomic-state signal.
/// </summary>
public static class PmdConstants
{
	/// <summary>PMD service UUID.</summary>
	public static readonly Guid ServiceUuid = new("fb005c80-02e7-f387-1cad-8acd2d8df0c8");

	/// <summary>PMD Control Point characteristic (Write + Indicate) — settings + start/stop.</summary>
	public static readonly Guid ControlPointUuid = new("fb005c81-02e7-f387-1cad-8acd2d8df0c8");

	/// <summary>PMD Data (MTU) characteristic (Notify) — the measurement frames.</summary>
	public static readonly Guid DataUuid = new("fb005c82-02e7-f387-1cad-8acd2d8df0c8");

	/// <summary>
	/// The PMD epoch: nanosecond device timestamps in every data frame are counted from
	/// 2000-01-01T00:00:00Z.
	/// </summary>
	public static readonly DateTimeOffset Epoch = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// PMD measurement-type identifiers (the leading byte of every data frame and the operand of
/// every control-point command). Only the types MeltdownMonitor streams are enumerated; the
/// numeric values are fixed by the PMD specification.
/// </summary>
public enum PmdMeasurementType : byte
{
	/// <summary>Raw ECG (H10 only, 130 Hz, microvolts) — gold-standard R-peak source.</summary>
	Ecg = 0,

	/// <summary>Photoplethysmography (Verity Sense). Not consumed directly; listed for completeness.</summary>
	Ppg = 1,

	/// <summary>Tri-axial accelerometer (H10 + Verity Sense) — the motion signal.</summary>
	Acc = 2,

	/// <summary>Peak-to-peak interval with per-beat error estimate and contact flags (Verity Sense).</summary>
	Ppi = 3,
}

/// <summary>
/// PMD Control Point operation codes (the leading byte of a command write). The device echoes
/// the op code in its indicated response.
/// </summary>
public enum PmdControlOpCode : byte
{
	/// <summary>Query the settings (sample rate / resolution / range / channels) a type supports.</summary>
	GetMeasurementSettings = 0x01,

	/// <summary>Start streaming a type with a chosen set of settings.</summary>
	RequestMeasurementStart = 0x02,

	/// <summary>Stop streaming a type.</summary>
	StopMeasurement = 0x03,
}

/// <summary>
/// PMD measurement-setting identifiers used in the TLV blocks of a settings response and a
/// start command.
/// </summary>
public enum PmdSettingType : byte
{
	SampleRate = 0x00,
	Resolution = 0x01,
	Range = 0x02,
	Channels = 0x04,
}
