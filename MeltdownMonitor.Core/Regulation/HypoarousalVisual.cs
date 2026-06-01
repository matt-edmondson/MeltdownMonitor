namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Pure presentation mappings for the low-arousal / collapse cues on the Regulation Field,
/// shared by both heads so desktop and mobile stay visually consistent and the decision logic
/// is unit-testable. Inputs are the per-sample <c>Hypoarousal</c> scalar [0, 1]
/// (<see cref="RegulationReading.Hypoarousal"/>) and the rate-of-change of that scalar from a
/// second <see cref="RegulationVelocityTracker"/>. Outputs are unitless [0, 1] factors the
/// renderers scale their own pixel/alpha constants by, plus two arrow-decision predicates.
/// </summary>
public static class HypoarousalVisual
{
	/// <summary>
	/// Scalar at or below which collapse cues stay fully dormant — a deadband so beat-to-beat
	/// noise and genuine cool-but-steady rest never tint the field. Matches the spirit of
	/// <c>HypoarousalThresholds.EnterSignal</c> but is a display floor, not a detection threshold.
	/// </summary>
	public const double Floor = 0.15;

	/// <summary>
	/// [0, 1] intensity for the shutdown-zone fill and the marker's collapse halo: 0 at or below
	/// <see cref="Floor"/>, ramping linearly to 1 as the scalar approaches 1. Non-finite → 0.
	/// </summary>
	public static double Intensity(double hypoScalar)
	{
		if (!double.IsFinite(hypoScalar) || hypoScalar <= Floor)
		{
			return 0.0;
		}

		return Math.Clamp((hypoScalar - Floor) / (1.0 - Floor), 0.0, 1.0);
	}

	/// <summary>
	/// True when the trajectory cue should show a collapse WARNING (an arrow toward the shutdown
	/// zone) instead of the index-derived arrow: the collapse scalar is meaningfully present AND
	/// rising.
	/// </summary>
	public static bool ShowCollapseArrow(double hypoScalar, RegulationDynamics hypoDynamics)
		=> hypoScalar > Floor && hypoDynamics.Trend == RegulationTrend.Escalating;

	/// <summary>
	/// True when the existing index arrow must be suppressed to avoid contradicting the shutdown
	/// zone: the collapse scalar is present AND the index arrow would read as "de-escalating"
	/// (calming). Prevents a slide into collapse from being cued as relaxing.
	/// </summary>
	public static bool SuppressIndexArrow(double hypoScalar, RegulationDynamics indexDynamics)
		=> hypoScalar > Floor && indexDynamics.Trend == RegulationTrend.DeEscalating;
}
