namespace MeltdownMonitor.Core.Detection;

public record AlertPayload(
	DateTimeOffset Timestamp,
	string TriggerReason,
	double RmssdAtTrigger,
	double BaselineAtTrigger);
