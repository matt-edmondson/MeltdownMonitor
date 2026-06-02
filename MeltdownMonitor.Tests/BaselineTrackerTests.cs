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

	[TestMethod]
	public void ColdStart_SymptomaticFirstSample_SeedsFromWarmUpMedian_NotFirstSample()
	{
		// Audit B: launching already dysregulated must not bake the symptom into the baseline.
		// The first sample is symptomatic (low RMSSD, high HR); the rest of the warm-up is healthy.
		var tracker = new BaselineHrvTracker();
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		tracker.Update(MakeSample(rmssd: 15, meanHr: 95, timestamp: start)); // activated at launch

		for (int i = 1; i <= 130; i++) // ~10.8 min of healthy data → completes the warm-up
		{
			tracker.Update(MakeSample(rmssd: 50, meanHr: 70, timestamp: start.AddSeconds(i * 5)));
		}

		Assert.IsTrue(tracker.IsWarm);
		Assert.IsTrue(tracker.IsColdCalibrated, "A no-history cold start that self-calibrates must flag it.");
		// The robust median of the warm-up window is the healthy 50/70, NOT the EWMA value
		// (~31 ms) that the symptomatic first sample would otherwise drag the baseline to.
		Assert.AreEqual(50.0, tracker.BaselineRmssd, 0.001, "Cold baseline must be the warm-up median, not the symptomatic first sample.");
		Assert.AreEqual(70.0, tracker.BaselineHr, 0.001);
	}

	[TestMethod]
	public void WarmStartedFromHistory_IsNotColdCalibrated()
	{
		// A history anchor means we are NOT self-calibrating cold; the EWMA + anchor guardrail
		// already protects the baseline, so the cold-seed path (and its flag) must not engage.
		var tracker = new BaselineHrvTracker();
		var now = DateTimeOffset.UtcNow;
		var history = new List<HrvSample>();
		for (int i = 0; i < 20; i++)
		{
			history.Add(MakeSample(rmssd: 50, meanHr: 70, timestamp: now.AddMinutes(-30).AddSeconds(i)));
		}

		tracker.SeedFromHistory(history);

		Assert.IsTrue(tracker.IsWarm);
		Assert.IsFalse(tracker.IsColdCalibrated, "Warm-started from history is anchored, not cold-calibrated.");
	}

	[TestMethod]
	public void WarmStartHrBaseline_SeedsHr_LeavesRmssdCold_AndNotWarm()
	{
		// Audit B: HealthKit HR is a legitimate resting-HR estimate but carries no beat-to-beat
		// detail, so warm-start must seed HR only and leave the parasympathetic RMSSD baseline to
		// warm up from real live beats — never fabricate it.
		var tracker = new BaselineHrvTracker();

		tracker.WarmStartHrBaseline([72, 72, 72, 72]);

		Assert.AreEqual(0, tracker.BaselineRmssd, 0.001,
			"HR-only warm-start must not fabricate an RMSSD baseline.");
		Assert.AreEqual(72.0, tracker.BaselineHr, 0.001);
		Assert.IsFalse(tracker.IsWarm, "HR-only warm-start must not arm the detector; RMSSD still warms live.");
	}

	[TestMethod]
	public void WarmStartHrBaseline_UsesRobustMedian_NotMean()
	{
		// A spike (e.g. a stray exercise reading) must not drag the seed; median is robust.
		var tracker = new BaselineHrvTracker();

		tracker.WarmStartHrBaseline([70, 70, 70, 200]);

		Assert.AreEqual(70.0, tracker.BaselineHr, 0.001,
			"Seed must be the median (70), not the spike-inflated mean.");
	}

	[TestMethod]
	public void WarmStartHrBaseline_EmptyOrNonPositive_IsNoOp()
	{
		var tracker = new BaselineHrvTracker();

		tracker.WarmStartHrBaseline([]);
		tracker.WarmStartHrBaseline([0, -5, -1]);

		Assert.AreEqual(0, tracker.BaselineHr, 0.001);
		Assert.AreEqual(0, tracker.BaselineRmssd, 0.001);
		Assert.IsFalse(tracker.IsWarm);
	}

	[TestMethod]
	public void WarmStartHrBaseline_NoOpWhenAlreadyWarmFromHistory()
	{
		// A real-RMSSD history seed (C) must win: the coarser HealthKit HR estimate must not
		// clobber a baseline that is already warm-started from genuine beat-to-beat history.
		var tracker = new BaselineHrvTracker();
		var now = DateTimeOffset.UtcNow;
		var history = new List<HrvSample>();
		for (int i = 0; i < 20; i++)
		{
			history.Add(MakeSample(rmssd: 50, meanHr: 70, timestamp: now.AddMinutes(-30).AddSeconds(i)));
		}
		tracker.SeedFromHistory(history);

		tracker.WarmStartHrBaseline([200, 200, 200]);

		Assert.AreEqual(70.0, tracker.BaselineHr, 0.001,
			"An already-warm history baseline must not be overwritten by the HealthKit HR seed.");
		Assert.IsTrue(tracker.IsWarm);
	}

	[TestMethod]
	public void WarmStartHr_ThenLiveBeats_RmssdSeedsFromWarmUpMedian_HrPreserved_AndColdCalibrated()
	{
		// First-ever launch: HealthKit anchors HR; RMSSD has no history anchor, so it warms up
		// from the live window's robust median and the cold-calibration provenance flag is set.
		var tracker = new BaselineHrvTracker();
		tracker.WarmStartHrBaseline([72, 72, 72, 72]);

		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
		for (int i = 0; i <= 130; i++) // ~10.8 min → completes the live warm-up
		{
			tracker.Update(MakeSample(rmssd: 50, meanHr: 70, timestamp: start.AddSeconds(i * 5)));
		}

		Assert.IsTrue(tracker.IsWarm);
		Assert.AreEqual(50.0, tracker.BaselineRmssd, 0.001,
			"RMSSD must seed from the live warm-up median (real beats), not a fabricated value.");
		Assert.IsTrue(tracker.BaselineHr is > 70.0 and <= 72.0,
			$"HR must stay anchored near the HealthKit seed (72), drifting toward live 70; got {tracker.BaselineHr:F2}.");
		Assert.IsTrue(tracker.IsColdCalibrated,
			"A self-calibrated RMSSD baseline with no parasympathetic history anchor must flag low confidence.");
	}

	[TestMethod]
	public void WarmStartHr_ThenSymptomaticFirstBeat_RmssdSeedsFromHealthyMedian_NotTheSymptom()
	{
		// The audit-B safety property in the B2 path: HealthKit anchors HR, then the user happens to
		// launch already activated. The symptomatic first live beat must NOT become the RMSSD baseline —
		// the robust warm-up median discards it. (Uniform data would hide a regression here.)
		var tracker = new BaselineHrvTracker();
		tracker.WarmStartHrBaseline([72, 72, 72, 72]);

		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
		tracker.Update(MakeSample(rmssd: 15, meanHr: 95, timestamp: start)); // activated at launch

		for (int i = 1; i <= 130; i++) // ~10.8 min of healthy data completes the warm-up
		{
			tracker.Update(MakeSample(rmssd: 50, meanHr: 70, timestamp: start.AddSeconds(i * 5)));
		}

		Assert.IsTrue(tracker.IsWarm);
		Assert.AreEqual(50.0, tracker.BaselineRmssd, 0.001,
			"RMSSD must seed from the healthy warm-up median, not the symptomatic first beat (15).");
		Assert.IsTrue(tracker.IsColdCalibrated,
			"No RMSSD history anchor → self-calibrated cold → badge must flag low confidence.");
	}

	[TestMethod]
	public void StaleHistoryAnchor_ThenSymptomaticWarmUp_BaselinePinnedNearAnchor_NotDraggedToSymptom()
	{
		// Why B2+C beats B2 alone: a relaunch after a gap finds only stale history, so SeedFromHistory
		// sets the real anchor but cannot warm-start. If the live warm-up is then symptomatic, the
		// ±MaxAnchorDrift clamp pins the baseline near the real resting anchor instead of letting the
		// symptom define it — so the detector arms correctly, not blind. (The first-ever-launch case,
		// with no anchor at all, has no such protection — that residual is intentional, badge-flagged.)
		var tracker = new BaselineHrvTracker();
		var now = DateTimeOffset.UtcNow;

		// History older than the 60-min warm-start window: anchors get set, but no live warm-start.
		var stale = new List<HrvSample>();
		for (int i = 0; i < 20; i++)
		{
			stale.Add(MakeSample(rmssd: 50, meanHr: 70, timestamp: now.AddMinutes(-90).AddSeconds(i)));
		}
		tracker.SeedFromHistory(stale);
		Assert.IsFalse(tracker.IsWarm, "Stale-only history anchors but must not warm-start.");

		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
		for (int i = 0; i <= 130; i++) // sustained symptomatic warm-up
		{
			tracker.Update(MakeSample(rmssd: 15, meanHr: 95, timestamp: start.AddSeconds(i * 5)));
		}

		Assert.IsTrue(tracker.IsWarm);
		// Anchor 50, MaxAnchorDrift 0.40 → clamped floor 30. The symptom (15) must not win.
		Assert.AreEqual(30.0, tracker.BaselineRmssd, 1.0,
			"The real anchor must pin RMSSD near 0.6×anchor, not let the symptomatic warm-up define it.");
		Assert.IsFalse(tracker.IsColdCalibrated,
			"A real RMSSD history anchor is present, so this is not a cold calibration.");
	}

	[TestMethod]
	public void Reset_ClearsColdCalibration()
	{
		var tracker = new BaselineHrvTracker();
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
		for (int i = 0; i <= 130; i++)
		{
			tracker.Update(MakeSample(rmssd: 50, meanHr: 70, timestamp: start.AddSeconds(i * 5)));
		}
		Assert.IsTrue(tracker.IsColdCalibrated);

		tracker.Reset();

		Assert.IsFalse(tracker.IsColdCalibrated, "Reset must clear the cold-calibration provenance flag.");
	}
}
