using MeltdownMonitor.Core.Baseline;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class BaselineTrackerTests
{
	private static HrvSample MakeSample(
		double rmssd = 50,
		double meanHr = 70,
		DetectorState state = DetectorState.Watching,
		DateTimeOffset? timestamp = null)
	{
		return new HrvSample(
			timestamp ?? DateTimeOffset.UtcNow,
			rmssd,
			Pnn50: 20,
			meanHr,
			BaselineRmssd: rmssd,
			BaselineHr: meanHr,
			state);
	}

	[TestMethod]
	public void ColdStart_IsNotWarm()
	{
		var tracker = new BaselineHrvTracker();
		Assert.IsFalse(tracker.IsWarm);
	}

	[TestMethod]
	public void FirstSample_SetsInitialBaseline()
	{
		var tracker = new BaselineHrvTracker();
		var sample = MakeSample(rmssd: 60, meanHr: 65);
		tracker.Update(sample);
		Assert.AreEqual(60, tracker.BaselineRmssd, 0.001);
		Assert.AreEqual(65, tracker.BaselineHr, 0.001);
	}

	[TestMethod]
	public void EwmaConverges_TowardNewValue()
	{
		var tracker = new BaselineHrvTracker();
		tracker.Update(MakeSample(rmssd: 60));

		// Feed many samples with rmssd=0; baseline should drift toward 0 (slowly)
		for (int i = 0; i < 100; i++)
		{
			tracker.Update(MakeSample(rmssd: 0));
		}

		Assert.IsTrue(tracker.BaselineRmssd < 60, "Baseline should have drifted below initial 60");
		Assert.IsTrue(tracker.BaselineRmssd > 0, "Baseline should not have dropped all the way to 0 in 100 steps");
	}

	[TestMethod]
	public void Warning_Sample_NotUsedToUpdateBaseline()
	{
		var tracker = new BaselineHrvTracker();
		tracker.Update(MakeSample(rmssd: 60));
		double beforeBaseline = tracker.BaselineRmssd;

		// Warning-state sample with extreme RMSSD should not move the baseline
		tracker.Update(MakeSample(rmssd: 5, state: DetectorState.Warning));
		Assert.AreEqual(beforeBaseline, tracker.BaselineRmssd, 0.001);
	}

	[TestMethod]
	public void Alerting_Sample_NotUsedToUpdateBaseline()
	{
		var tracker = new BaselineHrvTracker();
		tracker.Update(MakeSample(rmssd: 60));
		double beforeBaseline = tracker.BaselineRmssd;

		tracker.Update(MakeSample(rmssd: 5, state: DetectorState.Alerting));
		Assert.AreEqual(beforeBaseline, tracker.BaselineRmssd, 0.001);
	}

	[TestMethod]
	public void ContactLost_Sample_NotUsedToUpdateBaseline()
	{
		var tracker = new BaselineHrvTracker();
		tracker.Update(MakeSample(rmssd: 60));
		double beforeBaseline = tracker.BaselineRmssd;

		// An off-body sample with extreme RMSSD must not move the baseline.
		tracker.Update(MakeSample(rmssd: 5), SensorContactStatus.NotDetected);
		Assert.AreEqual(beforeBaseline, tracker.BaselineRmssd, 0.001);
	}

	[TestMethod]
	public void ContactDetected_Sample_StillUpdatesBaseline()
	{
		var tracker = new BaselineHrvTracker();
		tracker.Update(MakeSample(rmssd: 60));
		double beforeBaseline = tracker.BaselineRmssd;

		// A reading with confirmed contact is reliable and updates as normal.
		tracker.Update(MakeSample(rmssd: 5), SensorContactStatus.Detected);
		Assert.AreNotEqual(beforeBaseline, tracker.BaselineRmssd, 0.001,
			"A contact-confirmed sample should still move the baseline.");
	}

	[TestMethod]
	public void BecomeWarm_After10MinutesOfCleanSamples()
	{
		var tracker = new BaselineHrvTracker();
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		// Feed samples 5 seconds apart for 9 minutes — should NOT be warm yet
		for (int i = 0; i < 108; i++)
		{
			tracker.Update(MakeSample(timestamp: start.AddSeconds(i * 5)));
		}

		Assert.IsFalse(tracker.IsWarm, "Should not be warm before 10 minutes");

		// One more batch brings us past 10 minutes
		for (int i = 108; i < 130; i++)
		{
			tracker.Update(MakeSample(timestamp: start.AddSeconds(i * 5)));
		}

		Assert.IsTrue(tracker.IsWarm, "Should be warm after 10+ minutes");
	}

	[TestMethod]
	public void Reset_ClearsAllState()
	{
		var tracker = new BaselineHrvTracker();
		tracker.Update(MakeSample(rmssd: 60));
		tracker.Reset();
		Assert.IsFalse(tracker.IsWarm);
		Assert.AreEqual(0, tracker.BaselineRmssd);
	}
}
