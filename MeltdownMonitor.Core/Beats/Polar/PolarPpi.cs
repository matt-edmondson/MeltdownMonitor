namespace MeltdownMonitor.Core.Beats.Polar;

/// <summary>
/// Converts Polar PMD peak-to-peak interval samples into <see cref="Beat"/>s. Unlike the standard
/// Heart Rate service's bare RR list, PPI carries per-beat quality the artifact filter can lean on:
/// a <see cref="PmdPpiSample.Blocker"/> flag (the device's own "this interval is unreliable" signal),
/// a millisecond <see cref="PmdPpiSample.ErrorEstimateMs"/>, and skin-contact flags. Kept in Core so
/// every head converts identically and the quality rules are unit-tested.
/// </summary>
public static class PolarPpi
{
	/// <summary>
	/// Default ceiling for a PPI's error estimate before it's treated as an artifact. Polar reports
	/// single-digit errors for clean optical intervals; tens of milliseconds means a missed/spurious
	/// beat.
	/// </summary>
	public const int DefaultMaxErrorEstimateMs = 30;

	/// <summary>
	/// Whether a PPI sample is unreliable on its own quality flags alone (before any timing-based
	/// outlier check): the device flagged it, the optical sensor lost skin contact, or the error
	/// estimate is too large.
	/// </summary>
	public static bool IsLowQuality(PmdPpiSample sample, int maxErrorEstimateMs = DefaultMaxErrorEstimateMs) =>
		sample.Blocker
		|| (sample.SkinContactSupported && !sample.SkinContact)
		|| sample.ErrorEstimateMs > maxErrorEstimateMs;

	/// <summary>
	/// Builds a beat from a PPI sample. It's an artifact if PPI's own quality flags say so
	/// (<see cref="IsLowQuality"/>) <i>or</i> if the caller's timing-based filter rejects the interval
	/// (<paramref name="timingArtifact"/>) — the two are complementary: quality flags catch
	/// optically-bad beats the timing filter would pass, and vice versa.
	/// </summary>
	public static Beat ToBeat(
		PmdPpiSample sample,
		DateTimeOffset timestamp,
		bool timingArtifact,
		int maxErrorEstimateMs = DefaultMaxErrorEstimateMs) =>
		new(timestamp, sample.PpiMs, sample.HeartRate, IsLowQuality(sample, maxErrorEstimateMs) || timingArtifact);
}
