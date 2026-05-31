namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// The rate of change of a <see cref="RegulationReading.Index"/> over time, plus the
/// direction (with deadband) and a normalised magnitude for driving visuals. Produced
/// by <see cref="RegulationVelocityTracker"/>; <see cref="RegulationReading"/> itself
/// stays a pure single-sample value.
/// </summary>
/// <param name="Velocity">Signed d(Index)/dt in index-units per second (+ = escalating).</param>
/// <param name="Trend">Tri-state direction derived from <paramref name="Velocity"/> via a deadband.</param>
/// <param name="NormalizedSpeed">|Velocity| mapped to [0, 1] against a reference rate, for visual magnitude.</param>
public readonly record struct RegulationDynamics(
	double Velocity,
	RegulationTrend Trend,
	double NormalizedSpeed)
{
	/// <summary>A steady reading with no motion — the value before any sample, or while calibrating.</summary>
	public static RegulationDynamics Steady { get; } = new(0.0, RegulationTrend.Steady, 0.0);
}
