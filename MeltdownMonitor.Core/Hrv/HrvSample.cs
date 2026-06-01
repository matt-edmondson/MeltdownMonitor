using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.Core.Hrv;

public record HrvSample(
	DateTimeOffset Timestamp,
	double Rmssd,
	double Pnn50,
	double MeanHr,
	double BaselineRmssd,
	double BaselineHr,
	DetectorState State)
{
	/// <summary>
	/// Frequency-domain and Poincaré metrics from the 5-minute window.
	/// Null during warm-up or when insufficient data is available.
	/// </summary>
	public ExtendedHrvMetrics? Extended { get; init; }

	/// <summary>
	/// Baseline LF/HF ratio (EWMA). Zero until the baseline tracker has
	/// observed enough extended metrics samples to be meaningful.
	/// </summary>
	public double BaselineLfHfRatio { get; init; }

	/// <summary>Sensor skin/electrode contact at this sample's moment. Default
	/// <see cref="SensorContactStatus.NotSupported"/> (sensor not reporting contact).</summary>
	public SensorContactStatus SensorContact { get; init; } = SensorContactStatus.NotSupported;
}
