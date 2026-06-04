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

	// Minimum index axis range — always covers [-1, 1] but IndexAxis expands it to include extremes.
	// IndexMin/IndexMax are also the fixed x-range for FieldDensity (the 2D heatmap stays
	// within the lemniscate bounds so cells don't render outside the visible field).
	private const double IndexMin = -1.0;
	private const double IndexMax = 1.0;
	private const double VagalToneMin = 0.0;
	private const double VagalToneMax = 1.0;

	/// <summary>
	/// Distribution of arousal index (the X axis) across the trail window. The axis range
	/// expands dynamically: it always covers at least [-1, 1] but extends further when any
	/// trail reading falls outside that band, so severely dysregulated samples are spread
	/// across their own buckets rather than lost or piled into the edge.
	/// </summary>
	public static RegulationAxisHistogram IndexAxis(IReadOnlyList<RegulationTrailPoint> trail, int bucketCount = DefaultBucketCount)
	{
		ArgumentNullException.ThrowIfNull(trail);
		double min = IndexMin;
		double max = IndexMax;
		for (int i = 0; i < trail.Count; i++)
		{
			double v = trail[i].Reading.Index;
			if (!double.IsFinite(v)) { continue; }
			if (v < min) { min = v; }
			if (v > max) { max = v; }
		}

		return Build(trail, min, max, bucketCount, static p => p.Reading.Index);
	}

	/// <summary>Distribution of vagal tone (the Y axis, baseline at 0.5) across the trail window.</summary>
	public static RegulationAxisHistogram VagalToneAxis(IReadOnlyList<RegulationTrailPoint> trail, int bucketCount = DefaultBucketCount)
		=> Build(trail, VagalToneMin, VagalToneMax, bucketCount, static p => p.Reading.VagalTone);

	/// <summary>
	/// Joint dwell density (X = arousal index, Y = vagal tone) across the trail window — the 2D
	/// distribution behind <see cref="IndexAxis"/> and <see cref="VagalToneAxis"/>. Non-finite
	/// readings and values outside the axis ranges are skipped; only readings within the visible
	/// field boundaries contribute, so extreme (off-chart) values do not inflate the edge cells.
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
