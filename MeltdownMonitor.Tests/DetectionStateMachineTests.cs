using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class DetectionStateMachineTests
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
	};

	private static HrvSample NormalSample(
		DateTimeOffset? timestamp = null,
		double rmssd = 50,
		double meanHr = 70,
		double baselineRmssd = 50,
		double baselineHr = 70,
		DetectorState state = DetectorState.Watching)
	{
		return new HrvSample(
			Timestamp: timestamp ?? DateTimeOffset.UtcNow,
			Rmssd: rmssd,
			Pnn50: 20,
			MeanHr: meanHr,
			BaselineRmssd: baselineRmssd,
			BaselineHr: baselineHr,
			State: state);
	}

	private static HrvSample StressedSample(
		DateTimeOffset? timestamp = null,
		double baselineRmssd = 50,
		double baselineHr = 70)
	{
		// RMSSD 40% below baseline, HR 20% above → exceeds Warning threshold
		return NormalSample(timestamp,
			rmssd: baselineRmssd * 0.60,
			meanHr: baselineHr * 1.20,
			baselineRmssd,
			baselineHr);
	}

	private static HrvSample SeverelySample(
		DateTimeOffset? timestamp = null,
		double baselineRmssd = 50,
		double baselineHr = 70)
	{
		// RMSSD 60% below baseline → triggers immediate Alerting
		return NormalSample(timestamp,
			rmssd: baselineRmssd * 0.40,
			meanHr: baselineHr * 1.30,
			baselineRmssd,
			baselineHr);
	}

	[TestMethod]
	public void InitialState_IsIdle()
	{
		var detector = new DysregulationDetector(FastThresholds);
		Assert.AreEqual(DetectorState.Idle, detector.State);
	}

	[TestMethod]
	public void BaselineNotWarm_RemainsIdle()
	{
		var detector = new DysregulationDetector(FastThresholds);
		detector.Process(NormalSample(), baselineIsWarm: false);
		Assert.AreEqual(DetectorState.Idle, detector.State);
	}

	[TestMethod]
	public void BaselineWarm_TransitionsToWatching()
	{
		var detector = new DysregulationDetector(FastThresholds);
		detector.Process(NormalSample(), baselineIsWarm: true);
		Assert.AreEqual(DetectorState.Watching, detector.State);
	}

	[TestMethod]
	public void NormalSamples_StayInWatching()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		detector.Process(NormalSample(start), baselineIsWarm: true);

		for (int i = 1; i < 20; i++)
		{
			detector.Process(NormalSample(start.AddSeconds(i * 5)), baselineIsWarm: true);
		}

		Assert.AreEqual(DetectorState.Watching, detector.State);
	}

	[TestMethod]
	public void StressedSamplesHeldFor30s_TransitionToWarning()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		// Prime the state machine
		detector.Process(NormalSample(start), baselineIsWarm: true);

		// Feed stressed samples for 35 seconds
		DetectorState? lastState = null;
		for (int i = 1; i <= 8; i++)
		{
			lastState = detector.Process(StressedSample(start.AddSeconds(i * 5)), baselineIsWarm: true);
		}

		Assert.AreEqual(DetectorState.Warning, lastState);
	}

	[TestMethod]
	public void WarningConditionsClear_ReturnsToWatching()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		detector.Process(NormalSample(start), baselineIsWarm: true);
		for (int i = 1; i <= 8; i++)
		{
			detector.Process(StressedSample(start.AddSeconds(i * 5)), baselineIsWarm: true);
		}

		Assert.AreEqual(DetectorState.Warning, detector.State);

		// Normal samples — conditions no longer met
		var cleared = detector.Process(NormalSample(start.AddSeconds(45)), baselineIsWarm: true);
		Assert.AreEqual(DetectorState.Watching, cleared);
	}

	[TestMethod]
	public void SevereDrop_FromWatching_FiresAlertAndEntersAlerting()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		AlertPayload? firedAlert = null;
		detector.AlertFired += payload => firedAlert = payload;

		detector.Process(NormalSample(start), baselineIsWarm: true);
		detector.Process(SeverelySample(start.AddSeconds(5)), baselineIsWarm: true);

		Assert.IsNotNull(firedAlert);
		Assert.AreEqual(DetectorState.Alerting, detector.State);
	}

	[TestMethod]
	public void Alerting_HoldsWhileDysregulationPersists()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		detector.Process(NormalSample(start), baselineIsWarm: true);
		detector.Process(SeverelySample(start.AddSeconds(5)), baselineIsWarm: true);
		Assert.AreEqual(DetectorState.Alerting, detector.State);

		// Continued severe samples must keep the detector in Alerting.
		for (int i = 2; i <= 10; i++)
		{
			detector.Process(SeverelySample(start.AddSeconds(i * 5)), baselineIsWarm: true);
		}

		Assert.AreEqual(DetectorState.Alerting, detector.State);
	}

	[TestMethod]
	public void Alerting_SustainedRecovery_TransitionsToCooldown()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		detector.Process(NormalSample(start), baselineIsWarm: true);
		detector.Process(SeverelySample(start.AddSeconds(5)), baselineIsWarm: true);
		Assert.AreEqual(DetectorState.Alerting, detector.State);

		// Physiological recovery must be *sustained* (RecoveryHoldDuration = 10s).
		// First recovered sample at 10s, held through 20s → steps down to Cooldown.
		DetectorState? state = null;
		for (int i = 2; i <= 4; i++)
		{
			state = detector.Process(NormalSample(start.AddSeconds(i * 5)), baselineIsWarm: true);
		}

		Assert.AreEqual(DetectorState.Cooldown, state);
	}

	[TestMethod]
	public void Alerting_TransientReturnToBaseline_DoesNotEndAlert()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		detector.Process(NormalSample(start), baselineIsWarm: true);
		detector.Process(SeverelySample(start.AddSeconds(5)), baselineIsWarm: true);
		Assert.AreEqual(DetectorState.Alerting, detector.State);

		// A lone sample flicks back to baseline, then dysregulation resumes before
		// the recovery hold elapses. This is "returning to baseline", not recovery —
		// the detector must stay in Alerting.
		detector.Process(NormalSample(start.AddSeconds(10)), baselineIsWarm: true);
		var resumed = detector.Process(SeverelySample(start.AddSeconds(15)), baselineIsWarm: true);

		Assert.AreEqual(DetectorState.Alerting, resumed);
	}

	[TestMethod]
	public void CooldownExpires_TransitionsToWatching()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		detector.Process(NormalSample(start), baselineIsWarm: true);
		detector.Process(SeverelySample(start.AddSeconds(5)), baselineIsWarm: true);
		Assert.AreEqual(DetectorState.Alerting, detector.State);

		// Sustained recovery so the detector leaves Alerting and enters Cooldown.
		for (int i = 2; i <= 4; i++)
		{
			detector.Process(NormalSample(start.AddSeconds(i * 5)), baselineIsWarm: true);
		}
		Assert.AreEqual(DetectorState.Cooldown, detector.State);

		// Feed a sample after the cooldown period (10s) has elapsed.
		var afterCooldown = detector.Process(
			NormalSample(start.AddSeconds(35)), baselineIsWarm: true);

		Assert.AreEqual(DetectorState.Watching, afterCooldown);
	}

	[TestMethod]
	public void WarningEscalates_ToAlertingAfter60s()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		AlertPayload? firedAlert = null;
		var stateHistory = new List<DetectorState>();
		detector.AlertFired += payload => firedAlert = payload;
		detector.StateChanged += s => stateHistory.Add(s);

		detector.Process(NormalSample(start), baselineIsWarm: true);

		// Enter Warning at ~35s (conditions met at 5s, held for 30s)
		for (int i = 1; i <= 8; i++)
		{
			detector.Process(StressedSample(start.AddSeconds(i * 5)), baselineIsWarm: true);
		}

		Assert.AreEqual(DetectorState.Warning, detector.State);

		// Continue stressed for another 65s — the sustained Warning escalates and the
		// alert fires, entering Alerting. With dysregulation ongoing there is no
		// physiological recovery, so it holds in Alerting (does not skip to Cooldown).
		var warningStart = start.AddSeconds(40);
		for (int i = 1; i <= 14; i++)
		{
			detector.Process(StressedSample(warningStart.AddSeconds(i * 5)), baselineIsWarm: true);
		}

		Assert.IsNotNull(firedAlert, "Alert should have fired after 60s in Warning");
		Assert.IsTrue(stateHistory.Contains(DetectorState.Alerting), "Should have entered Alerting");
		Assert.IsFalse(stateHistory.Contains(DetectorState.Cooldown),
			"Must not reach Cooldown while dysregulation persists");
		Assert.AreEqual(DetectorState.Alerting, detector.State);
		StringAssert.Contains(firedAlert.TriggerReason, "Sustained");
	}

	[TestMethod]
	public void StateChangedEvent_Fires_OnTransition()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var states = new List<DetectorState>();
		detector.StateChanged += s => states.Add(s);

		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
		detector.Process(NormalSample(start), baselineIsWarm: true);

		Assert.IsTrue(states.Contains(DetectorState.Watching));
	}
}
