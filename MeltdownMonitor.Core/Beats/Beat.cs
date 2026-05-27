namespace MeltdownMonitor.Core.Beats;

public record Beat(
	DateTimeOffset Timestamp,
	double RrMs,
	int HeartRateBpm,
	bool IsArtifact);
