using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// User-chosen filter on what the watch renders, matching individual sensory
/// profiles (design doc §9): some users want only the continuous breath pacer,
/// some only discrete taps, some both.
/// </summary>
public enum WatchHapticMode
{
	/// <summary>Continuous paced-breath pacer only; discrete state cues suppressed.</summary>
	PacedBreath,

	/// <summary>Discrete state-change cues only; the continuous pacer stays silent.</summary>
	StateCues,

	/// <summary>Both the breath pacer and the discrete cues.</summary>
	Both,
}

/// <summary>
/// Ceiling for all haptic output (design doc §9). Even "Firm" stays gentle in
/// absolute terms — the product never jolts (§2); this only sets how salient the
/// proportional cue is allowed to become.
/// </summary>
public enum WatchHapticIntensity
{
	Low,
	Medium,
	Firm,
}

/// <summary>The resolved continuous-haptic plan for one reading.</summary>
/// <param name="Intensity">[0, 1]; 0 = silent.</param>
/// <param name="BreathPeriodSeconds">Paced-breath cycle length (always calming).</param>
public readonly record struct WatchHapticPlan(double Intensity, double BreathPeriodSeconds);

/// <summary>
/// Inputs to <see cref="WatchHapticPlanner"/> derived from <see cref="MobileSettings"/>
/// (plus tuning constants). Kept as a value so the planner stays pure and trivially
/// testable.
/// </summary>
/// <param name="Mode">Which outputs are enabled.</param>
/// <param name="IntensityCeiling">[0, 1] cap on continuous intensity.</param>
/// <param name="BreathPeriodSeconds">Paced-breath cycle length.</param>
/// <param name="ConfidenceFloor">Below this <see cref="RegulationReading.Confidence"/>
/// the watch is silent — a cue the app isn't sure about is worse than none (§2.2).</param>
/// <param name="AnchorWhenCalm">When true, a faint slow "anchor" breath plays even
/// at/below baseline; off by default (the watch gets out of the way when calm).</param>
/// <param name="AnchorIntensity">Intensity of that optional anchor breath.</param>
public readonly record struct WatchHapticOptions(
	WatchHapticMode Mode,
	double IntensityCeiling,
	double BreathPeriodSeconds,
	double ConfidenceFloor,
	bool AnchorWhenCalm,
	double AnchorIntensity)
{
	/// <summary>Confidence below which the watch stays silent (baseline not yet trustworthy).</summary>
	public const double DefaultConfidenceFloor = 0.5;

	/// <summary>Faint anchor-breath intensity when <see cref="AnchorWhenCalm"/> is on.</summary>
	public const double DefaultAnchorIntensity = 0.15;

	/// <summary>Maps the user's <see cref="WatchHapticIntensity"/> ceiling to [0, 1].</summary>
	public static double CeilingFor(WatchHapticIntensity intensity) => intensity switch
	{
		WatchHapticIntensity.Low => 0.40,
		WatchHapticIntensity.Medium => 0.70,
		WatchHapticIntensity.Firm => 1.00,
		_ => 0.40,
	};

	/// <summary>Seconds per full breath cycle for a given breaths-per-minute rate.</summary>
	public static double PeriodForBrpm(double breathsPerMinute) =>
		60.0 / Math.Clamp(breathsPerMinute, 3.0, 12.0);

	/// <summary>Builds options from the persisted watch-haptic settings.</summary>
	public static WatchHapticOptions From(MobileSettings settings) => new(
		settings.WatchHapticMode,
		CeilingFor(settings.WatchHapticIntensity),
		PeriodForBrpm(settings.WatchPacedBreathRate),
		DefaultConfidenceFloor,
		AnchorWhenCalm: false,
		DefaultAnchorIntensity);
}

/// <summary>
/// Pure mapping from a live <see cref="RegulationReading"/> + detector state to
/// the felt Regulation Field (design doc §7): a continuous, always-calming
/// breath pacer whose <i>salience</i> (intensity) — never its speed — grows with
/// arousal, plus discrete escalation/recovery cues. No watch, no I/O, so the
/// whole policy is unit-tested without a device (the way the Regulation Field's
/// animator is tested without a render surface).
/// </summary>
public static class WatchHapticPlanner
{
	/// <summary>
	/// Resolves the continuous haptic for one reading. Silent (intensity 0) while
	/// paused, off-skin, below the confidence floor, in <see cref="WatchHapticMode.StateCues"/>
	/// mode, or at/below baseline (unless an anchor breath is opted in). Above
	/// baseline the intensity scales with <see cref="RegulationReading.Index"/>,
	/// clamped to the user's ceiling. The breath period is constant — rising
	/// arousal raises intensity, it never shortens the breath (the cadence stays
	/// down-regulating, §2.1).
	/// </summary>
	public static WatchHapticPlan Plan(
		RegulationReading reading,
		DetectorState state,
		bool contactOk,
		bool isPaused,
		WatchHapticOptions options)
	{
		double period = options.BreathPeriodSeconds;

		// Hard silence gates.
		if (isPaused || !contactOk
			|| reading.Confidence < options.ConfidenceFloor
			|| options.Mode == WatchHapticMode.StateCues)
		{
			return new WatchHapticPlan(0.0, period);
		}

		// At or below baseline: calm. Silent unless the user opted into a faint anchor.
		if (reading.Index <= 0.0)
		{
			double calm = options.AnchorWhenCalm
				? Math.Clamp(options.AnchorIntensity, 0.0, options.IntensityCeiling)
				: 0.0;
			return new WatchHapticPlan(calm, period);
		}

		// Above baseline: proportional salience, clamped to the ceiling. Index ≥ 1
		// (severe) saturates at the ceiling.
		double intensity = Math.Clamp(reading.Index, 0.0, 1.0) * options.IntensityCeiling;
		return new WatchHapticPlan(intensity, period);
	}

	/// <summary>
	/// The discrete cue (if any) for a detector-state transition. Escalating into
	/// Warning/Alerting taps softly; de-escalating from an episode back toward
	/// baseline releases gently. Returns null in <see cref="WatchHapticMode.PacedBreath"/>
	/// mode (discrete cues suppressed) or for transitions that warrant no cue.
	/// </summary>
	public static WatchHapticCue? CueForTransition(
		DetectorState previous,
		DetectorState current,
		WatchHapticMode mode)
	{
		if (mode == WatchHapticMode.PacedBreath || previous == current)
		{
			return null;
		}

		// Escalation: rank rising into an episode tier.
		if (current == DetectorState.Alerting && Rank(previous) < Rank(DetectorState.Alerting))
		{
			return WatchHapticCue.EscalatedToAlerting;
		}

		if (current == DetectorState.Warning && Rank(previous) < Rank(DetectorState.Warning))
		{
			return WatchHapticCue.EscalatedToWarning;
		}

		// Recovery: leaving an active episode (Warning/Alerting) for a calmer state.
		if (IsEpisode(previous) && !IsEpisode(current))
		{
			return WatchHapticCue.Recovered;
		}

		return null;
	}

	private static bool IsEpisode(DetectorState state) =>
		state is DetectorState.Warning or DetectorState.Alerting;

	// Arousal severity order for escalation tests. Cooldown is a post-episode
	// recovery state, ranked below Warning so a Warning→Cooldown move reads as
	// recovery, not escalation.
	private static int Rank(DetectorState state) => state switch
	{
		DetectorState.Idle => 0,
		DetectorState.Watching => 1,
		DetectorState.Cooldown => 1,
		DetectorState.Warning => 2,
		DetectorState.Alerting => 3,
		_ => 0,
	};
}
