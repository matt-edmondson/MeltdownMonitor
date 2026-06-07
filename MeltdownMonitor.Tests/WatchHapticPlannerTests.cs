using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Regulation;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.Tests;

[TestClass]
public class WatchHapticPlannerTests
{
	// Warm, in-contact, not paused, Both mode, medium ceiling, 6 brpm.
	private static WatchHapticOptions Opts(
		WatchHapticMode mode = WatchHapticMode.Both,
		WatchHapticIntensity ceiling = WatchHapticIntensity.Medium,
		bool anchorWhenCalm = false) =>
		new(mode,
			WatchHapticOptions.CeilingFor(ceiling),
			WatchHapticOptions.PeriodForBrpm(6.0),
			WatchHapticOptions.DefaultConfidenceFloor,
			anchorWhenCalm,
			WatchHapticOptions.DefaultAnchorIntensity);

	private static RegulationReading Reading(double index, double confidence = 1.0) =>
		new(index, VariabilityQuality: 1.0, confidence, LobeRoundness: 0.5, LfHfBalance: 0.0);

	[TestMethod]
	public void ColdBaseline_BelowConfidenceFloor_IsSilent()
	{
		var plan = WatchHapticPlanner.Plan(
			Reading(index: 0.8, confidence: 0.2), DetectorState.Warning, contactOk: true, isPaused: false, Opts());

		Assert.AreEqual(0.0, plan.Intensity, 1e-9);
	}

	[TestMethod]
	public void Paused_IsSilent()
	{
		var plan = WatchHapticPlanner.Plan(
			Reading(0.8), DetectorState.Warning, contactOk: true, isPaused: true, Opts());

		Assert.AreEqual(0.0, plan.Intensity, 1e-9);
	}

	[TestMethod]
	public void OffSkin_IsSilent()
	{
		var plan = WatchHapticPlanner.Plan(
			Reading(0.8), DetectorState.Warning, contactOk: false, isPaused: false, Opts());

		Assert.AreEqual(0.0, plan.Intensity, 1e-9);
	}

	[TestMethod]
	public void AtOrBelowBaseline_IsSilentByDefault()
	{
		Assert.AreEqual(0.0,
			WatchHapticPlanner.Plan(Reading(0.0), DetectorState.Watching, true, false, Opts()).Intensity, 1e-9);
		Assert.AreEqual(0.0,
			WatchHapticPlanner.Plan(Reading(-0.5), DetectorState.Watching, true, false, Opts()).Intensity, 1e-9);
	}

	[TestMethod]
	public void AtOrBelowBaseline_PlaysFaintAnchor_WhenOptedIn()
	{
		var plan = WatchHapticPlanner.Plan(
			Reading(-0.3), DetectorState.Watching, true, false, Opts(anchorWhenCalm: true));

		Assert.AreEqual(WatchHapticOptions.DefaultAnchorIntensity, plan.Intensity, 1e-9);
	}

	[TestMethod]
	public void AboveBaseline_IntensityIsProportionalToIndex()
	{
		double ceiling = WatchHapticOptions.CeilingFor(WatchHapticIntensity.Medium);

		var low = WatchHapticPlanner.Plan(Reading(0.25), DetectorState.Watching, true, false, Opts());
		var high = WatchHapticPlanner.Plan(Reading(0.75), DetectorState.Warning, true, false, Opts());

		Assert.AreEqual(0.25 * ceiling, low.Intensity, 1e-9);
		Assert.AreEqual(0.75 * ceiling, high.Intensity, 1e-9);
		Assert.IsTrue(high.Intensity > low.Intensity);
	}

	[TestMethod]
	public void Intensity_IsClampedToTheCeiling_EvenForSevereReadings()
	{
		double ceiling = WatchHapticOptions.CeilingFor(WatchHapticIntensity.Low);

		// Index well past 1 (severe) must not exceed the chosen ceiling.
		var plan = WatchHapticPlanner.Plan(
			Reading(3.0), DetectorState.Alerting, true, false, Opts(ceiling: WatchHapticIntensity.Low));

		Assert.AreEqual(ceiling, plan.Intensity, 1e-9);
	}

	[TestMethod]
	public void Cadence_IsMonotonicallyCalming_NeverShortensWithArousal()
	{
		var opts = Opts();
		double calmPeriod = WatchHapticPlanner.Plan(Reading(0.1), DetectorState.Watching, true, false, opts).BreathPeriodSeconds;
		double severePeriod = WatchHapticPlanner.Plan(Reading(0.95), DetectorState.Alerting, true, false, opts).BreathPeriodSeconds;

		// Higher arousal grows salience (intensity), never speeds the breath up.
		Assert.AreEqual(calmPeriod, severePeriod, 1e-9);
		Assert.IsTrue(severePeriod > 0);
	}

	[TestMethod]
	public void StateCuesMode_SilencesTheContinuousPacer()
	{
		var plan = WatchHapticPlanner.Plan(
			Reading(0.8), DetectorState.Warning, true, false, Opts(mode: WatchHapticMode.StateCues));

		Assert.AreEqual(0.0, plan.Intensity, 1e-9);
	}

	[TestMethod]
	public void EscalationCues_FireOnEntryIntoEpisodeTiers()
	{
		Assert.AreEqual(WatchHapticCue.EscalatedToWarning,
			WatchHapticPlanner.CueForTransition(DetectorState.Watching, DetectorState.Warning, WatchHapticMode.Both));
		Assert.AreEqual(WatchHapticCue.EscalatedToAlerting,
			WatchHapticPlanner.CueForTransition(DetectorState.Warning, DetectorState.Alerting, WatchHapticMode.Both));
	}

	[TestMethod]
	public void RecoveryCue_FiresLeavingAnEpisodeForACalmerState()
	{
		Assert.AreEqual(WatchHapticCue.Recovered,
			WatchHapticPlanner.CueForTransition(DetectorState.Alerting, DetectorState.Cooldown, WatchHapticMode.Both));
		Assert.AreEqual(WatchHapticCue.Recovered,
			WatchHapticPlanner.CueForTransition(DetectorState.Warning, DetectorState.Watching, WatchHapticMode.Both));
	}

	[TestMethod]
	public void NoCue_ForNonEscalatingOrUnchangedTransitions()
	{
		Assert.IsNull(WatchHapticPlanner.CueForTransition(DetectorState.Idle, DetectorState.Watching, WatchHapticMode.Both));
		Assert.IsNull(WatchHapticPlanner.CueForTransition(DetectorState.Warning, DetectorState.Warning, WatchHapticMode.Both));
	}

	[TestMethod]
	public void PacedBreathMode_SuppressesDiscreteCues()
	{
		Assert.IsNull(WatchHapticPlanner.CueForTransition(
			DetectorState.Watching, DetectorState.Alerting, WatchHapticMode.PacedBreath));
	}
}
