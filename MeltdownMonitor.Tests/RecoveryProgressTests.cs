using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class RecoveryProgressTests
{
	private static readonly DetectionThresholds FastThresholds = new()
	{
		RmssdWarningDropFraction = 0.30,
		HrWarningRiseFraction = 0.15,
		WarningHoldDuration = TimeSpan.FromSeconds(30),
		AlertingEscalationDuration = TimeSpan.FromSeconds(60),
		RmssdAlertingDropFraction = 0.50,
		CooldownDuration = TimeSpan.FromSeconds(10),
		RmssdRecoveryDropFraction = 0.10,
		HrRecoveryRiseFraction = 0.05,
		RecoveryHoldDuration = TimeSpan.FromSeconds(10),
		// These tests fire on a single severe sample; pin count 1 so they stay
		// deterministic after the production default moves to 2. (No extended
		// metrics here, so LfHfCorroborationMode is never consulted — pinned for
		// parity with DetectionStateMachineTests.)
		LfHfCorroborationMode = LfHfCorroborationMode.Veto,
		SevereDropConfirmationCount = 1,
	};

	private static HrvSample Sample(
		DateTimeOffset timestamp,
		double rmssd = 50,
		double meanHr = 70,
		double baselineRmssd = 50,
		double baselineHr = 70) =>
		new(timestamp, Rmssd: rmssd, Pnn50: 20, MeanHr: meanHr,
			BaselineRmssd: baselineRmssd, BaselineHr: baselineHr, State: DetectorState.Watching);

	// RMSSD 60% below baseline → immediate Alerting.
	private static HrvSample Severe(DateTimeOffset timestamp) =>
		Sample(timestamp, rmssd: 50 * 0.40, meanHr: 70 * 1.30);

	// ── The Overall mapping ────────────────────────────────────────────────

	[TestMethod]
	public void Overall_Inactive_IsZero()
	{
		Assert.AreEqual(0.0, RecoveryProgress.Inactive.Overall, 1e-9);
		Assert.IsFalse(RecoveryProgress.Inactive.IsActive);
	}

	[TestMethod]
	public void Overall_MetricStage_OccupiesFirstHalf()
	{
		// No hold yet → progress is half the metric proximity (0 → 0.5).
		Assert.AreEqual(0.25, new RecoveryProgress(0.5, 0.0, true).Overall, 1e-9);
		Assert.AreEqual(0.5, new RecoveryProgress(1.0, 0.0, true).Overall, 1e-9);
	}

	[TestMethod]
	public void Overall_HoldStage_OccupiesSecondHalf()
	{
		// Once holding, progress climbs from 0.5 to 1.0 with the hold timer.
		Assert.AreEqual(0.75, new RecoveryProgress(1.0, 0.5, true).Overall, 1e-9);
		Assert.AreEqual(1.0, new RecoveryProgress(1.0, 1.0, true).Overall, 1e-9);
	}

	// ── Detector integration ───────────────────────────────────────────────

	[TestMethod]
	public void Recovery_IsInactive_WhileWatching()
	{
		var detector = new DysregulationDetector(FastThresholds);
		detector.Process(Sample(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)), baselineIsWarm: true);

		Assert.IsFalse(detector.Recovery.IsActive);
		Assert.AreEqual(0.0, detector.Recovery.Overall, 1e-9);
	}

	[TestMethod]
	public void Recovery_IsActiveButZero_WhenStillDysregulatedInAlerting()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		detector.Process(Sample(start), baselineIsWarm: true);
		detector.Process(Severe(start.AddSeconds(5)), baselineIsWarm: true);

		Assert.AreEqual(DetectorState.Alerting, detector.State);
		Assert.IsTrue(detector.Recovery.IsActive);
		// Metrics still well beyond the Warning trigger → no proximity, no hold.
		Assert.AreEqual(0.0, detector.Recovery.MetricProximity, 1e-9);
		Assert.AreEqual(0.0, detector.Recovery.Overall, 1e-9);
	}

	[TestMethod]
	public void Recovery_MetricProximity_RampsAsMetricsCloseOnTheBand()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		detector.Process(Sample(start), baselineIsWarm: true);
		detector.Process(Severe(start.AddSeconds(5)), baselineIsWarm: true);
		Assert.AreEqual(DetectorState.Alerting, detector.State);

		// RMSSD 20% below baseline sits midway between the recovery (10%) and warning
		// (30%) edges → proximity 0.5; HR is back at baseline → proximity 1. The worse
		// gates, so metric proximity is 0.5 and the hold has not started.
		detector.Process(Sample(start.AddSeconds(10), rmssd: 50 * 0.80, meanHr: 70), baselineIsWarm: true);

		Assert.AreEqual(DetectorState.Alerting, detector.State, "Partial return is not yet recovery.");
		Assert.AreEqual(0.5, detector.Recovery.MetricProximity, 1e-6);
		Assert.AreEqual(0.0, detector.Recovery.HoldProgress, 1e-9);
		Assert.AreEqual(0.25, detector.Recovery.Overall, 1e-6);
	}

	[TestMethod]
	public void Recovery_HoldProgress_AccruesOnceMetricsAreInBand()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		detector.Process(Sample(start), baselineIsWarm: true);
		detector.Process(Severe(start.AddSeconds(5)), baselineIsWarm: true);
		Assert.AreEqual(DetectorState.Alerting, detector.State);

		// First in-band sample starts the streak (held 0s) → metric stage tops out at 0.5.
		detector.Process(Sample(start.AddSeconds(10)), baselineIsWarm: true);
		Assert.AreEqual(1.0, detector.Recovery.MetricProximity, 1e-9);
		Assert.AreEqual(0.0, detector.Recovery.HoldProgress, 1e-9);
		Assert.AreEqual(0.5, detector.Recovery.Overall, 1e-9);

		// 5s into the 10s hold → halfway through the hold stage.
		detector.Process(Sample(start.AddSeconds(15)), baselineIsWarm: true);
		Assert.AreEqual(DetectorState.Alerting, detector.State);
		Assert.AreEqual(0.5, detector.Recovery.HoldProgress, 1e-6);
		Assert.AreEqual(0.75, detector.Recovery.Overall, 1e-6);
	}

	[TestMethod]
	public void Recovery_ReturnsToInactive_OnceTheAlertClears()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		detector.Process(Sample(start), baselineIsWarm: true);
		detector.Process(Severe(start.AddSeconds(5)), baselineIsWarm: true);

		// Sustained recovery for the full hold → steps down to Cooldown.
		DetectorState? state = null;
		for (int i = 2; i <= 4; i++)
		{
			state = detector.Process(Sample(start.AddSeconds(i * 5)), baselineIsWarm: true);
		}

		Assert.AreEqual(DetectorState.Cooldown, state);
		Assert.IsFalse(detector.Recovery.IsActive, "Recovery indicator clears once the episode ends.");
		Assert.AreEqual(0.0, detector.Recovery.Overall, 1e-9);
	}

	[TestMethod]
	public void Recovery_GoesInactive_WhenContactLost()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		detector.Process(Sample(start), baselineIsWarm: true);
		detector.Process(Severe(start.AddSeconds(5)), baselineIsWarm: true);
		Assert.IsTrue(detector.Recovery.IsActive);

		// Off-body data is untrustworthy — the indicator must not read it as progress.
		detector.Process(Sample(start.AddSeconds(10)), baselineIsWarm: true, contact: SensorContactStatus.NotDetected);

		Assert.IsFalse(detector.Recovery.IsActive);
		Assert.AreEqual(0.0, detector.Recovery.Overall, 1e-9);
	}
}
