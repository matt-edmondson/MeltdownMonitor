using System.Numerics;

namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Pure geometry for the Regulation Field's figure-8 (lemniscate of Bernoulli).
/// Screen space: +X right, +Y down. The marker is a needle on the major axis;
/// the polyline is the drawn track.
/// </summary>
public static class LemniscateGeometry
{
	// Peak |y| of the parametric lemniscate (1/(2√2)); used to normalise y so the
	// lobe half-height equals lobeHeight exactly.
	private const double YPeakNormalization = 0.35355339059327376;

	/// <summary>
	/// Marker position for a regulation index in [-1, 1]: the needle slides along
	/// the major axis from the cool (left) tip through the centre to the warm
	/// (right) tip. Depth, not orbit.
	/// </summary>
	public static Vector2 MarkerPoint(float index, Vector2 centre, float halfWidth)
		=> new(centre.X + (Math.Clamp(index, -1f, 1f) * halfWidth), centre.Y);

	/// <summary>
	/// Samples the lemniscate outline as a closed polyline of <paramref name="segments"/>
	/// points, centred at <paramref name="centre"/>. <paramref name="halfWidth"/> is the
	/// distance from centre to a lobe tip; <paramref name="lobeHeight"/> the half-height.
	/// </summary>
	/// <param name="segments">Number of sample points; must be ≥ 0 (0 yields an empty list).</param>
	public static IReadOnlyList<Vector2> Polyline(Vector2 centre, float halfWidth, float lobeHeight, int segments)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(segments);
		var points = new List<Vector2>(segments);
		for (int i = 0; i < segments; i++)
		{
			double t = (i / (double)segments) * 2.0 * Math.PI;
			double denom = 1.0 + (Math.Sin(t) * Math.Sin(t));
			double x = Math.Cos(t) / denom;
			double y = Math.Sin(t) * Math.Cos(t) / denom;
			// y is scaled so the lobe half-height is lobeHeight; the parametric y peaks
			// at YPeakNormalization (1/(2√2)), so divide by that to normalise to ±1 before scaling.
			points.Add(new Vector2(
				centre.X + ((float)x * halfWidth),
				centre.Y + ((float)(y / YPeakNormalization) * lobeHeight)));
		}

		return points;
	}
}
