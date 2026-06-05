using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

[TestClass]
public class RegulationFieldAggregateCacheTests
{
	private static RegulationTrailPoint Point(double index, double vagalTone = 0.5) =>
		new(new RegulationReading(index, 1.0, 1.0, 0.5, 0.0) { VagalTone = vagalTone }, DetectorState.Idle);

	[TestMethod]
	public void BeforeFirstUpdate_ExposesEmptyAggregates()
	{
		var cache = new RegulationFieldAggregateCache();
		Assert.AreEqual(0, cache.Density.PeakCount);
		Assert.AreEqual(0, cache.IndexAxis.TotalCount);
		Assert.AreEqual(0, cache.VagalToneAxis.TotalCount);
	}

	[TestMethod]
	public void Update_ComputesDensityAndBothHistograms()
	{
		var cache = new RegulationFieldAggregateCache();
		RegulationTrailPoint[] trail =
		[
			Point(-0.5, vagalTone: 0.2),
			Point(0.5, vagalTone: 0.8),
			Point(0.5, vagalTone: 0.8),
		];

		cache.Update(trail, version: 1, xBuckets: 2, yBuckets: 2, heatmapWindow: int.MaxValue);

		Assert.AreEqual(3, cache.Density.TotalCount);
		Assert.AreEqual(2, cache.Density.Count(1, 1));
		Assert.AreEqual(3, cache.IndexAxis.TotalCount);
		Assert.AreEqual(3, cache.VagalToneAxis.TotalCount);
		// IndexAxis uses xBuckets, VagalToneAxis uses yBuckets.
		Assert.AreEqual(2, cache.IndexAxis.BucketCount);
		Assert.AreEqual(2, cache.VagalToneAxis.BucketCount);
	}

	[TestMethod]
	public void Update_SameVersionAndParams_DoesNotRecompute()
	{
		var cache = new RegulationFieldAggregateCache();
		var trail = new List<RegulationTrailPoint> { Point(0.5, 0.8) };
		cache.Update(trail, version: 7, xBuckets: 2, yBuckets: 2, heatmapWindow: int.MaxValue);
		Assert.AreEqual(1, cache.Density.TotalCount);

		// Mutate the trail but keep the same version: a no-op call must keep the stale cached value,
		// proving the per-frame call doesn't rescan when nothing was flagged as changed.
		trail.Add(Point(0.5, 0.8));
		cache.Update(trail, version: 7, xBuckets: 2, yBuckets: 2, heatmapWindow: int.MaxValue);
		Assert.AreEqual(1, cache.Density.TotalCount, "unchanged version leaves the cached density in place");
	}

	[TestMethod]
	public void Update_NewVersion_Recomputes()
	{
		var cache = new RegulationFieldAggregateCache();
		var trail = new List<RegulationTrailPoint> { Point(0.5, 0.8) };
		cache.Update(trail, version: 1, xBuckets: 2, yBuckets: 2, heatmapWindow: int.MaxValue);
		Assert.AreEqual(1, cache.Density.TotalCount);

		trail.Add(Point(0.5, 0.8));
		cache.Update(trail, version: 2, xBuckets: 2, yBuckets: 2, heatmapWindow: int.MaxValue);
		Assert.AreEqual(2, cache.Density.TotalCount, "a bumped version triggers a rescan");
	}

	[TestMethod]
	public void Update_BucketChange_RecomputesWithoutVersionBump()
	{
		var cache = new RegulationFieldAggregateCache();
		RegulationTrailPoint[] trail = [Point(0.5, 0.8)];
		cache.Update(trail, version: 1, xBuckets: 2, yBuckets: 2, heatmapWindow: int.MaxValue);
		Assert.AreEqual(2, cache.Density.XBuckets);

		cache.Update(trail, version: 1, xBuckets: 8, yBuckets: 4, heatmapWindow: int.MaxValue);
		Assert.AreEqual(8, cache.Density.XBuckets, "a bucket-count change recomputes even at the same version");
		Assert.AreEqual(4, cache.Density.YBuckets);
	}

	[TestMethod]
	public void Update_HeatmapWindow_BoundsDensityToTrailingSliceButNotHistograms()
	{
		var cache = new RegulationFieldAggregateCache();
		RegulationTrailPoint[] trail =
		[
			Point(-0.5, vagalTone: 0.2),  // older — outside a window of 2
			Point(-0.5, vagalTone: 0.2),  // older — outside a window of 2
			Point(0.5, vagalTone: 0.8),   // recent — inside the window
			Point(0.5, vagalTone: 0.8),   // recent — inside the window
		];

		cache.Update(trail, version: 1, xBuckets: 2, yBuckets: 2, heatmapWindow: 2);

		Assert.AreEqual(2, cache.Density.TotalCount, "density spans only the last heatmapWindow points");
		Assert.AreEqual(2, cache.Density.Count(1, 1));
		Assert.AreEqual(4, cache.IndexAxis.TotalCount, "histograms span the whole buffer regardless of window");
		Assert.AreEqual(4, cache.VagalToneAxis.TotalCount);
	}

	[TestMethod]
	public void Update_WindowChangeOnly_Recomputes()
	{
		var cache = new RegulationFieldAggregateCache();
		RegulationTrailPoint[] trail = [Point(-0.5, 0.2), Point(0.5, 0.8), Point(0.5, 0.8)];
		cache.Update(trail, version: 1, xBuckets: 2, yBuckets: 2, heatmapWindow: int.MaxValue);
		Assert.AreEqual(3, cache.Density.TotalCount);

		cache.Update(trail, version: 1, xBuckets: 2, yBuckets: 2, heatmapWindow: 1);
		Assert.AreEqual(1, cache.Density.TotalCount, "a window change recomputes even at the same version");
	}

	[TestMethod]
	public void Update_NullTrail_Throws() =>
		Assert.Throws<ArgumentNullException>(() =>
			new RegulationFieldAggregateCache().Update(null!, 1, 2, 2, int.MaxValue));

	[TestMethod]
	public void Update_ZeroBuckets_Throws()
	{
		var cache = new RegulationFieldAggregateCache();
		Assert.Throws<ArgumentOutOfRangeException>(() => cache.Update([], 1, 0, 2, int.MaxValue));
		Assert.Throws<ArgumentOutOfRangeException>(() => cache.Update([], 1, 2, 0, int.MaxValue));
	}
}
