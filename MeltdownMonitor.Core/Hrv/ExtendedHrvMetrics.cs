namespace MeltdownMonitor.Core.Hrv;

/// <summary>
/// Frequency-domain and Poincaré HRV metrics computed from a 2–5 minute NN window.
/// Null when insufficient data is available.
/// </summary>
public record ExtendedHrvMetrics(
	/// <summary>Low-frequency band power (0.04–0.15 Hz), ms².</summary>
	double LfPowerMs2,
	/// <summary>High-frequency band power (0.15–0.40 Hz), ms².</summary>
	double HfPowerMs2,
	/// <summary>LF/HF ratio — sympathovagal balance index.</summary>
	double LfHfRatio,
	/// <summary>Poincaré SD1 — short-term beat-to-beat variability = RMSSD / √2, ms.</summary>
	double SD1,
	/// <summary>Poincaré SD2 — longer-term variability, ms.</summary>
	double SD2,
	/// <summary>SD1/SD2 ratio — parasympathetic index; falling value = autonomic imbalance.</summary>
	double SD1SD2Ratio,
	/// <summary>Standard deviation of NN intervals over the extended window, ms.</summary>
	double Sdnn);
