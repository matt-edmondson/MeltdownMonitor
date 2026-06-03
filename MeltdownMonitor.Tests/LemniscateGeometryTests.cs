using System.Numerics;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

[TestClass]
public class LemniscateGeometryTests
{
	[TestMethod]
	public void MarkerAtZero_IsCentre()
	{
		var p = LemniscateGeometry.MarkerPoint(0f, new Vector2(100, 50), halfWidth: 40f);
		Assert.AreEqual(100f, p.X, 0.001f);
		Assert.AreEqual(50f, p.Y, 0.001f);
	}

	[TestMethod]
	public void MarkerAtPlusOne_IsRightTip()
	{
		var p = LemniscateGeometry.MarkerPoint(1f, new Vector2(100, 50), halfWidth: 40f);
		Assert.AreEqual(140f, p.X, 0.001f);
	}

	[TestMethod]
	public void MarkerAtMinusOne_IsLeftTip()
	{
		var p = LemniscateGeometry.MarkerPoint(-1f, new Vector2(100, 50), halfWidth: 40f);
		Assert.AreEqual(60f, p.X, 0.001f);
	}

	[TestMethod]
	public void Polyline_IsClosedAndSymmetric()
	{
		var pts = LemniscateGeometry.Polyline(new Vector2(0, 0), halfWidth: 40f, lobeHeight: 20f, segments: 64);
		Assert.AreEqual(64, pts.Count);
		// Closed figure-8: first and last points are within ~one segment of each other
		// (a broken/open polyline would leave them a whole lobe-width apart).
		Assert.IsTrue(Vector2.Distance(pts[0], pts[^1]) < 12f,
			$"polyline should be ~closed, gap was {Vector2.Distance(pts[0], pts[^1])}");
		// Symmetric about the vertical axis: max |x| on each side is equal.
		float maxRight = pts.Max(p => p.X);
		float maxLeft = -pts.Min(p => p.X);
		Assert.AreEqual(maxRight, maxLeft, 0.5f);
	}

	[TestMethod]
	public void Polyline_LobeHeight_GovernsPeakY()
	{
		var pts = LemniscateGeometry.Polyline(new Vector2(0, 0), halfWidth: 40f, lobeHeight: 15f, segments: 128);
		float maxY = pts.Max(p => p.Y);
		Assert.AreEqual(15f, maxY, 0.1f, "peak y should equal lobeHeight");
	}

	[TestMethod]
	public void Polyline_NegativeSegments_Throws()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => LemniscateGeometry.Polyline(new Vector2(0, 0), 40f, 20f, -1));
	}

	// --- StrokeClosed (tri-strip ribbon) ---

	// Build a regular n-gon centreline on a circle of the given radius (closed loop).
	private static Vector2[] Circle(Vector2 centre, float radius, int n)
	{
		var pts = new Vector2[n];
		for (int i = 0; i < n; i++)
		{
			float a = (i / (float)n) * MathF.Tau;
			pts[i] = centre + (new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius);
		}

		return pts;
	}

	[TestMethod]
	public void StrokeClosed_OffsetsACircleByHalfThickness()
	{
		Vector2 c = new(10f, -5f);
		const float radius = 50f;
		const float half = 6f;
		var pts = Circle(c, radius, 256);
		var halves = new float[pts.Length];
		Array.Fill(halves, half);
		var left = new Vector2[pts.Length];
		var right = new Vector2[pts.Length];

		LemniscateGeometry.StrokeClosed(pts, halves, miterLimit: 4f, left, right);

		// One boundary rides the outer radius (R + half), the other the inner (R - half).
		// With many segments the miter extension is ~1, so both land within a small tolerance.
		for (int i = 0; i < pts.Length; i++)
		{
			float dl = Vector2.Distance(left[i], c);
			float dr = Vector2.Distance(right[i], c);
			float outer = MathF.Max(dl, dr);
			float inner = MathF.Min(dl, dr);
			Assert.AreEqual(radius + half, outer, 0.05f);
			Assert.AreEqual(radius - half, inner, 0.05f);
		}
	}

	[TestMethod]
	public void StrokeClosed_StrokeWidthMatchesThickness()
	{
		var pts = Circle(Vector2.Zero, 40f, 128);
		var halves = new float[pts.Length];
		Array.Fill(halves, 5f);
		var left = new Vector2[pts.Length];
		var right = new Vector2[pts.Length];

		LemniscateGeometry.StrokeClosed(pts, halves, miterLimit: 4f, left, right);

		// Across a smooth curve the two boundaries stay ~2*half apart (the full stroke width).
		for (int i = 0; i < pts.Length; i++)
		{
			Assert.AreEqual(10f, Vector2.Distance(left[i], right[i]), 0.2f);
		}
	}

	[TestMethod]
	public void StrokeClosed_ClampsMiterAtSharpReversal()
	{
		// A degenerate triangle that doubles back on itself: the apex join would, without a
		// limit, shoot the miter toward infinity. It must stay within miterLimit * half.
		const float half = 4f;
		const float limit = 4f;
		Vector2[] pts =
		[
			new(0f, 0f),
			new(100f, 1f),   // near-collinear return path → very sharp join at this apex
			new(0f, 2f),
		];
		var halves = new[] { half, half, half };
		var left = new Vector2[3];
		var right = new Vector2[3];

		LemniscateGeometry.StrokeClosed(pts, halves, limit, left, right);

		for (int i = 0; i < 3; i++)
		{
			Assert.IsTrue(Vector2.Distance(left[i], pts[i]) <= (half * limit) + 1e-3f,
				$"left[{i}] miter {Vector2.Distance(left[i], pts[i])} exceeded clamp {half * limit}");
			Assert.IsTrue(Vector2.Distance(right[i], pts[i]) <= (half * limit) + 1e-3f,
				$"right[{i}] miter {Vector2.Distance(right[i], pts[i])} exceeded clamp {half * limit}");
		}
	}

	[TestMethod]
	public void StrokeClosed_LemniscateProducesFiniteRibbon()
	{
		var pts = LemniscateGeometry.Polyline(new Vector2(0, 0), halfWidth: 240f, lobeHeight: 80f, segments: 96).ToArray();
		var halves = new float[pts.Length];
		Array.Fill(halves, 8f);
		var left = new Vector2[pts.Length];
		var right = new Vector2[pts.Length];

		LemniscateGeometry.StrokeClosed(pts, halves, miterLimit: 4f, left, right);

		for (int i = 0; i < pts.Length; i++)
		{
			Assert.IsFalse(float.IsNaN(left[i].X) || float.IsNaN(left[i].Y), $"left[{i}] is NaN");
			Assert.IsFalse(float.IsNaN(right[i].X) || float.IsNaN(right[i].Y), $"right[{i}] is NaN");
			Assert.IsTrue(Vector2.Distance(left[i], right[i]) > 0f, $"ribbon collapsed at {i}");
		}
	}

	[TestMethod]
	public void StrokeClosed_DuplicatePointKeepsRibbonWidth()
	{
		// A repeated vertex (one edge has no direction) must borrow its neighbour's tangent
		// rather than collapsing the stroke to zero width there.
		Vector2[] pts =
		[
			new(0f, 0f),
			new(10f, 0f),
			new(10f, 0f), // duplicate
			new(20f, 0f),
			new(20f, 10f),
			new(0f, 10f),
		];
		var halves = new float[pts.Length];
		Array.Fill(halves, 3f);
		var left = new Vector2[pts.Length];
		var right = new Vector2[pts.Length];

		LemniscateGeometry.StrokeClosed(pts, halves, miterLimit: 4f, left, right);

		Assert.IsTrue(Vector2.Distance(left[2], right[2]) > 0f, "duplicate vertex collapsed the ribbon");
	}

	[TestMethod]
	public void StrokeClosed_MismatchedLengths_Throws()
	{
		var pts = new Vector2[4];
		Assert.Throws<ArgumentException>(
			() => LemniscateGeometry.StrokeClosed(pts, new float[3], 4f, new Vector2[4], new Vector2[4]));
	}

	[TestMethod]
	public void StrokeClosed_MiterLimitBelowOne_Throws()
	{
		var pts = new Vector2[4];
		Assert.Throws<ArgumentOutOfRangeException>(
			() => LemniscateGeometry.StrokeClosed(pts, new float[4], 0.5f, new Vector2[4], new Vector2[4]));
	}
}
