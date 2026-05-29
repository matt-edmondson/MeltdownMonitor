namespace MeltdownMonitor.Core.Baseline;

/// <summary>
/// User-tunable parameters for the HRV baseline. Responsiveness is expressed in
/// human-friendly minutes of "memory"; the pipeline converts those windows to the
/// tracker's per-sample EWMA alpha using the active sample cadence.
/// </summary>
public record BaselineTuning
{
	/// <summary>Days of history read for the long-term anchor median.</summary>
	public int AnchorWindowDays { get; init; } = 7;

	/// <summary>Recent window (minutes) whose median seeds the live baseline at startup.</summary>
	public double WarmStartWindowMinutes { get; init; } = 60.0;

	/// <summary>Minimum recent clean samples required to warm-start (skip the cold warm-up).</summary>
	public int MinWarmStartSamples { get; init; } = 12;

	/// <summary>Guardrail band (fraction) the live baseline may drift from the anchor.</summary>
	public double MaxAnchorDrift { get; init; } = 0.40;

	/// <summary>Effective memory (minutes) of the live RMSSD/HR baseline.</summary>
	public double RmssdHrWindowMinutes { get; init; } = 15.0;

	/// <summary>Effective memory (minutes) of the live LF/HF baseline.</summary>
	public double LfHfWindowMinutes { get; init; } = 17.0;

	/// <summary>Cold-start warm-up (minutes) before the detector arms.</summary>
	public double WarmUpMinutes { get; init; } = 10.0;
}
