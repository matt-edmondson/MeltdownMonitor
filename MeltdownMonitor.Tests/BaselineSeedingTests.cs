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

	[TestMethod]
	public void Guardrail_PreventsBaselineDriftingBelowAnchorBand()
	{
		// Warm-start with RMSSD median 49.5, HR median 69.5 (anchor == warm-start here).
		var tracker = new BaselineHrvTracker();
		tracker.SeedFromHistory(RecentClean());

		// Feed a long run of crushed RMSSD; the EWMA would head toward 5 but the
		// guardrail floors it at anchor * (1 - 0.40) = 49.5 * 0.6 = 29.7.
		var now = DateTimeOffset.UtcNow;
		for (int i = 0; i < 2000; i++)
		{
			tracker.Update(Sample(5, 69.5, now.AddSeconds(i)));
		}

		Assert.AreEqual(29.7, tracker.BaselineRmssd, 0.1,
			"Baseline must not drop below 40% under the anchor.");
	}

	[TestMethod]
	public void Guardrail_PreventsBaselineDriftingAboveAnchorBand()
	{
		var tracker = new BaselineHrvTracker();
		tracker.SeedFromHistory(RecentClean());

		var now = DateTimeOffset.UtcNow;
		for (int i = 0; i < 2000; i++)
		{
			tracker.Update(Sample(49.5, 200, now.AddSeconds(i)));
		}

		// HR ceiling = anchor 69.5 * 1.40 = 97.3.
		Assert.AreEqual(97.3, tracker.BaselineHr, 0.1,
			"Baseline HR must not rise above 40% over the anchor.");
	}

	[TestMethod]
	public void Guardrail_NoAnchor_DoesNotClamp()
	{
		// No seeding => no anchor => behaves exactly like cold start.
		var tracker = new BaselineHrvTracker();
		tracker.Update(Sample(5, 70, DateTimeOffset.UtcNow));

		Assert.AreEqual(5, tracker.BaselineRmssd, 0.001);
	}

	[TestMethod]
	public void Guardrail_RespectsCustomDriftBand()
	{
		// Tighter 10% band → floor = anchor 49.5 * 0.90 = 44.55.
		var tracker = new BaselineHrvTracker { MaxAnchorDrift = 0.10 };
		tracker.SeedFromHistory(RecentClean());

		var now = DateTimeOffset.UtcNow;
		for (int i = 0; i < 2000; i++)
		{
			tracker.Update(Sample(5, 49.5, now.AddSeconds(i)));
		}

		Assert.AreEqual(44.55, tracker.BaselineRmssd, 0.1);
	}

	[TestMethod]
	public void WarmStart_RespectsCustomMinSamples()
	{
		// RecentClean() has 20 samples; requiring 50 must prevent a warm start.
		var tracker = new BaselineHrvTracker { MinWarmStartSamples = 50 };
		tracker.SeedFromHistory(RecentClean());

		Assert.IsFalse(tracker.IsWarm);
	}

	[TestMethod]
	public void CustomAlpha_ChangesConvergenceSpeed()
	{
		var fast = new BaselineHrvTracker { RmssdHrAlpha = 0.5 };
		var slow = new BaselineHrvTracker { RmssdHrAlpha = 0.01 };

		var now = DateTimeOffset.UtcNow;
		fast.Update(Sample(100, 70, now));
		slow.Update(Sample(100, 70, now));
		for (int i = 1; i <= 10; i++)
		{
			fast.Update(Sample(0, 70, now.AddSeconds(i)));
			slow.Update(Sample(0, 70, now.AddSeconds(i)));
		}

		Assert.IsTrue(fast.BaselineRmssd < slow.BaselineRmssd,
			"A higher alpha converges toward the new value faster.");
	}
}
