using ktsu.Containers;

namespace MeltdownMonitor.Core.Beats;

public class RrArtifactFilter
{
	private const double MinRrMs = 300.0;
	private const double MaxRrMs = 2000.0;
	private const double MaxDeviationFraction = 0.25;
	private const int MedianWindowSize = 5;

	private RingBuffer<double> _recentClean = new(MedianWindowSize);

	/// <summary>
	/// Returns true if the RR interval should be rejected as an artifact.
	/// Clean intervals are added to the moving median window.
	/// </summary>
	public bool IsArtifact(double rrMs)
	{
		if (rrMs < MinRrMs || rrMs > MaxRrMs)
		{
			return true;
		}

		if (_recentClean.Count >= 2)
		{
			double median = ComputeMedian(_recentClean.ToArray());
			if (Math.Abs(rrMs - median) / median > MaxDeviationFraction)
			{
				return true;
			}
		}

		_recentClean.PushBack(rrMs);

		return false;
	}

	public void Reset() => _recentClean = new(MedianWindowSize);

	private static double ComputeMedian(double[] values)
	{
		Array.Sort(values);
		int mid = values.Length / 2;
		return values.Length % 2 == 0
			? (values[mid - 1] + values[mid]) / 2.0
			: values[mid];
	}
}
