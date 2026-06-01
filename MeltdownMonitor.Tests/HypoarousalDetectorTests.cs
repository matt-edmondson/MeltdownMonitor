using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class HypoarousalDetectorTests
{
	private static readonly HypoarousalThresholds Fast = new()
	{
		EnterSignal = 0.5,
		ExitSignal = 0.3,
		EnterHoldDuration = TimeSpan.FromSeconds(30),
		RecoveryDuration = TimeSpan.FromSeconds(30),
		CooldownDuration = TimeSpan.FromSeconds(60),
	};

	private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static HrvSample Sample(DateTimeOffset ts, double rmssd, double meanHr) =>
		new(ts, rmssd, Pnn50: 20, meanHr, BaselineRmssd: 50, BaselineHr: 70, DetectorState.Watching);

	// Signal 0: HR at baseline, RMSSD healthy — ordinary regulation.
	private static HrvSample Normal(DateTimeOffset ts) => Sample(ts, rmssd: 50, meanHr: 70);

	// Signal ~0.70: HR 25% below baseline (52.5 bpm), RMSSD 30% of baseline (15 ms) — above EnterSignal.
	private static HrvSample Low(DateTimeOffset ts) => Sample(ts, rmssd: 15, meanHr: 52.5);

	[TestMethod]
	public void InitialState_IsIdle()
	{
		Assert.AreEqual(HypoarousalState.Idle, new HypoarousalDetector(Fast).State);
	}

	[TestMethod]
	public void BaselineNotWarm_RemainsIdle()
	{
		var detector = new HypoarousalDetector(Fast);
		detector.Process(Low(Start), baselineIsWarm: false);
		Assert.AreEqual(HypoarousalState.Idle, detector.State);
	}

	[TestMethod]
	public void BaselineWarm_TransitionsToMonitoring()
	{
		var detector = new HypoarousalDetector(Fast);
		detector.Process(Normal(Start), baselineIsWarm: true);
		Assert.AreEqual(HypoarousalState.Monitoring, detector.State);
	}

	[TestMethod]
	public void NormalSamples_StayInMonitoring()
	{
		var detector = new HypoarousalDetector(Fast);
		for (int i = 0; i < 20; i++)
		{
			detector.Process(Normal(Start.AddSeconds(i * 5)), baselineIsWarm: true);
		}
		Assert.AreEqual(HypoarousalState.Monitoring, detector.State);
	}

	[TestMethod]
	public void SustainedLowArousal_EntersLowArousalAndFiresGentleAlert()
	{
		var detector = new HypoarousalDetector(Fast);
		AlertPayload? fired = null;
		detector.AlertFired += p => fired = p;

		detector.Process(Normal(Start), baselineIsWarm: true); // → Monitoring

		HypoarousalState? last = null;
		for (int i = 1; i <= 7; i++) // Low from t+5s to t+35s → 30s hold elapses
		{
			last = detector.Process(Low(Start.AddSeconds(i * 5)), baselineIsWarm: true);
		}

		Assert.AreEqual(HypoarousalState.LowArousal, last);
		Assert.IsTrue(detector.IsEpisodeActive);
		Assert.IsNotNull(fired, "Entering LowArousal must fire an alert.");
		Assert.AreEqual(AlertKind.Hypoarousal, fired.Kind, "A low-arousal alert must be labeled Hypoarousal for gentle routing.");
	}

	[TestMethod]
	public void BriefLowDip_DoesNotEnterLowArousal()
	{
		var detector = new HypoarousalDetector(Fast);
		AlertPayload? fired = null;
		detector.AlertFired += p => fired = p;

		detector.Process(Normal(Start), baselineIsWarm: true);

		// Only 15s of low signal — short of the 30s hold — then it clears.
		for (int i = 1; i <= 3; i++)
		{
			detector.Process(Low(Start.AddSeconds(i * 5)), baselineIsWarm: true);
		}
		var cleared = detector.Process(Normal(Start.AddSeconds(20)), baselineIsWarm: true);

		Assert.AreEqual(HypoarousalState.Monitoring, cleared);
		Assert.IsNull(fired, "A brief dip must not raise an alert.");
	}

	[TestMethod]
	public void LowArousal_SustainedRecovery_ReturnsToMonitoring()
	{
		var detector = EnteredLowArousal(out _);

		// Sustained clearing for the full recovery hold (30s) → back to Monitoring.
		HypoarousalState? state = null;
		for (int i = 8; i <= 14; i++) // Normal from t+40s to t+70s
		{
			state = detector.Process(Normal(Start.AddSeconds(i * 5)), baselineIsWarm: true);
		}

		Assert.AreEqual(HypoarousalState.Monitoring, state);
		Assert.IsFalse(detector.IsEpisodeActive);
	}

	[TestMethod]
	public void LowArousal_TransientClearing_DoesNotExit()
	{
		var detector = EnteredLowArousal(out _);

		// One clearing sample, then the signal drops again before the recovery hold elapses.
		detector.Process(Normal(Start.AddSeconds(40)), baselineIsWarm: true);
		var resumed = detector.Process(Low(Start.AddSeconds(45)), baselineIsWarm: true);

		Assert.AreEqual(HypoarousalState.LowArousal, resumed, "A lone clearing sample is not recovery.");
	}

	[TestMethod]
	public void ContactLost_LowSignal_IsIgnoredAndNoEntry()
	{
		var detector = new HypoarousalDetector(Fast);
		AlertPayload? fired = null;
		detector.AlertFired += p => fired = p;

		detector.Process(Normal(Start), baselineIsWarm: true);

		// Off-body data reads like collapse, but must not drive the machine.
		HypoarousalState? last = null;
		for (int i = 1; i <= 12; i++)
		{
			last = detector.Process(Low(Start.AddSeconds(i * 5)), baselineIsWarm: true, contact: SensorContactStatus.NotDetected);
		}

		Assert.AreEqual(HypoarousalState.Monitoring, last);
		Assert.IsNull(fired, "Contact-lost data must never raise a hypoarousal alert.");
	}

	[TestMethod]
	public void ContactLost_ResetsInProgressEnterStreak()
	{
		var detector = new HypoarousalDetector(Fast);
		detector.Process(Normal(Start), baselineIsWarm: true);

		// Low signal accumulates from t+5s but hasn't yet held 30s by t+30s.
		for (int i = 1; i <= 6; i++)
		{
			detector.Process(Low(Start.AddSeconds(i * 5)), baselineIsWarm: true);
		}
		Assert.AreEqual(HypoarousalState.Monitoring, detector.State);

		// A contact-lost sample at t+35s — which would otherwise complete the hold — resets the streak.
		var gated = detector.Process(Low(Start.AddSeconds(35)), baselineIsWarm: true, contact: SensorContactStatus.NotDetected);
		Assert.AreEqual(HypoarousalState.Monitoring, gated);

		// Clean low data resumes; the streak re-accumulates from t+40s, so it shouldn't have entered yet…
		var resumed = detector.Process(Low(Start.AddSeconds(40)), baselineIsWarm: true);
		Assert.AreEqual(HypoarousalState.Monitoring, resumed, "The streak should have reset, delaying entry.");

		// …but a fresh 30s hold (by t+70s) enters as normal.
		HypoarousalState? last = null;
		for (int i = 9; i <= 14; i++)
		{
			last = detector.Process(Low(Start.AddSeconds(i * 5)), baselineIsWarm: true);
		}
		Assert.AreEqual(HypoarousalState.LowArousal, last);
	}

	[TestMethod]
	public void ContactLost_DuringLowArousal_DoesNotCountAsExit()
	{
		var detector = EnteredLowArousal(out _);

		// "Recovered"-looking samples while off-body are untrustworthy — they must not satisfy the
		// recovery hold and end the episode early.
		HypoarousalState? state = null;
		for (int i = 8; i <= 16; i++)
		{
			state = detector.Process(Normal(Start.AddSeconds(i * 5)), baselineIsWarm: true, contact: SensorContactStatus.NotDetected);
		}
		Assert.AreEqual(HypoarousalState.LowArousal, state, "Contact-lost samples must not end an episode.");

		// With contact restored, sustained clearing proceeds to Monitoring as usual.
		for (int i = 17; i <= 24; i++)
		{
			state = detector.Process(Normal(Start.AddSeconds(i * 5)), baselineIsWarm: true, contact: SensorContactStatus.Detected);
		}
		Assert.AreEqual(HypoarousalState.Monitoring, state);
	}

	[TestMethod]
	public void AlertCooldown_SuppressesRapidReentryAlert()
	{
		// A long cooldown so the second entry — which happens well within ~2 minutes — is debounced.
		var thresholds = Fast with { CooldownDuration = TimeSpan.FromMinutes(10) };
		var detector = new HypoarousalDetector(thresholds);
		int alertCount = 0;
		detector.AlertFired += _ => alertCount++;

		detector.Process(Normal(Start), baselineIsWarm: true);

		// First episode: enter (alert 1), then sustained clearing back to Monitoring.
		for (int i = 1; i <= 7; i++) // Low t+5..t+35 → enter
		{
			detector.Process(Low(Start.AddSeconds(i * 5)), baselineIsWarm: true);
		}
		Assert.AreEqual(HypoarousalState.LowArousal, detector.State);
		for (int i = 8; i <= 14; i++) // Normal t+40..t+70 → exit
		{
			detector.Process(Normal(Start.AddSeconds(i * 5)), baselineIsWarm: true);
		}
		Assert.AreEqual(HypoarousalState.Monitoring, detector.State);

		// Second episode within the cooldown window: re-enters the state…
		HypoarousalState? last = null;
		for (int i = 15; i <= 21; i++) // Low t+75..t+105 → re-enter
		{
			last = detector.Process(Low(Start.AddSeconds(i * 5)), baselineIsWarm: true);
		}
		Assert.AreEqual(HypoarousalState.LowArousal, last, "The state still tracks the second episode.");
		Assert.AreEqual(1, alertCount, "…but the alert is debounced by the cooldown.");
	}

	[TestMethod]
	public void StateChangedEvent_Fires_OnTransition()
	{
		var detector = new HypoarousalDetector(Fast);
		var states = new List<HypoarousalState>();
		detector.StateChanged += s => states.Add(s);

		detector.Process(Normal(Start), baselineIsWarm: true);

		Assert.IsTrue(states.Contains(HypoarousalState.Monitoring));
	}

	[TestMethod]
	public void LiveThresholdEdit_IsHonouredByDetector()
	{
		// Mirrors the dysregulation detector's live-edit contract: a settings change must reach the
		// detector without reconstructing it.
		HypoarousalThresholds current = Fast with { EnterSignal = 0.99 }; // unreachable by the Low signal (~0.70)
		var detector = new HypoarousalDetector(() => current);

		detector.Process(Normal(Start), baselineIsWarm: true);
		for (int i = 1; i <= 10; i++) // sustained Low, but EnterSignal too high → no entry
		{
			detector.Process(Low(Start.AddSeconds(i * 5)), baselineIsWarm: true);
		}
		Assert.AreEqual(HypoarousalState.Monitoring, detector.State, "An unreachable EnterSignal must not enter.");

		current = Fast with { EnterSignal = 0.5 }; // user lowers the bar live

		HypoarousalState? last = null;
		for (int i = 11; i <= 18; i++) // fresh sustained Low → now enters under the edited threshold
		{
			last = detector.Process(Low(Start.AddSeconds(i * 5)), baselineIsWarm: true);
		}
		Assert.AreEqual(HypoarousalState.LowArousal, last, "The detector must read the edited threshold, not a stale snapshot.");
	}

	[TestMethod]
	public void ProductionDefault_EnterSignal_CatchesSubSevereLowArousal()
	{
		// Decision A: the default enter threshold sits below 0.5 so the detector catches sustained
		// *sub-severe* low arousal — the regime the severe dysregulation path never reaches.
		Assert.AreEqual(0.35, new HypoarousalThresholds().EnterSignal, 1e-9);

		var thresholds = new HypoarousalThresholds // production signal levels, fast timing for the test
		{
			EnterHoldDuration = TimeSpan.FromSeconds(30),
			RecoveryDuration = TimeSpan.FromSeconds(30),
		};
		var detector = new HypoarousalDetector(thresholds);

		// RMSSD 38% below baseline (31 ms) — never trips the 50% severe path — with HR 25% below
		// (52.5 bpm). Signal ≈ 0.38, above the 0.35 default.
		HrvSample SubSevere(DateTimeOffset ts) => Sample(ts, rmssd: 31, meanHr: 52.5);

		detector.Process(Normal(Start), baselineIsWarm: true);
		HypoarousalState? last = null;
		for (int i = 1; i <= 7; i++)
		{
			last = detector.Process(SubSevere(Start.AddSeconds(i * 5)), baselineIsWarm: true);
		}

		Assert.AreEqual(HypoarousalState.LowArousal, last,
			"The default threshold must catch sustained sub-severe low arousal.");
	}

	[TestMethod]
	public void AlertPayload_DefaultsToHyperarousalKind()
	{
		// The additive default keeps DysregulationDetector's 4-arg construction (and the persisted
		// alert history) unchanged.
		var payload = new AlertPayload(Start, "core", 20, 50);
		Assert.AreEqual(AlertKind.Hyperarousal, payload.Kind);
	}

	// Drives a detector into LowArousal (alert fires at t+35s) and returns it.
	private static HypoarousalDetector EnteredLowArousal(out AlertPayload? fired)
	{
		var detector = new HypoarousalDetector(Fast);
		AlertPayload? captured = null;
		detector.AlertFired += p => captured = p;

		detector.Process(Normal(Start), baselineIsWarm: true);
		for (int i = 1; i <= 7; i++)
		{
			detector.Process(Low(Start.AddSeconds(i * 5)), baselineIsWarm: true);
		}

		fired = captured;
		return detector;
	}
}
