using MeltdownMonitor.Core.Beats;

namespace MeltdownMonitor.Tests;

[TestClass]
public class RrArtifactFilterTests
{
	[TestMethod]
	public void CleanRr_WithinBounds_Accepted()
	{
		var filter = new RrArtifactFilter();
		Assert.IsFalse(filter.IsArtifact(800));
	}

	[TestMethod]
	public void RrBelowMinimum_Rejected()
	{
		var filter = new RrArtifactFilter();
		Assert.IsTrue(filter.IsArtifact(299));
	}

	[TestMethod]
	public void RrAboveMaximum_Rejected()
	{
		var filter = new RrArtifactFilter();
		Assert.IsTrue(filter.IsArtifact(2001));
	}

	[TestMethod]
	public void RrExactlyAtMinimum_Accepted()
	{
		var filter = new RrArtifactFilter();
		Assert.IsFalse(filter.IsArtifact(300));
	}

	[TestMethod]
	public void RrExactlyAtMaximum_Accepted()
	{
		var filter = new RrArtifactFilter();
		Assert.IsFalse(filter.IsArtifact(2000));
	}

	[TestMethod]
	public void Ectopic_DeviatesFromMedianByMoreThan25Percent_Rejected()
	{
		var filter = new RrArtifactFilter();
		// Seed with clean beats around 800ms
		filter.IsArtifact(800);
		filter.IsArtifact(810);
		filter.IsArtifact(790);
		// An ectopic at 400ms is 50% below median ≈ 800ms — should be rejected
		Assert.IsTrue(filter.IsArtifact(400));
	}

	[TestMethod]
	public void SlightVariation_WithinMedianWindow_Accepted()
	{
		var filter = new RrArtifactFilter();
		filter.IsArtifact(800);
		filter.IsArtifact(810);
		filter.IsArtifact(790);
		// 850ms is 6% above median ≈ 800 — within 25%
		Assert.IsFalse(filter.IsArtifact(850));
	}

	[TestMethod]
	public void First_TwoBeats_NotRejectedByMedianRule()
	{
		// With fewer than 2 clean beats in the window the median rule cannot fire.
		var filter = new RrArtifactFilter();
		Assert.IsFalse(filter.IsArtifact(800)); // first beat, no median yet
		Assert.IsFalse(filter.IsArtifact(900)); // second beat, only 1 in median window
	}

	[TestMethod]
	public void Reset_ClearsMedianWindow()
	{
		var filter = new RrArtifactFilter();
		filter.IsArtifact(800);
		filter.IsArtifact(810);
		filter.IsArtifact(790);
		filter.Reset();
		// After reset the median window is empty — 400ms passes the median rule again
		// (still within absolute bounds) and should be accepted.
		Assert.IsFalse(filter.IsArtifact(400));
	}

	[TestMethod]
	public void SustainedRegimeShift_RecoversAfterConsecutiveRejections()
	{
		var filter = new RrArtifactFilter();
		// Establish a stable ~800ms median.
		filter.IsArtifact(800);
		filter.IsArtifact(810);
		filter.IsArtifact(790);

		// An abrupt sustained drop to 590ms (≈26% step, in absolute bounds). The first few
		// are rejected; after MaxConsecutiveRejections (4) the filter re-seeds and accepts.
		Assert.IsTrue(filter.IsArtifact(590), "1st rejected");
		Assert.IsTrue(filter.IsArtifact(590), "2nd rejected");
		Assert.IsTrue(filter.IsArtifact(590), "3rd rejected");
		Assert.IsFalse(filter.IsArtifact(590), "4th accepted — regime shift, median re-seeded");

		// New level is now the baseline; subsequent 590s are clean.
		Assert.IsFalse(filter.IsArtifact(590));
	}

	[TestMethod]
	public void LoneEctopic_StillRejected_AfterRegimeShiftLogicAdded()
	{
		var filter = new RrArtifactFilter();
		filter.IsArtifact(800);
		filter.IsArtifact(810);
		filter.IsArtifact(790);
		Assert.IsTrue(filter.IsArtifact(400), "A single ectopic is still rejected.");
		// A clean beat resets the streak, so the next ectopic is again rejected.
		Assert.IsFalse(filter.IsArtifact(805));
		Assert.IsTrue(filter.IsArtifact(400));
	}
}
