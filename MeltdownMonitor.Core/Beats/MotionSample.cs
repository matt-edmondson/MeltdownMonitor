namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// Where a <see cref="MotionSample"/> came from. The strap-borne accelerometer (Polar PMD) sits
/// on the torso and tracks body movement directly; the device IMU is the phone/PC fallback and is
/// a coarser proxy (it only moves when the device does), so consumers may weight it differently.
/// </summary>
public enum MotionSourceKind
{
	/// <summary>Polar PMD accelerometer on the chest strap / armband.</summary>
	PolarStrap,

	/// <summary>The host device's own inertial sensor (phone/PC accelerometer).</summary>
	DeviceImu,
}

/// <summary>
/// One tri-axial acceleration sample, normalised to g (1 g ≈ 9.81 m/s²). Raw axis values exist so
/// orientation-aware consumers can use them, but the movement monitor only needs
/// <see cref="Magnitude"/>.
/// </summary>
/// <param name="Timestamp">When the sample was measured (UTC).</param>
/// <param name="X">Acceleration along X (g).</param>
/// <param name="Y">Acceleration along Y (g).</param>
/// <param name="Z">Acceleration along Z (g).</param>
/// <param name="Source">Which sensor produced the sample.</param>
public record MotionSample(DateTimeOffset Timestamp, double X, double Y, double Z, MotionSourceKind Source)
{
	/// <summary>Vector magnitude of the acceleration (g). At rest this is ~1 g (gravity).</summary>
	public double Magnitude => Math.Sqrt((X * X) + (Y * Y) + (Z * Z));
}
