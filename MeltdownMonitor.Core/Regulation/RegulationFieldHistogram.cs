namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Buckets the readings in a Regulation Field trail window into per-axis histograms: arousal
/// index (the X axis, spanning [-1, 1]) and variability quality (the Y axis, spanning [0, 1]).
/// Pure and deterministic so both heads render identical distributions and it can be unit-tested.
/// </summary>
public static class RegulationFieldHistogram
{
	/// <summary>Default bucket resolution for each axis.</summary>
	public const int DefaultBucketCount = 24;

	// Fixed axis ranges, matching the field's marker mapping (see RegulationReading / the views).
	private const double IndexMin = -1.0;
	private const double IndexMax = 1.0;
	private const double QualityMin = 0.0;
	private const double QualityMax = 1.0;

	/// <summary>Distribution of arousal index (the X axis) across the trail window.</summary>
	public static RegulationAxisHistogram IndexAxis(IReadOnlyList<RegulationTrailPoint> trail, int bucketCount = DefaultBucketCount)
		=> Build(trail, IndexMin, IndexMax, bucketCount, static p => p.Reading.Index);

	/// <summary>Distribution of variability quality (the Y axis) across the trail window.</summary>
	public static RegulationAxisHistogram QualityAxis(IReadOnlyList<RegulationTrailPoint> trail, int bucketCount = DefaultBucketCount)
		=> Build(trail, QualityMin, QualityMax, bucketCount, static p => p.Reading.VariabilityQuality);

	private static RegulationAxisHistogram Build(
		IReadOnlyList<RegulationTrailPoint> trail,
		double min,
		double max,
		int bucketCount,
		Func<RegulationTrailPoint, double> selector)
	{
		ArgumentNullException.ThrowIfNull(trail);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketCount);

		var counts = new int[bucketCount];
		double span = max - min;
		for (int i = 0; i < trail.Count; i++)
		{
			double value = selector(trail[i]);
			if (!double.IsFinite(value))
			{
				continue;
			}

			// Map the value onto a bucket index; out-of-range and exact-max values clamp into
			// [0, bucketCount-1] so a reading sitting on the upper edge still counts.
			int bucket = (int)Math.Floor((value - min) / span * bucketCount);
			bucket = Math.Clamp(bucket, 0, bucketCount - 1);
			counts[bucket]++;
		}

		return new RegulationAxisHistogram(min, max, counts);
	}
}
