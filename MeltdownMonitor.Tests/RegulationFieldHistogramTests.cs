using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

[TestClass]
public class RegulationFieldHistogramTests
{
	private static RegulationTrailPoint Point(double index, double vagalTone = 0.5) =>
		new(new RegulationReading(index, 1.0, 1.0, 0.5, 0.0) { VagalTone = vagalTone }, DetectorState.Idle);

	[TestMethod]
	public void EmptyTrail_ProducesZeroCounts()
	{
		var hist = RegulationFieldHistogram.IndexAxis([]);
		Assert.AreEqual(RegulationFieldHistogram.DefaultBucketCount, hist.BucketCount);
		Assert.AreEqual(0, hist.TotalCount);
		Assert.AreEqual(0, hist.PeakCount);
	}

	[TestMethod]
	public void IndexAxis_CountsEverySample()
	{
		RegulationTrailPoint[] trail = [Point(-0.9), Point(0.0), Point(0.0), Point(0.85)];
		var hist = RegulationFieldHistogram.IndexAxis(trail);
		Assert.AreEqual(4, hist.TotalCount);
		Assert.AreEqual(-1.0, hist.Min, 1e-9);
		Assert.AreEqual(1.0, hist.Max, 1e-9);
	}

	[TestMethod]
	public void IndexAxis_BucketsByValue()
	{
		// 4 buckets over [-1, 1]: [-1,-0.5) [-0.5,0) [0,0.5) [0.5,1].
		RegulationTrailPoint[] trail = [Point(-0.75), Point(-0.25), Point(0.25), Point(0.25), Point(0.75)];
		var hist = RegulationFieldHistogram.IndexAxis(trail, bucketCount: 4);
		CollectionAssert.AreEqual(new[] { 1, 1, 2, 1 }, hist.Counts.ToArray());
		Assert.AreEqual(2, hist.PeakCount);
	}

	[TestMethod]
	public void Extremes_ClampIntoEndBuckets()
	{
		// Below min and at/above max both land in the first/last bucket rather than overflowing.
		RegulationTrailPoint[] trail = [Point(-2.0), Point(1.0), Point(5.0)];
		var hist = RegulationFieldHistogram.IndexAxis(trail, bucketCount: 4);
		Assert.AreEqual(1, hist.Counts[0], "value below min clamps into the first bucket");
		Assert.AreEqual(2, hist.Counts[^1], "max and above-max clamp into the last bucket");
		Assert.AreEqual(3, hist.TotalCount);
	}

	[TestMethod]
	public void VagalToneAxis_BucketsOverZeroToOne()
	{
		RegulationTrailPoint[] trail = [Point(0, vagalTone: 0.1), Point(0, vagalTone: 0.9), Point(0, vagalTone: 0.95)];
		var hist = RegulationFieldHistogram.VagalToneAxis(trail, bucketCount: 10);
		Assert.AreEqual(0.0, hist.Min, 1e-9);
		Assert.AreEqual(1.0, hist.Max, 1e-9);
		Assert.AreEqual(1, hist.Counts[1], "0.1 → second bucket");
		Assert.AreEqual(2, hist.Counts[9], "0.9 and 0.95 → last bucket");
	}

	[TestMethod]
	public void NonFiniteValues_AreSkipped()
	{
		RegulationTrailPoint[] trail = [Point(double.NaN), Point(0.25), Point(double.PositiveInfinity)];
		var hist = RegulationFieldHistogram.IndexAxis(trail, bucketCount: 4);
		Assert.AreEqual(1, hist.TotalCount);
	}

	[TestMethod]
	public void ZeroBuckets_Throws()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => RegulationFieldHistogram.IndexAxis([], bucketCount: 0));
	}

	[TestMethod]
	public void FieldDensity_BucketsByIndexAndVagalTone()
	{
		// 2×2 grid: x split at index 0 (cool|warm), y split at vagal tone 0.5 (FRAGILE|STEADY).
		// Row-major counts[(y*2)+x]: cell (x=0,y=0) = cool+fragile, (x=1,y=1) = warm+steady.
		RegulationTrailPoint[] trail =
		[
			Point(-0.5, vagalTone: 0.2),  // cool, fragile  → (0,0)
			Point(0.5, vagalTone: 0.8),   // warm, steady   → (1,1)
			Point(0.5, vagalTone: 0.8),   // warm, steady   → (1,1)
			Point(-0.5, vagalTone: 0.8),  // cool, steady   → (0,1)
		];
		var d = RegulationFieldHistogram.FieldDensity(trail, xBuckets: 2, yBuckets: 2);

		Assert.AreEqual(4, d.TotalCount);
		Assert.AreEqual(2, d.PeakCount, "warm+steady cell holds two samples");
		Assert.AreEqual(1, d.Count(0, 0), "cool+fragile");
		Assert.AreEqual(0, d.Count(1, 0), "warm+fragile is empty");
		Assert.AreEqual(1, d.Count(0, 1), "cool+steady");
		Assert.AreEqual(2, d.Count(1, 1), "warm+steady");
	}

	[TestMethod]
	public void FieldDensity_PeakBucket_PointsAtBusiestCell()
	{
		// Same layout as FieldDensity_BucketsByIndexAndVagalTone: the warm+steady cell (1,1) holds two.
		RegulationTrailPoint[] trail =
		[
			Point(-0.5, vagalTone: 0.2),
			Point(0.5, vagalTone: 0.8),
			Point(0.5, vagalTone: 0.8),
			Point(-0.5, vagalTone: 0.8),
		];
		var d = RegulationFieldHistogram.FieldDensity(trail, xBuckets: 2, yBuckets: 2);

		Assert.AreEqual(1, d.PeakX, "busiest column");
		Assert.AreEqual(1, d.PeakY, "busiest row");
		Assert.AreEqual(d.PeakCount, d.Count(d.PeakX, d.PeakY), "peak coords address the peak count");
	}

	[TestMethod]
	public void FieldDensity_PeakBucket_EmptyIsNegative()
	{
		var d = RegulationFieldHistogram.FieldDensity([], xBuckets: 3, yBuckets: 3);
		Assert.AreEqual(0, d.PeakCount);
		Assert.AreEqual(-1, d.PeakX);
		Assert.AreEqual(-1, d.PeakY);
	}

	[TestMethod]
	public void FieldDensity_PeakBucket_TieResolvesToFirstRowMajor()
	{
		// Two singleton cells: (0,0) cool+fragile and (1,1) warm+steady. Row-major scan hits (0,0) first.
		RegulationTrailPoint[] trail =
		[
			Point(0.5, vagalTone: 0.8),   // (1,1)
			Point(-0.5, vagalTone: 0.2),  // (0,0)
		];
		var d = RegulationFieldHistogram.FieldDensity(trail, xBuckets: 2, yBuckets: 2);

		Assert.AreEqual(1, d.PeakCount);
		Assert.AreEqual(0, d.PeakX);
		Assert.AreEqual(0, d.PeakY);
	}

	[TestMethod]
	public void FieldDensity_SkipsNonFiniteAndClampsOutOfRange()
	{
		RegulationTrailPoint[] trail =
		[
			Point(double.NaN, vagalTone: 0.5),  // skipped (non-finite index)
			Point(5.0, vagalTone: 2.0),          // both out of range → clamps into top-right cell
		];
		var d = RegulationFieldHistogram.FieldDensity(trail, xBuckets: 2, yBuckets: 2);
		Assert.AreEqual(1, d.TotalCount);
		Assert.AreEqual(1, d.Count(1, 1), "above-max index and tone clamp into the last cell");
	}

	[TestMethod]
	public void FieldDensity_ZeroBuckets_Throws()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => RegulationFieldHistogram.FieldDensity([], xBuckets: 0, yBuckets: 4));
		Assert.Throws<ArgumentOutOfRangeException>(() => RegulationFieldHistogram.FieldDensity([], xBuckets: 4, yBuckets: 0));
	}

	[TestMethod]
	public void HighDensityBounds_EmptyGrid_IsNull()
	{
		var d = RegulationFieldHistogram.FieldDensity([], xBuckets: 4, yBuckets: 4);
		Assert.IsNull(d.HighDensityBounds(0.5));
	}

	[TestMethod]
	public void HighDensityBounds_WrapsBusyCellsAcrossBuckets()
	{
		// Peak cell (1,1) holds 4; a neighbour (0,1) holds 2. At a 50% threshold both qualify, so the
		// box spans columns 0..1 on row 1 — wider than the single peak cell.
		RegulationTrailPoint[] trail =
		[
			Point(0.5, vagalTone: 0.8), Point(0.5, vagalTone: 0.8),
			Point(0.5, vagalTone: 0.8), Point(0.5, vagalTone: 0.8),  // (1,1) ×4
			Point(-0.5, vagalTone: 0.8), Point(-0.5, vagalTone: 0.8), // (0,1) ×2
		];
		var d = RegulationFieldHistogram.FieldDensity(trail, xBuckets: 2, yBuckets: 2);

		var b = d.HighDensityBounds(0.5);
		Assert.IsNotNull(b);
		Assert.AreEqual(0, b.Value.MinX);
		Assert.AreEqual(1, b.Value.MaxX);
		Assert.AreEqual(1, b.Value.MinY);
		Assert.AreEqual(1, b.Value.MaxY);
		Assert.AreEqual(2, b.Value.Width);
		Assert.AreEqual(1, b.Value.Height);
	}

	[TestMethod]
	public void HighDensityBounds_HighThreshold_CollapsesOntoPeak()
	{
		// Same data: at threshold 1.0 only the peak cell (1,1) survives → a single-cell box.
		RegulationTrailPoint[] trail =
		[
			Point(0.5, vagalTone: 0.8), Point(0.5, vagalTone: 0.8),
			Point(0.5, vagalTone: 0.8), Point(0.5, vagalTone: 0.8),
			Point(-0.5, vagalTone: 0.8), Point(-0.5, vagalTone: 0.8),
		];
		var d = RegulationFieldHistogram.FieldDensity(trail, xBuckets: 2, yBuckets: 2);

		var b = d.HighDensityBounds(1.0);
		Assert.IsNotNull(b);
		Assert.AreEqual(d.PeakX, b.Value.MinX);
		Assert.AreEqual(d.PeakX, b.Value.MaxX);
		Assert.AreEqual(d.PeakY, b.Value.MinY);
		Assert.AreEqual(d.PeakY, b.Value.MaxY);
		Assert.AreEqual(1, b.Value.Width);
		Assert.AreEqual(1, b.Value.Height);
	}

	[TestMethod]
	public void HighDensityBounds_ZeroThreshold_WrapsEveryOccupiedCell()
	{
		// Two singleton corners (0,0) and (1,1): threshold 0 keeps both occupied cells, empty cells
		// stay out, so the box spans the whole occupied extent.
		RegulationTrailPoint[] trail =
		[
			Point(-0.5, vagalTone: 0.2),  // (0,0)
			Point(0.5, vagalTone: 0.8),   // (1,1)
		];
		var d = RegulationFieldHistogram.FieldDensity(trail, xBuckets: 2, yBuckets: 2);

		var b = d.HighDensityBounds(0.0);
		Assert.IsNotNull(b);
		Assert.AreEqual(0, b.Value.MinX);
		Assert.AreEqual(0, b.Value.MinY);
		Assert.AreEqual(1, b.Value.MaxX);
		Assert.AreEqual(1, b.Value.MaxY);
	}

	[TestMethod]
	public void HighDensityBounds_ThresholdClampsToUnitRange()
	{
		// Out-of-range thresholds clamp: below 0 behaves like 0, above 1 like 1.
		RegulationTrailPoint[] trail =
		[
			Point(0.5, vagalTone: 0.8), Point(0.5, vagalTone: 0.8),
			Point(-0.5, vagalTone: 0.2),
		];
		var d = RegulationFieldHistogram.FieldDensity(trail, xBuckets: 2, yBuckets: 2);

		var wide = d.HighDensityBounds(-1.0);
		var tight = d.HighDensityBounds(2.0);
		Assert.AreEqual(d.HighDensityBounds(0.0), wide);
		Assert.AreEqual(d.HighDensityBounds(1.0), tight);
	}

	[TestMethod]
	public void FieldDensity_Count_BoundsChecked()
	{
		var d = RegulationFieldHistogram.FieldDensity([Point(0, 0.5)], xBuckets: 3, yBuckets: 3);
		Assert.Throws<ArgumentOutOfRangeException>(() => d.Count(3, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => d.Count(0, 3));
		Assert.Throws<ArgumentOutOfRangeException>(() => d.Count(-1, 0));
	}
}
