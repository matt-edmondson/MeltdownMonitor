namespace MeltdownMonitor.Core.Detection;

public enum DetectorState
{
	Idle,
	Watching,
	Warning,
	Alerting,
	Cooldown,
}
