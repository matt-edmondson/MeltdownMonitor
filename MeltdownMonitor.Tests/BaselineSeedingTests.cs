using MeltdownMonitor.Core.Baseline;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class BaselineSeedingTests
{
	private static HrvSample Sample(
		double rmssd,
		double hr,
		DateTimeOffset ts,
		DetectorState state = DetectorState.Watching,
		double? lfHf = null)
	{
		var sample = new HrvSample(ts, rmssd, Pnn50: 20, hr, BaselineRmssd: 0, BaselineHr: 0, state);
		if (lfHf is { } v)
		{
			sample = sample with
			{
				Extended = new ExtendedHrvMetrics(
					LfPowerMs2: 0, HfPowerMs2: 0, LfHfRatio: v,
					SD1: 0, SD2: 0, SD1SD2Ratio: 0, Sdnn: 0)
			};
		}

		return sample;
	}

	private static List<HrvSample> RecentClean()
	{
		var now = DateTimeOffset.UtcNow;
		var list = new List<HrvSample>();
		for (int i = 0; i < 20; i++)
		{
			list.Add(Sample(40 + i, 60 + i, now.AddMinutes(-30).AddSeconds(i), lfHf: 1.0 + (i * 0.1)));
		}

		return list;
	}

	[TestMethod]
	public void SeedFromHistory_WarmStarts_AndSeedsMedian()
	{
		var tracker = new BaselineHrvTracker();
		tracker.SeedFromHistory(RecentClean());

		Assert.IsTrue(tracker.IsWarm, "Enough recent clean samples should warm-start the tracker.");
		Assert.AreEqual(49.5, tracker.BaselineRmssd, 0.001);
		Assert.AreEqual(69.5, tracker.BaselineHr, 0.001);
	}

	[TestMethod]
	public void SeedFromHistory_ExcludesEpisodeSamples_FromMedian()
	{
		var samples = RecentClean();
		var now = DateTimeOffset.UtcNow;
		samples.Add(Sample(1, 200, now.AddMinutes(-5), DetectorState.Alerting));
		samples.Add(Sample(1, 200, now.AddMinutes(-4), DetectorState.Warning));

		var tracker = new BaselineHrvTracker();
		tracker.SeedFromHistory(samples);

		Assert.AreEqual(49.5, tracker.BaselineRmssd, 0.001);
		Assert.AreEqual(69.5, tracker.BaselineHr, 0.001);
	}

	[TestMethod]
	public void SeedFromHistory_StaleHistory_StaysCold()
	{
		var old = DateTimeOffset.UtcNow.AddHours(-3);
		var samples = new List<HrvSample>();
		for (int i = 0; i < 50; i++)
		{
			samples.Add(Sample(50, 70, old.AddSeconds(i)));
		}

		var tracker = new BaselineHrvTracker();
		tracker.SeedFromHistory(samples);

		Assert.IsFalse(tracker.IsWarm, "History older than the warm-start window must not warm-start.");
		Assert.AreEqual(0, tracker.BaselineRmssd, 0.001, "Stale history does not seed the live EWMA.");
	}

	[TestMethod]
	public void SeedFromHistory_NoHistory_IsNoOp()
	{
		var tracker = new BaselineHrvTracker();
		tracker.SeedFromHistory([]);

		Assert.IsFalse(tracker.IsWarm);
		Assert.AreEqual(0, tracker.BaselineRmssd, 0.001);
	}
}
