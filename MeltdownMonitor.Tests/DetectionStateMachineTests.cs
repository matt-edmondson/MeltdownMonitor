using MeltdownMonitor.Core.Beats;
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
		// Pin the pre-flip mechanics so these state-machine tests stay deterministic
		// after the production defaults move to clinical best practice (Additive / 2).
		// Veto + count 1 = the single-sample / veto behaviour these tests exercise.
		LfHfCorroborationMode = LfHfCorroborationMode.Veto,
		SevereDropConfirmationCount = 1,
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
	public void ContactLost_SevereDrop_IsIgnoredAndNoAlertFires()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		AlertPayload? fired = null;
		detector.AlertFired += p => fired = p;

		detector.Process(NormalSample(start), baselineIsWarm: true); // → Watching

		// A severe drop while the sensor is off-body must not drive the state machine.
		var gated = detector.Process(
			SeverelySample(start.AddSeconds(5)), baselineIsWarm: true, contact: SensorContactStatus.NotDetected);

		Assert.AreEqual(DetectorState.Watching, gated);
		Assert.IsNull(fired, "Contact-lost data must never raise an alert.");
	}

	[TestMethod]
	public void ContactRestored_ResumesNormalDetection()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		AlertPayload? fired = null;
		detector.AlertFired += p => fired = p;

		detector.Process(NormalSample(start), baselineIsWarm: true); // → Watching
		detector.Process(SeverelySample(start.AddSeconds(5)), baselineIsWarm: true, contact: SensorContactStatus.NotDetected);

		// Once contact returns, the same severe drop fires as it normally would.
		var restored = detector.Process(
			SeverelySample(start.AddSeconds(10)), baselineIsWarm: true, contact: SensorContactStatus.Detected);

		Assert.AreEqual(DetectorState.Alerting, restored);
		Assert.IsNotNull(fired);
	}

	[TestMethod]
	public void ContactLost_DuringAlerting_DoesNotCountAsRecovery()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		detector.Process(NormalSample(start), baselineIsWarm: true);
		detector.Process(SeverelySample(start.AddSeconds(5)), baselineIsWarm: true);
		Assert.AreEqual(DetectorState.Alerting, detector.State);

		// "Recovered"-looking samples while off-body are untrustworthy — a dropped
		// sensor reads like calm. They must not satisfy the recovery hold.
		DetectorState? state = null;
		for (int i = 2; i <= 8; i++)
		{
			state = detector.Process(
				NormalSample(start.AddSeconds(i * 5)), baselineIsWarm: true, contact: SensorContactStatus.NotDetected);
		}

		Assert.AreEqual(DetectorState.Alerting, state, "Contact-lost samples must not end an alert.");

		// With contact restored, sustained recovery proceeds to Cooldown as usual.
		detector.Process(NormalSample(start.AddSeconds(45)), baselineIsWarm: true, contact: SensorContactStatus.Detected);
		var recovered = detector.Process(NormalSample(start.AddSeconds(55)), baselineIsWarm: true, contact: SensorContactStatus.Detected);

		Assert.AreEqual(DetectorState.Cooldown, recovered);
	}

	[TestMethod]
	public void ContactLost_ResetsInProgressWarningStreak()
	{
		var detector = new DysregulationDetector(FastThresholds);
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

		detector.Process(NormalSample(start), baselineIsWarm: true); // → Watching

		// Stressed conditions accumulate from t=5s but haven't yet held 30s by t=30s.
		for (int i = 1; i <= 6; i++)
		{
			detector.Process(StressedSample(start.AddSeconds(i * 5)), baselineIsWarm: true);
		}
		Assert.AreEqual(DetectorState.Watching, detector.State);

		// A contact-lost sample at t=35s — which would otherwise complete the 30s hold —
		// resets the streak instead of escalating to Warning.
		var gated = detector.Process(
			StressedSample(start.AddSeconds(35)), baselineIsWarm: true, contact: SensorContactStatus.NotDetected);
		Assert.AreEqual(DetectorState.Watching, gated);

		// Clean stressed data resumes; the streak must re-accumulate from t=40s, so a
		// sample just after wouldn't yet escalate…
		var resumed = detector.Process(StressedSample(start.AddSeconds(40)), baselineIsWarm: true);
		Assert.AreEqual(DetectorState.Watching, resumed, "The streak should have reset, delaying the Warning.");

		// …but once a fresh 30s hold elapses (by t=70s) it escalates as normal.
		DetectorState? last = null;
		for (int i = 9; i <= 14; i++)
		{
			last = detector.Process(StressedSample(start.AddSeconds(i * 5)), baselineIsWarm: true);
		}
		Assert.AreEqual(DetectorState.Warning, last);
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

	private static HrvSample StressedWithLfHf(DateTimeOffset ts, double lfHfRatio, double baselineLfHf)
	{
		// Core Warning conditions met (RMSSD 40% below, HR 20% above), with extended LF/HF present.
		return new HrvSample(ts, 30, 20, 84, 50, 70, DetectorState.Watching)
		{
			BaselineLfHfRatio = baselineLfHf,
			Extended = new ExtendedHrvMetrics(0, 0, lfHfRatio, 0, 0, 0.4, 0),
		};
	}

	[TestMethod]
	public void VetoMode_LfHfNotElevated_SuppressesWarning()
	{
		var detector = new DysregulationDetector(FastThresholds); // fixture pins LfHfCorroborationMode.Veto
		var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		detector.Process(NormalSample(start), baselineIsWarm: true);

		// Core met but LF/HF flat (ratio == baseline) → veto blocks Warning.
		DetectorState? last = null;
		for (int i = 1; i <= 10; i++)
		{
			last = detector.Process(StressedWithLfHf(start.AddSeconds(i * 5), lfHfRatio: 1.5, baselineLfHf: 1.5), baselineIsWarm: true);
		}

		Assert.AreEqual(DetectorState.Watching, last, "Veto mode must suppress the Warning when LF/HF isn't elevated.");
	}

	[TestMethod]
	public void AdditiveMode_LfHfNotElevated_StillWarns()
	{
		var thresholds = FastThresholds with { LfHfCorroborationMode = LfHfCorroborationMode.Additive };
		var detector = new DysregulationDetector(thresholds);
		var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		detector.Process(NormalSample(start), baselineIsWarm: true);

		DetectorState? last = null;
		for (int i = 1; i <= 10; i++)
		{
			last = detector.Process(StressedWithLfHf(start.AddSeconds(i * 5), lfHfRatio: 1.5, baselineLfHf: 1.5), baselineIsWarm: true);
		}

		Assert.AreEqual(DetectorState.Warning, last, "Additive mode must not let a flat LF/HF veto a core-satisfied Warning.");
	}

	[TestMethod]
	public void SevereDropConfirmation_One_FiresOnFirstSample()
	{
		var detector = new DysregulationDetector(FastThresholds); // fixture pins SevereDropConfirmationCount 1
		var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		detector.Process(NormalSample(start), baselineIsWarm: true);

		var state = detector.Process(SeverelySample(start.AddSeconds(5)), baselineIsWarm: true);

		Assert.AreEqual(DetectorState.Alerting, state);
	}

	[TestMethod]
	public void ProductionDefaults_FollowClinicalBestPractice()
	{
		// The 2026-06-01 clinical audit flipped these defaults from preserve-today's-behaviour
		// toward clinical best practice: LF/HF must not veto a core-satisfied early Warning, and
		// the false-positive-prone immediate-severe path needs two consecutive in-contact samples.
		var defaults = new DetectionThresholds();

		Assert.AreEqual(LfHfCorroborationMode.Additive, defaults.LfHfCorroborationMode,
			"LF/HF should strengthen confidence, not veto an early Warning (its 5-minute window lags onset).");
		Assert.AreEqual(2, defaults.SevereDropConfirmationCount,
			"Two consecutive severe samples reject a transient breath-hold/Valsalva RMSSD collapse.");
	}

	[TestMethod]
	public void SevereDropConfirmation_Two_RequiresTwoConsecutive()
	{
		var thresholds = FastThresholds with { SevereDropConfirmationCount = 2 };
		var detector = new DysregulationDetector(thresholds);
		var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		detector.Process(NormalSample(start), baselineIsWarm: true);

		var afterFirst = detector.Process(SeverelySample(start.AddSeconds(5)), baselineIsWarm: true);
		Assert.AreEqual(DetectorState.Watching, afterFirst, "One severe sample must not fire when confirmation is 2.");

		var afterSecond = detector.Process(SeverelySample(start.AddSeconds(10)), baselineIsWarm: true);
		Assert.AreEqual(DetectorState.Alerting, afterSecond, "Second consecutive severe sample fires.");
	}
}
