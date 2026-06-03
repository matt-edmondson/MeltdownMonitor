namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// A 2D dwell-density grid over the Regulation Field: how many samples in the comet-trail window
/// fell in each cell of the (arousal index X in [-1, 1], vagal tone Y in [0, 1]) plane. It is the
/// joint distribution behind the two marginal <see cref="RegulationAxisHistogram"/>s, and the
/// field renders it as a faint heatmap underlay showing where regulation habitually settles.
/// Pure/deterministic so both heads render identical clouds and it can be unit-tested.
/// </summary>
public readonly record struct RegulationFieldDensity
{
	private readonly int[] _counts;

	/// <param name="xBuckets">Columns across the arousal-index axis (X). Must be &gt; 0.</param>
	/// <param name="yBuckets">Rows across the vagal-tone axis (Y). Must be &gt; 0.</param>
	/// <param name="counts">
	/// Per-cell sample counts in row-major order: <c>counts[(y * xBuckets) + x]</c>, with x = 0 the
	/// leftmost (cool) column and y = 0 the lowest vagal-tone (FRAGILE) row. Length must equal
	/// <paramref name="xBuckets"/> × <paramref name="yBuckets"/>. Never null.
	/// </param>
	public RegulationFieldDensity(int xBuckets, int yBuckets, int[] counts)
	{
		ArgumentNullException.ThrowIfNull(counts);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(xBuckets);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(yBuckets);
		if (counts.Length != xBuckets * yBuckets)
		{
			throw new ArgumentException(
				$"counts length {counts.Length} must equal xBuckets*yBuckets = {xBuckets * yBuckets}.", nameof(counts));
		}

		XBuckets = xBuckets;
		YBuckets = yBuckets;
		_counts = counts;

		int peak = 0;
		int total = 0;
		int peakIndex = -1;
		for (int i = 0; i < counts.Length; i++)
		{
			int c = counts[i];
			if (c > peak)
			{
				peak = c;
				peakIndex = i;
			}

			total += c;
		}

		PeakCount = peak;
		TotalCount = total;
		PeakX = peakIndex >= 0 ? peakIndex % xBuckets : -1;
		PeakY = peakIndex >= 0 ? peakIndex / xBuckets : -1;
	}

	/// <summary>Number of columns across the X (arousal index) axis.</summary>
	public int XBuckets { get; }

	/// <summary>Number of rows across the Y (vagal tone) axis.</summary>
	public int YBuckets { get; }

	/// <summary>Per-cell sample counts, row-major: <c>Counts[(y * XBuckets) + x]</c>.</summary>
	public IReadOnlyList<int> Counts => _counts ?? [];

	/// <summary>Largest single-cell count; 0 when empty. Normalise heat intensity against this.</summary>
	public int PeakCount { get; }

	/// <summary>Column (X) of the busiest cell — where dwell peaks; -1 when empty. Ties resolve to
	/// the first such cell in row-major order.</summary>
	public int PeakX { get; }

	/// <summary>Row (Y) of the busiest cell — where dwell peaks; -1 when empty. Ties resolve to
	/// the first such cell in row-major order.</summary>
	public int PeakY { get; }

	/// <summary>Total samples counted across every cell.</summary>
	public int TotalCount { get; }

	/// <summary>Count in the cell at column <paramref name="x"/>, row <paramref name="y"/>.</summary>
	public int Count(int x, int y)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(x);
		ArgumentOutOfRangeException.ThrowIfNegative(y);
		ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, XBuckets);
		ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, YBuckets);
		return _counts[(y * XBuckets) + x];
	}
}
