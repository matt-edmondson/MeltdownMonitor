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
}
