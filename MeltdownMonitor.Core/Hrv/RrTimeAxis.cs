namespace MeltdownMonitor.Core.Hrv;

/// <summary>
/// Reconstructs a time axis for a run of RR intervals. Beats arrive from BLE in
/// batches that share one arrival timestamp, so wall-clock time can't space them
/// apart — but each RR interval <em>is</em> the gap (ms) to the previous beat, so a
/// running sum is an exact relative time axis. The newest beat sits at 0; older beats
/// are negative seconds before it.
/// </summary>
public static class RrTimeAxis
{
	/// <summary>
	/// Returns one x position (seconds) per RR interval, newest at 0 and older beats
	/// negative. Consecutive spacing equals the corresponding RR interval in seconds.
	/// Returns an empty array for empty input.
	/// The first interval is a gap from an unobserved earlier beat, so it does not contribute to the axis extent.
	/// </summary>
	public static double[] CumulativeSeconds(IReadOnlyList<double> rrMs)
	{
		ArgumentNullException.ThrowIfNull(rrMs);

		int n = rrMs.Count;
		if (n == 0)
		{
			return [];
		}

		var x = new double[n];
		x[0] = 0.0;
		for (int i = 1; i < n; i++)
		{
			x[i] = x[i - 1] + (rrMs[i] / 1000.0);
		}

		double newest = x[n - 1];
		for (int i = 0; i < n; i++)
		{
			x[i] -= newest;
		}

		return x;
	}
}
