namespace MeltdownMonitor.Core.Beats.Polar;

/// <summary>
/// Bridges decoded PMD accelerometer samples into the platform-neutral <see cref="MotionSample"/>
/// the movement monitor consumes. Kept in Core so all three BLE heads convert identically. Polar
/// ACC values are in milli-g; <see cref="MotionSample"/> is in g.
/// </summary>
public static class PolarMotion
{
	private const double MilliGToG = 1.0 / 1000.0;

	/// <summary>Converts a Polar ACC sample (milli-g) to a strap-sourced <see cref="MotionSample"/> (g).</summary>
	public static MotionSample ToMotionSample(PmdAccSample acc, DateTimeOffset timestamp) =>
		new(timestamp, acc.X * MilliGToG, acc.Y * MilliGToG, acc.Z * MilliGToG, MotionSourceKind.PolarStrap);
}
