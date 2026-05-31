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
}
