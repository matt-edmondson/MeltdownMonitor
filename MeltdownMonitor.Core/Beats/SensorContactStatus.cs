namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// Skin / electrode contact state reported in the Heart Rate Measurement
/// characteristic (<c>0x2A37</c>) flags byte. The Sensor Contact Support bit
/// (bit 2) says whether the sensor reports contact at all; the Sensor Contact
/// Status bit (bit 1) says whether contact is currently detected.
/// </summary>
public enum SensorContactStatus
{
	/// <summary>The sensor doesn't report contact (support bit clear) — status is unknown.</summary>
	NotSupported,

	/// <summary>Contact reporting is supported, but the sensor is not in good contact.</summary>
	NotDetected,

	/// <summary>The sensor reports good skin / electrode contact.</summary>
	Detected,
}
