namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Direction of change of the arousal index, with a deadband around zero so small
/// noise reads as <see cref="Steady"/> rather than flickering between the poles.
/// </summary>
public enum RegulationTrend
{
	DeEscalating,
	Steady,
	Escalating,
}
