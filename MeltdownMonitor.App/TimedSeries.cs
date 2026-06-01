using ktsu.Containers;

namespace MeltdownMonitor.App;

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
	/// Copies the series out as ImPlot-ready arrays: <paramref name="xs"/> are seconds
	/// relative to <paramref name="nowEpochSeconds"/> (newest near 0, older negative)
	/// and <paramref name="ys"/> are the values. Same length; both empty when no data.
	/// </summary>
	public void Snapshot(double nowEpochSeconds, out float[] xs, out float[] ys)
	{
		int n = _values.Count;
		xs = new float[n];
		ys = new float[n];
		for (int i = 0; i < n; i++)
		{
			xs[i] = (float)(_epochSeconds.At(i) - nowEpochSeconds);
			ys[i] = _values.At(i);
		}
	}
}
