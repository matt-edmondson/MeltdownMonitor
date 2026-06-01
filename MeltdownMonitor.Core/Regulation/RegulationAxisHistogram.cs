namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// The distribution of Regulation Field samples across evenly-sized value buckets over a fixed
/// [<see cref="Min"/>, <see cref="Max"/>] range. The field draws one of these along each axis —
/// arousal index (X) and variability quality (Y) — to show how the samples currently in the
/// comet-trail window are spread across the field.
/// </summary>
public readonly record struct RegulationAxisHistogram
{
	private readonly int[] _counts;

	/// <param name="min">Lower edge of the first bucket.</param>
	/// <param name="max">Upper edge of the last bucket.</param>
	/// <param name="counts">Per-bucket sample counts, lowest value first. Never null.</param>
	public RegulationAxisHistogram(double min, double max, int[] counts)
	{
		ArgumentNullException.ThrowIfNull(counts);
		Min = min;
		Max = max;
		_counts = counts;

		int peak = 0;
		int total = 0;
		foreach (int c in counts)
		{
			if (c > peak)
			{
				peak = c;
			}

			total += c;
		}

		PeakCount = peak;
		TotalCount = total;
	}

	/// <summary>Lower edge of the first bucket.</summary>
	public double Min { get; }

	/// <summary>Upper edge of the last bucket.</summary>
	public double Max { get; }

	/// <summary>Per-bucket sample counts, lowest value first.</summary>
	public IReadOnlyList<int> Counts => _counts ?? [];

	/// <summary>Number of buckets.</summary>
	public int BucketCount => _counts?.Length ?? 0;

	/// <summary>Largest single-bucket count; 0 when empty. Normalise bar lengths against this.</summary>
	public int PeakCount { get; }

	/// <summary>Total samples counted across every bucket.</summary>
	public int TotalCount { get; }
}
