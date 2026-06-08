using MeltdownMonitor.Core.Motion;

namespace MeltdownMonitor.Core.Detection;

public record DetectionThresholds
{
	/// <summary>RMSSD must drop this fraction below baseline to trigger Warning.</summary>
	public double RmssdWarningDropFraction { get; init; } = 0.30;

	/// <summary>Mean HR must rise this fraction above baseline to trigger Warning.</summary>
	public double HrWarningRiseFraction { get; init; } = 0.15;

	/// <summary>Warning conditions must hold this long before entering Warning state.</summary>
	public TimeSpan WarningHoldDuration { get; init; } = TimeSpan.FromSeconds(30);

	/// <summary>Warning conditions must hold this long after entering Warning before Alerting.</summary>
	public TimeSpan AlertingEscalationDuration { get; init; } = TimeSpan.FromSeconds(60);

	/// <summary>RMSSD drop fraction that immediately triggers Alerting from any state.</summary>
	public double RmssdAlertingDropFraction { get; init; } = 0.50;

	/// <summary>Minimum time between alerts.</summary>
	public TimeSpan CooldownDuration { get; init; } = TimeSpan.FromMinutes(10);

	// ── Physiological recovery (exiting Alerting) ──
	// Distinguishes a genuine vagal rebound from a transient return toward baseline.
	// Merely clearing the Warning trigger is not recovery; RMSSD must climb back
	// *near* baseline and HR must settle, and both must hold for RecoveryHoldDuration.

	/// <summary>
	/// During an alert, RMSSD must have climbed back to within this fraction of
	/// baseline (i.e. its drop is no deeper than this) to count toward recovery.
	/// Tighter than the Warning trigger so a partial rebound doesn't end the alert.
	/// </summary>
	public double RmssdRecoveryDropFraction { get; init; } = 0.10;

	/// <summary>
	/// During an alert, HR must have settled to within this fraction above baseline
	/// to count toward recovery.
	/// </summary>
	public double HrRecoveryRiseFraction { get; init; } = 0.05;

	/// <summary>
	/// Recovery conditions must hold continuously for this long before the detector
	/// accepts that the body has physiologically recovered and steps down to Cooldown.
	/// </summary>
	public TimeSpan RecoveryHoldDuration { get; init; } = TimeSpan.FromSeconds(60);

	// ── LF/HF corroboration (on by default — the most accurate setting) ──

	/// <summary>
	/// When true, Warning entry also requires LF/HF to be elevated above its baseline —
	/// the more accurate (more specific) signal, so it is the default. It is only applied
	/// once a personal LF/HF baseline and ≥2 minutes of clean extended metrics exist; until
	/// then the detector falls back to the RMSSD+HR condition, so it never suppresses early
	/// warnings during warm-up.
	/// </summary>
	public bool UseLfHfCorroboration { get; init; } = true;

	/// <summary>
	/// LF/HF must rise at least this fraction above its baseline for the corroboration
	/// condition to be satisfied. Default 50% (e.g. baseline 1.5 → current ≥ 2.25).
	/// </summary>
	public double LfHfWarningRiseFraction { get; init; } = 0.50;

	/// <summary>
	/// Whether LF/HF corroboration can veto a Warning or only strengthen it. Default
	/// <see cref="LfHfCorroborationMode.Additive"/> per the 2026-06-01 clinical audit: the laggy
	/// 5-minute LF/HF window must not suppress a core-satisfied early Warning (a missed early
	/// warning is the more harmful error for an awareness tool). <see cref="LfHfCorroborationMode.Veto"/>
	/// is the more specific but more suppressive prior behaviour. Only consulted when
	/// <see cref="UseLfHfCorroboration"/> is true.
	/// </summary>
	public LfHfCorroborationMode LfHfCorroborationMode { get; init; } = LfHfCorroborationMode.Additive;

	/// <summary>
	/// Consecutive in-contact samples with an immediate-severe RMSSD drop required before the
	/// immediate alert fires. Default 2 per the 2026-06-01 clinical audit: the immediate ≥50%-drop
	/// path is the most false-positive-prone (a transient RSA regularisation — breath-hold, Valsalva —
	/// can momentarily collapse RMSSD), so two consecutive in-contact samples (~10 s) reject those at
	/// the cost of ~one sample of latency. 1 fires on the first qualifying sample (prior behaviour).
	/// </summary>
	public int SevereDropConfirmationCount { get; init; } = 2;

	// ── Movement gating (motion corroboration; only active when a motion source feeds levels) ──

	/// <summary>
	/// When true, a movement level at/above <see cref="MovementGateLevel"/> defers Warning/Alerting
	/// escalation the same way an off-body sensor does — exertion drops HRV and raises HR (the
	/// dysregulation signature), so without this an exercise bout reads as a meltdown. Only ever
	/// consulted when a motion source supplies a level other than <see cref="MovementLevel.Unknown"/>,
	/// so a build with no accelerometer behaves exactly as before. Default on.
	/// </summary>
	public bool UseMovementGating { get; init; } = true;

	/// <summary>
	/// The movement level at/above which detection and the baseline are gated. Default
	/// <see cref="MovementLevel.Moderate"/> (walking): light fidgeting must NOT suppress alerts
	/// (agitation is part of the signal we want), but walking-and-up confounds HRV enough to gate.
	/// </summary>
	public MovementLevel MovementGateLevel { get; init; } = MovementLevel.Moderate;

	/// <summary>
	/// When true, individual beats that arrive while movement is at/above <see cref="MovementGateLevel"/>
	/// are marked as artifacts and kept out of the HRV computation entirely — motion smears the RR with
	/// noise (the same jitter that inflates SDNN/RMSSD), so excluding those beats yields a cleaner read
	/// than merely deferring escalation. Complementary to <see cref="UseMovementGating"/>: that holds the
	/// detector during exertion; this keeps motion-corrupted intervals out of the metrics themselves. Only
	/// active when a motion source supplies a level other than <see cref="MovementLevel.Unknown"/>, so a
	/// build with no accelerometer is unaffected. Default off — opt-in, since it discards data.
	/// </summary>
	public bool RejectMotionArtifacts { get; init; }

	// ── Apple Watch corroboration (only active when a watch metric source feeds a verdict) ──

	/// <summary>
	/// When true, a <see cref="WatchCorroboration.Conflicted"/> verdict — the Apple Watch and the
	/// chest strap disagree on heart rate — defers Warning/Alerting escalation the same way an
	/// off-body sensor does: two sensors on one body should agree, so a sustained disagreement marks
	/// the strap signal as suspect (motion artifact, poor contact) and the more conservative choice is
	/// to hold rather than alarm on an unreliable reading. Only ever consulted when a watch metric
	/// source supplies a verdict other than <see cref="WatchCorroboration.Unknown"/>, so a build with
	/// no paired watch behaves exactly as before. Default on.
	/// </summary>
	public bool UseWatchCorroboration { get; init; } = true;

	/// <summary>
	/// When true, a <see cref="WatchCorroboration.Conflicted"/> verdict also <b>freezes the baseline</b>
	/// for that sample — the same treatment the movement gate gives exertion — on the reasoning that a
	/// strap reading the wrist contradicts is suspect RR that would otherwise quietly re-normalise the
	/// EWMA baseline. Off by default: the verdict already gates escalation (<see cref="UseWatchCorroboration"/>),
	/// the baseline's minutes-long memory absorbs a few suspect samples, and freezing is the stricter,
	/// opt-in choice. Only consulted when <see cref="UseWatchCorroboration"/> is on and a verdict other
	/// than <see cref="WatchCorroboration.Unknown"/> is present, so a no-watch build is unaffected.
	/// </summary>
	public bool FreezeBaselineOnWatchConflict { get; init; }

	/// <summary>
	/// Tuning for the <see cref="HypoarousalDetector"/> — the low-arousal/shutdown edge (audit A(b)).
	/// Nested here so the whole detection configuration travels and serialises as one object and the
	/// pipelines can wire the detector to live edits via <c>Thresholds.Hypoarousal</c>. The
	/// dysregulation detector ignores it.
	/// </summary>
	public HypoarousalThresholds Hypoarousal { get; init; } = new();
}
