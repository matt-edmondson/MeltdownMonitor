using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.Core.Hrv;

public record HrvSample(
	DateTimeOffset Timestamp,
	double Rmssd,
	double Pnn50,
	double MeanHr,
	double BaselineRmssd,
	double BaselineHr,
	DetectorState State);
