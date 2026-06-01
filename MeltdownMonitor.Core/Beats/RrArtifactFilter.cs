using ktsu.Containers;

namespace MeltdownMonitor.Core.Beats;

public class RrArtifactFilter
{
	private const double MinRrMs = 300.0;
	private const double MaxRrMs = 2000.0;
	private const double MaxDeviationFraction = 0.25;
	private const int MedianWindowSize = 5;

	// After this many consecutive in-bounds beats are rejected by the relative-median rule, the
	// filter treats it as a sustained regime shift (or a resumed stream after a gap) rather than a
	// lone ectopic, and re-seeds the median — otherwise rejected beats never refresh the median, so
	// a sustained step is rejected forever until Reset().
	private const int MaxConsecutiveRejections = 4;

	private RingBuffer<double> _recentClean = new(MedianWindowSize);
	private int _consecutiveRejections;

	/// <summary>
	/// Returns true if the RR interval should be rejected as an artifact.
	/// Clean intervals are added to the moving median window.
	/// </summary>
	public bool IsArtifact(double rrMs)
	{
		// Absolute bounds are a hard physiological limit and never count toward a "regime shift".
		if (rrMs < MinRrMs || rrMs > MaxRrMs)
		{
			return true;
		}

		if (_recentClean.Count >= 2)
		{
			double median = ComputeMedian(_recentClean.ToArray());
			if (Math.Abs(rrMs - median) / median > MaxDeviationFraction)
			{
				_consecutiveRejections++;

				// A *run* of in-bounds rejections is a sustained regime shift (or a resumed
				// stream after a gap), not a lone ectopic: re-seed the median from this beat
				// so the filter can't get stuck rejecting the new level forever.
				if (_consecutiveRejections >= MaxConsecutiveRejections)
				{
					_recentClean = new(MedianWindowSize);
					_recentClean.PushBack(rrMs);
					_consecutiveRejections = 0;
					return false;
				}

				return true;
			}
		}

		_consecutiveRejections = 0;
		_recentClean.PushBack(rrMs);

		return false;
	}

	public void Reset()
	{
		_recentClean = new(MedianWindowSize);
		_consecutiveRejections = 0;
	}

	private static double ComputeMedian(double[] values)
	{
		Array.Sort(values);
		int mid = values.Length / 2;
		return values.Length % 2 == 0
			? (values[mid - 1] + values[mid]) / 2.0
			: values[mid];
	}
}
