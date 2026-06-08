namespace MeltdownMonitor.Core.Motion;

/// <summary>
/// The rule for excluding motion-corrupted beats from HRV. Movement smears the RR interval with timing
/// noise, so when the body is moving at/above the gate level a beat's interval is unreliable and is best
/// kept out of the metrics. Kept as a tiny pure function so both pipelines apply it identically and it is
/// unit-tested. <see cref="MovementLevel.Unknown"/> (no motion source) never rejects, so a build with no
/// accelerometer is byte-identical.
/// </summary>
public static class MotionArtifactGate
{
	/// <summary>True when a beat at this movement <paramref name="level"/> should be treated as an
	/// artifact (the level is known and at/above <paramref name="threshold"/>).</summary>
	public static bool IsArtifact(MovementLevel level, MovementLevel threshold) =>
		level != MovementLevel.Unknown && level >= threshold;
}
