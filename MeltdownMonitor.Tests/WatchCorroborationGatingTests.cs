using MeltdownMonitor.Core.Baseline;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class WatchCorroborationGatingTests
{
	private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static HrvSample Sample(double rmssd, double meanHr, double baselineRmssd = 50, double baselineHr = 70,
		DetectorState state = DetectorState.Watching) =>
		new(T0, rmssd, Pnn50: 20, meanHr, baselineRmssd, baselineHr, state);

	// Fire-on-first-sample so a single severe sample is decisive.
	private static DetectionThresholds Thresholds(bool useWatchCorroboration = true) => new()
	{
		SevereDropConfirmationCount = 1,
		UseWatchCorroboration = useWatchCorroboration,
	};

	[TestMethod]
	public void Detector_SevereDrop_GatedByConflict()
	{
		var detector = new DysregulationDetector(Thresholds());
		bool fired = false;
		detector.AlertFired += _ => fired = true;

		// RMSSD 60% below baseline would immediately alert — but the watch contradicts the strap HR.
		var state = detector.Process(Sample(rmssd: 20, meanHr: 91), baselineIsWarm: true,
			watch: WatchCorroboration.Conflicted);

		Assert.IsFalse(fired);
		Assert.AreNotEqual(DetectorState.Alerting, state);
	}

	[TestMethod]
	public void Detector_SevereDrop_FiresWhenConfirmed()
	{
		var detector = new DysregulationDetector(Thresholds());
		bool fired = false;
		detector.AlertFired += _ => fired = true;

		var state = detector.Process(Sample(rmssd: 20, meanHr: 91), baselineIsWarm: true,
			watch: WatchCorroboration.Confirmed);

		Assert.IsTrue(fired);
		Assert.AreEqual(DetectorState.Alerting, state);
	}

	[TestMethod]
	public void Detector_UnknownVerdict_NeverGates()
	{
		var detector = new DysregulationDetector(Thresholds());
		bool fired = false;
		detector.AlertFired += _ => fired = true;

		// No paired watch → Unknown → behaves exactly like a no-watch build.
		detector.Process(Sample(rmssd: 20, meanHr: 91), baselineIsWarm: true,
			watch: WatchCorroboration.Unknown);

		Assert.IsTrue(fired);
	}

	[TestMethod]
	public void Detector_CorroborationDisabled_IgnoresConflict()
	{
		var detector = new DysregulationDetector(Thresholds(useWatchCorroboration: false));
		bool fired = false;
		detector.AlertFired += _ => fired = true;

		detector.Process(Sample(rmssd: 20, meanHr: 91), baselineIsWarm: true,
			watch: WatchCorroboration.Conflicted);

		Assert.IsTrue(fired);
	}

	[TestMethod]
	public void Detector_Conflict_ClearsBuildingWarningStreak()
	{
		// A warning streak that was accumulating must not escalate while the watch contradicts the strap.
		var thresholds = new DetectionThresholds { WarningHoldDuration = TimeSpan.FromSeconds(30) };
		var detector = new DysregulationDetector(thresholds);
		bool fired = false;
		detector.AlertFired += _ => fired = true;

		HrvSample Stressed(DateTimeOffset ts) =>
			new(ts, Rmssd: 30, Pnn50: 20, MeanHr: 84, BaselineRmssd: 50, BaselineHr: 70, DetectorState.Watching);

		// Arm the warning streak at T0 (clean), then a conflict arrives mid-streak.
		detector.Process(Stressed(T0), baselineIsWarm: true);
		detector.Process(Stressed(T0.AddSeconds(15)), baselineIsWarm: true, watch: WatchCorroboration.Conflicted);

		// Even after the hold window elapses, the streak was cleared by the conflict, so no escalation.
		var state = detector.Process(Stressed(T0.AddSeconds(31)), baselineIsWarm: true);

		Assert.IsFalse(fired, "A conflict must clear the building warning streak.");
		Assert.AreEqual(DetectorState.Watching, state);
	}

	[TestMethod]
	public void Baseline_FrozenOnConflict_WhenOptedIn()
	{
		var tracker = new BaselineHrvTracker { RmssdHrAlpha = 0.5, FreezeOnWatchConflict = true };
		tracker.Update(Sample(rmssd: 50, meanHr: 70)); // anchors at 50 / 70

		tracker.Update(Sample(rmssd: 30, meanHr: 90), watch: WatchCorroboration.Conflicted);

		Assert.AreEqual(50, tracker.BaselineRmssd, 1e-9);
		Assert.AreEqual(70, tracker.BaselineHr, 1e-9);
	}

	[TestMethod]
	public void Baseline_NotFrozenOnConflict_WhenOptionOff()
	{
		// Default off: the gate-only behaviour — a conflict does not freeze the baseline.
		var tracker = new BaselineHrvTracker { RmssdHrAlpha = 0.5 };
		tracker.Update(Sample(rmssd: 50, meanHr: 70));

		tracker.Update(Sample(rmssd: 30, meanHr: 90), watch: WatchCorroboration.Conflicted);

		// EWMA at α=0.5 still folds the sample in: (0.5 × 50) + (0.5 × 30) = 40.
		Assert.AreEqual(40, tracker.BaselineRmssd, 1e-9);
		Assert.AreEqual(80, tracker.BaselineHr, 1e-9);
	}

	[TestMethod]
	public void Baseline_NotFrozenWhenConfirmed_EvenIfOptedIn()
	{
		var tracker = new BaselineHrvTracker { RmssdHrAlpha = 0.5, FreezeOnWatchConflict = true };
		tracker.Update(Sample(rmssd: 50, meanHr: 70));

		// A Confirmed (or Unknown) verdict never freezes — only a conflict does.
		tracker.Update(Sample(rmssd: 30, meanHr: 90), watch: WatchCorroboration.Confirmed);

		Assert.AreEqual(40, tracker.BaselineRmssd, 1e-9);
	}

	[TestMethod]
	public void Threshold_FreezeBaselineOnWatchConflict_DefaultsOff()
	{
		Assert.IsFalse(new DetectionThresholds().FreezeBaselineOnWatchConflict);
	}
}
