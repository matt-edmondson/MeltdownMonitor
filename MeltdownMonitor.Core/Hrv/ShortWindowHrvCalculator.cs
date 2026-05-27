using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.Core.Hrv;

public class ShortWindowHrvCalculator
{
	private const double WindowSeconds = 60.0;
	private const double EmitIntervalSeconds = 5.0;

	private readonly LinkedList<Beat> _window = new();
	private DateTimeOffset _lastEmitTime = DateTimeOffset.MinValue;

	/// <summary>
	/// Adds a clean (non-artifact) beat to the rolling window and optionally
	/// returns a new HrvSample if the emit interval has elapsed.
	/// Pass the current baseline and state so the sample is self-contained.
	/// </summary>
	public HrvSample? AddBeat(Beat beat, double baselineRmssd, double baselineHr, DetectorState state)
	{
		if (beat.IsArtifact)
		{
			return null;
		}

		_window.AddLast(beat);
		EvictOldBeats(beat.Timestamp);

		if ((beat.Timestamp - _lastEmitTime).TotalSeconds < EmitIntervalSeconds)
		{
			return null;
		}

		if (_window.Count < 2)
		{
			return null;
		}

		_lastEmitTime = beat.Timestamp;
		return Compute(beat.Timestamp, baselineRmssd, baselineHr, state);
	}

	private void EvictOldBeats(DateTimeOffset now)
	{
		var cutoff = now.AddSeconds(-WindowSeconds);
		while (_window.First is not null && _window.First.Value.Timestamp < cutoff)
		{
			_window.RemoveFirst();
		}
	}

	private HrvSample Compute(DateTimeOffset timestamp, double baselineRmssd, double baselineHr, DetectorState state)
	{
		var rrs = _window.Select(b => b.RrMs).ToArray();

		double rmssd = ComputeRmssd(rrs);
		double pnn50 = ComputePnn50(rrs);
		double meanHr = 60_000.0 / rrs.Average();

		return new HrvSample(timestamp, rmssd, pnn50, meanHr, baselineRmssd, baselineHr, state);
	}

	public static double ComputeRmssd(double[] rrs)
	{
		if (rrs.Length < 2)
		{
			return 0.0;
		}

		double sumSqDiffs = 0.0;
		for (int i = 1; i < rrs.Length; i++)
		{
			double diff = rrs[i] - rrs[i - 1];
			sumSqDiffs += diff * diff;
		}

		return Math.Sqrt(sumSqDiffs / (rrs.Length - 1));
	}

	public static double ComputePnn50(double[] rrs)
	{
		if (rrs.Length < 2)
		{
			return 0.0;
		}

		int count50 = 0;
		for (int i = 1; i < rrs.Length; i++)
		{
			if (Math.Abs(rrs[i] - rrs[i - 1]) > 50.0)
			{
				count50++;
			}
		}

		return count50 / (double)(rrs.Length - 1) * 100.0;
	}
}
