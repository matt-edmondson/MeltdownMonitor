using ktsu.Containers;

namespace MeltdownMonitor.App;

/// <summary>An ImPlot-ready pair of equal-length arrays: x positions and y values.</summary>
internal readonly record struct ChartSeries(float[] Xs, float[] Ys);

/// <summary>
/// A live series for the Status charts: values plus the wall-clock time each value was
/// recorded, kept in lock-step ring buffers. <see cref="Snapshot"/> hands ImPlot a
/// matching (x, y) pair so points plot against real time, not sample index. Timestamps
/// are stored as Unix epoch seconds.
/// </summary>
internal sealed class TimedSeries(int capacity)
{
	private readonly RingBuffer<float> _values = new(capacity);
	private readonly RingBuffer<double> _epochSeconds = new(capacity);

	public int Count => _values.Count;

	public void PushBack(DateTimeOffset timestamp, float value)
	{
		_values.PushBack(value);
		_epochSeconds.PushBack(timestamp.ToUnixTimeMilliseconds() / 1000.0);
	}

	public void Resize(int capacity)
	{
		_values.Resize(capacity);
		_epochSeconds.Resize(capacity);
	}

	public void Resample(int capacity)
	{
		_values.Resample(capacity);
		_epochSeconds.Resample(capacity);
	}

	/// <summary>
	/// Copies the series out as an ImPlot-ready <see cref="ChartSeries"/>: Xs are seconds
	/// relative to <paramref name="nowEpochSeconds"/> (newest near 0, older negative) and
	/// Ys are the values. Same length; both empty when no data.
	/// </summary>
	public ChartSeries Snapshot(double nowEpochSeconds)
	{
		int n = _values.Count;
		var xs = new float[n];
		var ys = new float[n];
		for (int i = 0; i < n; i++)
		{
			// Subtract in double (both operands ~1.7e9 epoch seconds) THEN cast to float.
			// Casting epoch to float before subtracting would lose ~128 s of precision.
			xs[i] = (float)(_epochSeconds.At(i) - nowEpochSeconds);
			ys[i] = _values.At(i);
		}

		return new ChartSeries(xs, ys);
	}
}
