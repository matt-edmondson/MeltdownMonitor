namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Buckets the readings in a Regulation Field trail window into per-axis histograms: arousal
/// index (the X axis, spanning [-1, 1]) and vagal tone (the Y axis, spanning [0, 1], baseline at
/// 0.5). Pure and deterministic so both heads render identical distributions and it can be
/// unit-tested.
/// </summary>
public static class RegulationFieldHistogram
{
	/// <summary>Default bucket resolution for each axis.</summary>
	public const int DefaultBucketCount = 24;

	// Fixed axis ranges, matching the field's marker mapping (see RegulationReading / the views).
	private const double IndexMin = -1.0;
	private const double IndexMax = 1.0;
	private const double VagalToneMin = 0.0;
	private const double VagalToneMax = 1.0;

	/// <summary>Distribution of arousal index (the X axis) across the trail window.</summary>
	public static RegulationAxisHistogram IndexAxis(IReadOnlyList<RegulationTrailPoint> trail, int bucketCount = DefaultBucketCount)
		=> Build(trail, IndexMin, IndexMax, bucketCount, static p => p.Reading.Index);

	/// <summary>Distribution of vagal tone (the Y axis, baseline at 0.5) across the trail window.</summary>
	public static RegulationAxisHistogram VagalToneAxis(IReadOnlyList<RegulationTrailPoint> trail, int bucketCount = DefaultBucketCount)
		=> Build(trail, VagalToneMin, VagalToneMax, bucketCount, static p => p.Reading.VagalTone);

	/// <summary>
	/// Joint dwell density (X = arousal index, Y = vagal tone) across the trail window — the 2D
	/// distribution behind <see cref="IndexAxis"/> and <see cref="VagalToneAxis"/>. Non-finite
	/// readings are skipped; out-of-range values clamp into the edge cells.
	/// </summary>
	public static RegulationFieldDensity FieldDensity(
		IReadOnlyList<RegulationTrailPoint> trail, int xBuckets = DefaultBucketCount, int yBuckets = DefaultBucketCount)
	{
		ArgumentNullException.ThrowIfNull(trail);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(xBuckets);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(yBuckets);

		var counts = new int[xBuckets * yBuckets];
		double xSpan = IndexMax - IndexMin;
		double ySpan = VagalToneMax - VagalToneMin;
		for (int i = 0; i < trail.Count; i++)
		{
			RegulationReading r = trail[i].Reading;
			if (!double.IsFinite(r.Index) || !double.IsFinite(r.VagalTone))
			{
				continue;
			}

			int bx = Math.Clamp((int)Math.Floor((r.Index - IndexMin) / xSpan * xBuckets), 0, xBuckets - 1);
			int by = Math.Clamp((int)Math.Floor((r.VagalTone - VagalToneMin) / ySpan * yBuckets), 0, yBuckets - 1);
			counts[(by * xBuckets) + bx]++;
		}

		return new RegulationFieldDensity(xBuckets, yBuckets, counts);
	}

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
