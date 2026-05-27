namespace MeltdownMonitor.Core.Beats;

public record HrMeasurement(
	int HeartRateBpm,
	IReadOnlyList<double> RrIntervals);
