namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Memoizes the Regulation Field's dwell density and the two per-axis histograms so the heads
/// recompute them only when the trail or the bucket/window settings actually change — not on every
/// render frame. At long heatmap windows (up to ~30 days of readings) a per-frame rescan of the
/// whole trail is the dominant cost; gating it on a monotonic trail <c>version</c> collapses that to
/// once per new sample. <see cref="Update"/> is a cheap no-op when nothing relevant changed.
/// </summary>
/// <remarks>
/// Not thread-safe: a caller that mutates its trail off the render thread must serialize
/// <see cref="Update"/> against that mutation (the desktop head calls it under the same lock that
/// guards the trail). The recompute reads the supplied list synchronously and keeps no reference to
/// it, so the caller may reuse or mutate the list freely once <see cref="Update"/> returns.
/// </remarks>
public sealed class RegulationFieldAggregateCache
{
	private bool _valid;
	private long _version = long.MinValue;
	private int _xBuckets;
	private int _yBuckets;
	private int _window = -1;

	/// <summary>The cached dwell density over the heatmap window. Empty until the first
	/// <see cref="Update"/>.</summary>
	public RegulationFieldDensity Density { get; private set; }

	/// <summary>The cached arousal-index (X) axis distribution over the full trail. Empty until the
	/// first <see cref="Update"/>.</summary>
	public RegulationAxisHistogram IndexAxis { get; private set; }

	/// <summary>The cached vagal-tone (Y) axis distribution over the full trail. Empty until the
	/// first <see cref="Update"/>.</summary>
	public RegulationAxisHistogram VagalToneAxis { get; private set; }

	/// <summary>
	/// Recompute the cached aggregates if <paramref name="version"/> or any bucket/window parameter
	/// differs from the last call; otherwise return immediately, leaving the cached values in place.
	/// The two histograms span the whole <paramref name="trail"/>; the density spans only its last
	/// <paramref name="heatmapWindow"/> points (pass <see cref="int.MaxValue"/> to span the whole
	/// buffer), matching each head's existing windowing.
	/// </summary>
	/// <param name="trail">The trail buffer to bucket. Read synchronously; not retained.</param>
	/// <param name="version">A value the caller bumps whenever the trail's contents change. The
	/// cache recomputes whenever this differs from the previous call.</param>
	/// <param name="xBuckets">Columns across the arousal-index (X) axis; also the index-histogram
	/// bucket count. Must be &gt; 0.</param>
	/// <param name="yBuckets">Rows across the vagal-tone (Y) axis; also the vagal-histogram bucket
	/// count. Must be &gt; 0.</param>
	/// <param name="heatmapWindow">How many of the most recent points the density spans. Values at or
	/// above <paramref name="trail"/>'s count span the whole buffer.</param>
	public void Update(IReadOnlyList<RegulationTrailPoint> trail, long version, int xBuckets, int yBuckets, int heatmapWindow)
	{
		ArgumentNullException.ThrowIfNull(trail);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(xBuckets);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(yBuckets);

		if (_valid && version == _version && xBuckets == _xBuckets && yBuckets == _yBuckets && heatmapWindow == _window)
		{
			return;
		}

		IndexAxis = RegulationFieldHistogram.IndexAxis(trail, xBuckets);
		VagalToneAxis = RegulationFieldHistogram.VagalToneAxis(trail, yBuckets);

		// Density spans only the trailing heatmap window; histograms span the full buffer (which can
		// be longer when the comet trail is configured longer than the heatmap).
		int start = heatmapWindow > 0 && trail.Count > heatmapWindow ? trail.Count - heatmapWindow : 0;
		Density = RegulationFieldHistogram.FieldDensity(trail, xBuckets, yBuckets, start);

		_valid = true;
		_version = version;
		_xBuckets = xBuckets;
		_yBuckets = yBuckets;
		_window = heatmapWindow;
	}
}
