using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

/// <summary>
/// Runs the dysregulation and hypoarousal detectors over the same stream — the interaction a
/// single-detector unit test cannot see. Guards the resolution of the severe-path pre-emption:
/// the immediate-severe path fires far sooner than the hypoarousal hold, so during a collapse the
/// severe alert is the one that reaches the user — it must therefore route gently when HR is low.
/// </summary>
[TestClass]
public class DetectorInteractionTests
{
	private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static HrvSample Sample(DateTimeOffset ts, double rmssd, double meanHr) =>
		new(ts, rmssd, Pnn50: 20, meanHr, BaselineRmssd: 50, BaselineHr: 70, DetectorState.Watching);

	private static HrvSample Normal(DateTimeOffset ts) => Sample(ts, rmssd: 50, meanHr: 70);

	// RMSSD 60% below baseline (trips the 50% severe path) with HR 25% *below* baseline = a collapse.
	private static HrvSample Collapse(DateTimeOffset ts) => Sample(ts, rmssd: 20, meanHr: 52.5);

	// RMSSD 60% below baseline with HR 30% *above* baseline = a classic sympathetic meltdown.
	private static HrvSample Meltdown(DateTimeOffset ts) => Sample(ts, rmssd: 20, meanHr: 91);

	[TestMethod]
	public void DeepCollapse_SevereAlertRoutesHypoarousal_WhileHypoDetectorStillHolding()
	{
		var dys = new DysregulationDetector(new DetectionThresholds());   // production defaults (severe confirm 2)
		var hypo = new HypoarousalDetector(new HypoarousalThresholds());  // production defaults (60s enter hold)

		AlertPayload? dysAlert = null;
		AlertPayload? hypoAlert = null;
		dys.AlertFired += p => dysAlert = p;
		hypo.AlertFired += p => hypoAlert = p;

		dys.Process(Normal(Start), baselineIsWarm: true);
		hypo.Process(Normal(Start), baselineIsWarm: true);

		DetectorState? dysState = null;
		for (int i = 1; i <= 3; i++) // collapse from t+5s to t+15s — severe confirms on the 2nd (~t+10s)
		{
			var s = Collapse(Start.AddSeconds(i * 5));
			dysState = dys.Process(s, baselineIsWarm: true);
			hypo.Process(s, baselineIsWarm: true);
		}

		// The severe path fired in ~10s and — because HR is below baseline — routed gently.
		Assert.AreEqual(DetectorState.Alerting, dysState);
		Assert.IsNotNull(dysAlert);
		Assert.AreEqual(AlertKind.Hypoarousal, dysAlert.Kind,
			"A severe RMSSD collapse with HR below baseline must route gently, not as a jarring meltdown.");

		// The hypoarousal detector is still mid-hold (60s) — it would have been pre-empted, which is
		// exactly why the severe path carries the gentle routing.
		Assert.AreEqual(HypoarousalState.Monitoring, hypo.State);
		Assert.IsNull(hypoAlert);
	}

	[TestMethod]
	public void SevereDrop_WithHrAboveBaseline_StaysHyperarousal()
	{
		var dys = new DysregulationDetector(new DetectionThresholds());
		AlertPayload? alert = null;
		dys.AlertFired += p => alert = p;

		dys.Process(Normal(Start), baselineIsWarm: true);
		for (int i = 1; i <= 3; i++)
		{
			dys.Process(Meltdown(Start.AddSeconds(i * 5)), baselineIsWarm: true);
		}

		Assert.IsNotNull(alert);
		Assert.AreEqual(AlertKind.Hyperarousal, alert.Kind,
			"A severe drop with HR above baseline is a sympathetic meltdown — it stays Hyperarousal.");
	}

	[TestMethod]
	public void SustainedCollapse_HypoDetectorEventuallyEntersAndAlertsGently()
	{
		var hypo = new HypoarousalDetector(new HypoarousalThresholds()); // 60s enter hold
		AlertPayload? hypoAlert = null;
		hypo.AlertFired += p => hypoAlert = p;

		hypo.Process(Normal(Start), baselineIsWarm: true);

		HypoarousalState? last = null;
		for (int i = 1; i <= 13; i++) // collapse from t+5s to t+65s → 60s hold elapses
		{
			last = hypo.Process(Collapse(Start.AddSeconds(i * 5)), baselineIsWarm: true);
		}

		Assert.AreEqual(HypoarousalState.LowArousal, last);
		Assert.IsNotNull(hypoAlert);
		Assert.AreEqual(AlertKind.Hypoarousal, hypoAlert.Kind);
	}
}
