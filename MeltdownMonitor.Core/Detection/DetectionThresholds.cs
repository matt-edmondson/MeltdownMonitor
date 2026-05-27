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
}
