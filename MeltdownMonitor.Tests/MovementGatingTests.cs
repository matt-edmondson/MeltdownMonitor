using MeltdownMonitor.Core.Baseline;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Motion;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MovementGatingTests
{
	private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static HrvSample Sample(double rmssd, double meanHr, double baselineRmssd = 50, double baselineHr = 70,
		DetectorState state = DetectorState.Watching) =>
		new(T0, rmssd, Pnn50: 20, meanHr, baselineRmssd, baselineHr, state);

	// Fire-on-first-sample thresholds so a single severe sample is decisive.
	private static DetectionThresholds Thresholds(bool useMovementGating = true) => new()
	{
		SevereDropConfirmationCount = 1,
		UseMovementGating = useMovementGating,
		MovementGateLevel = MovementLevel.Moderate,
	};

	[TestMethod]
	public void Detector_SevereDrop_GatedByMovement()
	{
		var detector = new DysregulationDetector(Thresholds());
		bool fired = false;
		detector.AlertFired += _ => fired = true;

		// RMSSD 60% below baseline would immediately alert — but the body is moving vigorously.
		var state = detector.Process(Sample(rmssd: 20, meanHr: 91), baselineIsWarm: true,
			contact: default, movement: MovementLevel.Vigorous);

		Assert.IsFalse(fired);
		Assert.AreNotEqual(DetectorState.Alerting, state);
	}

	[TestMethod]
	public void Detector_SevereDrop_FiresWhenStill()
	{
		var detector = new DysregulationDetector(Thresholds());
		bool fired = false;
		detector.AlertFired += _ => fired = true;

		var state = detector.Process(Sample(rmssd: 20, meanHr: 91), baselineIsWarm: true,
			contact: default, movement: MovementLevel.Still);

		Assert.IsTrue(fired);
		Assert.AreEqual(DetectorState.Alerting, state);
	}

	[TestMethod]
	public void Detector_LightMovement_DoesNotGate()
	{
		var detector = new DysregulationDetector(Thresholds());
		bool fired = false;
		detector.AlertFired += _ => fired = true;

		// Light fidgeting is below the Moderate gate, so agitation still alerts.
		detector.Process(Sample(rmssd: 20, meanHr: 91), baselineIsWarm: true,
			contact: default, movement: MovementLevel.Light);

		Assert.IsTrue(fired);
	}

	[TestMethod]
	public void Detector_MovementGatingDisabled_IgnoresMovement()
	{
		var detector = new DysregulationDetector(Thresholds(useMovementGating: false));
		bool fired = false;
		detector.AlertFired += _ => fired = true;

		detector.Process(Sample(rmssd: 20, meanHr: 91), baselineIsWarm: true,
			contact: default, movement: MovementLevel.Vigorous);

		Assert.IsTrue(fired);
	}

	[TestMethod]
	public void Baseline_FrozenWhileMoving()
	{
		var tracker = new BaselineHrvTracker { RmssdHrAlpha = 0.5 };
		tracker.Update(Sample(rmssd: 50, meanHr: 70)); // anchors at 50 / 70

		tracker.Update(Sample(rmssd: 30, meanHr: 90), movement: MovementLevel.Moderate);

		Assert.AreEqual(50, tracker.BaselineRmssd, 1e-9);
		Assert.AreEqual(70, tracker.BaselineHr, 1e-9);
	}

	[TestMethod]
	public void Baseline_UpdatesWhenStill()
	{
		var tracker = new BaselineHrvTracker { RmssdHrAlpha = 0.5 };
		tracker.Update(Sample(rmssd: 50, meanHr: 70));

		tracker.Update(Sample(rmssd: 30, meanHr: 90), movement: MovementLevel.Unknown);

		// EWMA at α=0.5: (0.5 × 50) + (0.5 × 30) = 40.
		Assert.AreEqual(40, tracker.BaselineRmssd, 1e-9);
		Assert.AreEqual(80, tracker.BaselineHr, 1e-9);
	}

	[TestMethod]
	public void Baseline_LightMovementDoesNotFreeze()
	{
		var tracker = new BaselineHrvTracker { RmssdHrAlpha = 0.5 };
		tracker.Update(Sample(rmssd: 50, meanHr: 70));

		tracker.Update(Sample(rmssd: 30, meanHr: 90), movement: MovementLevel.Light);

		Assert.AreEqual(40, tracker.BaselineRmssd, 1e-9);
	}
}
