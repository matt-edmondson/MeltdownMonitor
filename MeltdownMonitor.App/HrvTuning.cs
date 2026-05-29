namespace MeltdownMonitor.App;

/// <summary>
/// Advanced HRV computation windows. Changing these alters what the metrics mean and
/// their comparability with published references — adjust with care.
/// </summary>
public record HrvTuning
{
	/// <summary>Short NN window (seconds) for RMSSD/pNN50/mean-HR. 60 s is a common standard.</summary>
	public double ShortWindowSeconds { get; init; } = 60.0;

	/// <summary>Extended window (seconds) for frequency-domain and Poincaré metrics. 300 s is clinical standard.</summary>
	public double ExtendedWindowSeconds { get; init; } = 300.0;

	/// <summary>How often (seconds) the extended metrics recompute.</summary>
	public double ExtendedComputeIntervalSeconds { get; init; } = 30.0;
}
