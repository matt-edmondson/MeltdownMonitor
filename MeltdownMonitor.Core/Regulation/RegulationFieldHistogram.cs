namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Buckets the readings in a Regulation Field trail window into per-axis histograms: arousal
/// index (the X axis) and vagal tone (the Y axis, spanning [0, 1], baseline at 0.5). The index
/// axis expands dynamically to cover extreme values beyond ±1. Pure and deterministic so both
/// heads render identical distributions and it can be unit-tested.
/// </summary>
public static class RegulationFieldHistogram
{
	/// <summary>Default bucket resolution for each axis.</summary>
	public const int DefaultBucketCount = 24;

	// Fixed axis ranges, matching the field's marker mapping (see RegulationReading / the views).
	// The marker clamps to ±1 and the dwell heatmap spans the same band, so the index histogram
	// stays fixed at [-1, 1] too — all buckets render within the lemniscate bounds. Readings
	// beyond ±1 (severe dysregulation, where the marker is already pegged at the edge) are skipped
	// rather than stretching the axis off-screen.
	private const double IndexMin = -1.0;
	private const double IndexMax = 1.0;
	private const double VagalToneMin = 0.0;
	private const double VagalToneMax = 1.0;

	/// <summary>Distribution of arousal index (the X axis, spanning [-1, 1]) across the trail window.</summary>
	public static RegulationAxisHistogram IndexAxis(IReadOnlyList<RegulationTrailPoint> trail, int bucketCount = DefaultBucketCount)
		=> Build(trail, IndexMin, IndexMax, bucketCount, static p => p.Reading.Index);

	/// <summary>Distribution of vagal tone (the Y axis, baseline at 0.5) across the trail window.</summary>
	public static RegulationAxisHistogram VagalToneAxis(IReadOnlyList<RegulationTrailPoint> trail, int bucketCount = DefaultBucketCount)
		=> Build(trail, VagalToneMin, VagalToneMax, bucketCount, static p => p.Reading.VagalTone);

	/// <summary>
	/// Joint dwell density (X = arousal index, Y = vagal tone) across the trail window — the 2D
	/// distribution behind <see cref="IndexAxis"/> and <see cref="VagalToneAxis"/>. Non-finite
	/// readings and values outside the axis ranges are skipped; only readings within the visible
	/// field boundaries contribute, so extreme (off-chart) values do not inflate the edge cells.
	/// <paramref name="startIndex"/> counts only the trailing slice <c>trail[startIndex..]</c>, so
	/// the heatmap can span a shorter dwell window than the full buffer without allocating a copy.
	/// </summary>
	public static RegulationFieldDensity FieldDensity(
		IReadOnlyList<RegulationTrailPoint> trail, int xBuckets = DefaultBucketCount, int yBuckets = DefaultBucketCount, int startIndex = 0)
	{
		ArgumentNullException.ThrowIfNull(trail);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(xBuckets);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(yBuckets);

		var counts = new int[xBuckets * yBuckets];
		double xSpan = IndexMax - IndexMin;
		double ySpan = VagalToneMax - VagalToneMin;
		for (int i = Math.Max(0, startIndex); i < trail.Count; i++)
		{
			RegulationReading r = trail[i].Reading;
			if (!double.IsFinite(r.Index) || !double.IsFinite(r.VagalTone))
			{
				continue;
			}

			if (r.Index < IndexMin || r.Index > IndexMax || r.VagalTone < VagalToneMin || r.VagalTone > VagalToneMax)
			{
				continue;
			}

			int bx = Math.Min((int)Math.Floor((r.Index - IndexMin) / xSpan * xBuckets), xBuckets - 1);
			int by = Math.Min((int)Math.Floor((r.VagalTone - VagalToneMin) / ySpan * yBuckets), yBuckets - 1);
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
			if (!double.IsFinite(value) || value < min || value > max)
			{
				continue;
			}

			// Exact-max maps to bucketCount; clamp it into the last bucket.
			int bucket = Math.Min((int)Math.Floor((value - min) / span * bucketCount), bucketCount - 1);
			counts[bucket]++;
		}

		return new RegulationAxisHistogram(min, max, counts);
	}
}
