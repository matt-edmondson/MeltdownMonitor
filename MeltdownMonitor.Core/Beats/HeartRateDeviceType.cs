namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// A heart-rate sensor this app can pin its BLE scan to. Every entry must
/// expose RR intervals over the standard GATT Heart Rate Measurement
/// characteristic (0x2A37) — that is what the HRV pipeline runs on.
///
/// Member order and integer values are part of the persisted settings contract
/// (<c>AppSettings.DeviceType</c> / <c>MobileSettings.DeviceType</c> serialize the
/// enum by value). Only ever append; never reorder or remove.
///
/// Garmin watches (Forerunner 935/955 and similar) are deliberately absent: the
/// 935 broadcasts over ANT+ only, and watches that do broadcast over BLE send a
/// smoothed BPM with no RR field, so they cannot drive HRV. Only Garmin chest
/// straps expose RR over BLE.
/// </summary>
public enum HeartRateDeviceType
{
	/// <summary>Connect to the first device found advertising the Heart Rate Service.</summary>
	Auto,

	/// <summary>Polar H10 chest strap — "Polar H10 xxxxxxxx".</summary>
	H10,

	/// <summary>Polar Verity Sense optical armband — "Polar Sense xxxxxxxx".</summary>
	VeritySense,

	/// <summary>Garmin HRM-Dual chest strap — advertises "HRM-Dual:xxxxxxx".</summary>
	GarminHrmDual,

	/// <summary>Garmin HRM-Pro / HRM-Pro Plus chest strap — advertises "HRM-Pro:xxxxxxx"
	/// (the Pro Plus advertises "HRM-Pro+", which the prefix still matches).</summary>
	GarminHrmPro,
}
