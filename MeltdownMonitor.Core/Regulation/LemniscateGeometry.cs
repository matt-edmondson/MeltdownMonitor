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

	/// <summary>Default outline sample count — preserves the historical fixed resolution.</summary>
	public const int DefaultSegments = 96;

	/// <summary>Minimum configurable resolution; still a recognizable figure-8 (clearly faceted).
	/// Floor of 24 also keeps the desktop trace's n-1 divisor safe.</summary>
	public const int MinSegments = 24;

	/// <summary>Maximum configurable resolution; smooth, with diminishing visual return above.</summary>
	public const int MaxSegments = 256;

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

	/// <summary>
	/// Strokes a closed centreline into the boundary of a filled ribbon ("tri-strip"), so a
	/// thick trace can be drawn as a single non-overlapping mesh instead of stacked round-join
	/// segments (which overdraw — and bloom — under additive blending). For each centreline
	/// point it emits one <paramref name="left"/> and one <paramref name="right"/> vertex,
	/// offset to either side along the join's miter direction by that point's half-thickness;
	/// consecutive left/right pairs form the quads of the ribbon. The miter extension is clamped
	/// to <paramref name="miterLimit"/>× so a sharp turn or near-reversal (e.g. the lemniscate's
	/// self-crossing) widens the join instead of shooting a spike to infinity.
	/// </summary>
	/// <param name="centre">Closed centreline; the last point joins back to the first.</param>
	/// <param name="halfThickness">Per-point stroke half-width; same length as <paramref name="centre"/>.</param>
	/// <param name="miterLimit">Maximum miter extension as a multiple of half-thickness; must be ≥ 1.</param>
	/// <param name="left">Receives the left-hand boundary vertices; same length as <paramref name="centre"/>.</param>
	/// <param name="right">Receives the right-hand boundary vertices; same length as <paramref name="centre"/>.</param>
	public static void StrokeClosed(
		ReadOnlySpan<Vector2> centre,
		ReadOnlySpan<float> halfThickness,
		float miterLimit,
		Span<Vector2> left,
		Span<Vector2> right)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(miterLimit, 1f);
		int n = centre.Length;
		if (halfThickness.Length != n || left.Length != n || right.Length != n)
		{
			throw new ArgumentException("centre, halfThickness, left and right must all be the same length.");
		}

		for (int i = 0; i < n; i++)
		{
			Vector2 cur = centre[i];
			Vector2 dIn = Direction(centre[((i - 1) + n) % n], cur);
			Vector2 dOut = Direction(cur, centre[(i + 1) % n]);

			// A duplicated point leaves one edge with no direction; borrow the other so the
			// join still resolves rather than collapsing the ribbon to zero width there.
			if (dIn == Vector2.Zero)
			{
				dIn = dOut;
			}

			if (dOut == Vector2.Zero)
			{
				dOut = dIn;
			}

			// Edge normals (screen space, +Y down: rotate the tangent +90°). The miter is the
			// bisector of the two edge normals; scaling by 1/cos(half-angle) keeps the stroke
			// the same width through the bend.
			Vector2 nIn = new(-dIn.Y, dIn.X);
			Vector2 nOut = new(-dOut.Y, dOut.X);
			Vector2 sum = nIn + nOut;
			float sumLen = sum.Length();

			Vector2 miter;
			float scale;
			if (sumLen < 1e-4f)
			{
				// ~180° reversal: the bisector is undefined, so just butt the join.
				miter = nOut;
				scale = 1f;
			}
			else
			{
				miter = sum / sumLen;
				float cos = Vector2.Dot(miter, nOut); // cos(half join angle)
				scale = cos > 1e-3f ? MathF.Min(1f / cos, miterLimit) : miterLimit;
			}

			float h = halfThickness[i] * scale;
			left[i] = cur + (miter * h);
			right[i] = cur - (miter * h);
		}
	}

	private static Vector2 Direction(Vector2 a, Vector2 b)
	{
		Vector2 d = b - a;
		float len = d.Length();
		return len < 1e-6f ? Vector2.Zero : d / len;
	}
}
