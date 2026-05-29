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
}
