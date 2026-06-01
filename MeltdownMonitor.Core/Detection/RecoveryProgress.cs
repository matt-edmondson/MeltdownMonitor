namespace MeltdownMonitor.Core.Detection;

/// <summary>
/// How close the body is to clearing an active dysregulation episode — the data
/// behind the Regulation Field's recovery indicator. Leaving an alert is a two-stage
/// gate: first the metrics (RMSSD, HR) must climb back into the recovery band near
/// baseline, then that band must be *held* for
/// <see cref="DetectionThresholds.RecoveryHoldDuration"/>. <see cref="Overall"/> maps
/// those two stages onto a single 0–1 meter. Only meaningful while an episode is
/// active (Warning or Alerting); otherwise it is <see cref="Inactive"/>.
/// </summary>
/// <param name="MetricProximity">
/// [0, 1]: how close the current RMSSD/HR are to the recovery band. 1 = at or inside
/// the band (metrics recovered), 0 = at or beyond the Warning trigger. Recovery needs
/// *both* markers, so this tracks the worse of the two.
/// </param>
/// <param name="HoldProgress">
/// [0, 1]: fraction of <see cref="DetectionThresholds.RecoveryHoldDuration"/> the
/// in-band recovery streak has been sustained. 0 until the detector is actively holding
/// a streak (only accrues in Alerting); resets to 0 if the streak breaks.
/// </param>
/// <param name="IsActive">
/// True only during an active episode (Warning or Alerting); false otherwise, when
/// there is nothing to recover from.
/// </param>
public readonly record struct RecoveryProgress(
	double MetricProximity,
	double HoldProgress,
	bool IsActive)
{
	/// <summary>No active episode — nothing to recover from.</summary>
	public static RecoveryProgress Inactive { get; } = new(0.0, 0.0, false);

	/// <summary>
	/// A single 0–1 "distance to recovery" for the indicator. The first half (0→0.5)
	/// tracks the metrics closing on the recovery band; the second half (0.5→1) tracks
	/// the hold timer once in the band. 0 when no episode is active.
	/// </summary>
	public double Overall => !IsActive
		? 0.0
		: HoldProgress > 0.0
			? 0.5 + (0.5 * Math.Clamp(HoldProgress, 0.0, 1.0))
			: 0.5 * Math.Clamp(MetricProximity, 0.0, 1.0);
}
