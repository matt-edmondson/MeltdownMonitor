using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.Core.Hrv;

public class ShortWindowHrvCalculator
{
	private const double WindowSeconds = 60.0;
	private const double ExtendedWindowSeconds = 300.0; // 5-minute clinical standard
	private const double EmitIntervalSeconds = 5.0;
	private const double ExtendedComputeIntervalSeconds = 30.0;

	private readonly LinkedList<Beat> _shortWindow = new();
	private readonly LinkedList<Beat> _extendedWindow = new();
	private DateTimeOffset _lastEmitTime = DateTimeOffset.MinValue;
	private DateTimeOffset _lastExtendedComputeTime = DateTimeOffset.MinValue;
	private ExtendedHrvMetrics? _latestExtended;

	/// <summary>
	/// Adds a clean beat to the rolling windows and optionally returns a new
	/// HrvSample. Extended metrics are recomputed every 30 s when the 5-minute
	/// window holds at least 2 minutes of data.
	/// </summary>
	public HrvSample? AddBeat(
		Beat beat,
		double baselineRmssd,
		double baselineHr,
		DetectorState state,
		double baselineLfHfRatio = 0)
	{
		if (beat.IsArtifact)
		{
			return null;
		}

		_shortWindow.AddLast(beat);
		_extendedWindow.AddLast(beat);
		EvictOldBeats(beat.Timestamp);

		if ((beat.Timestamp - _lastEmitTime).TotalSeconds < EmitIntervalSeconds)
		{
			return null;
		}

		if (_shortWindow.Count < 2)
		{
			return null;
		}

		// Refresh extended metrics on their own slower cadence
		if ((beat.Timestamp - _lastExtendedComputeTime).TotalSeconds >= ExtendedComputeIntervalSeconds)
		{
			_latestExtended = ComputeExtended();
			_lastExtendedComputeTime = beat.Timestamp;
		}

		_lastEmitTime = beat.Timestamp;
		return Compute(beat.Timestamp, baselineRmssd, baselineHr, state, baselineLfHfRatio);
	}

	private void EvictOldBeats(DateTimeOffset now)
	{
		var shortCutoff = now.AddSeconds(-WindowSeconds);
		while (_shortWindow.First is not null && _shortWindow.First.Value.Timestamp < shortCutoff)
		{
			_shortWindow.RemoveFirst();
		}

		var extendedCutoff = now.AddSeconds(-ExtendedWindowSeconds);
		while (_extendedWindow.First is not null && _extendedWindow.First.Value.Timestamp < extendedCutoff)
		{
			_extendedWindow.RemoveFirst();
		}
	}

	private HrvSample Compute(
		DateTimeOffset timestamp,
		double baselineRmssd,
		double baselineHr,
		DetectorState state,
		double baselineLfHfRatio)
	{
		var rrs = _shortWindow.Select(b => b.RrMs).ToArray();

		double rmssd = ComputeRmssd(rrs);
		double pnn50 = ComputePnn50(rrs);
		double meanHr = 60_000.0 / rrs.Average();

		return new HrvSample(timestamp, rmssd, pnn50, meanHr, baselineRmssd, baselineHr, state)
		{
			Extended = _latestExtended,
			BaselineLfHfRatio = baselineLfHfRatio,
		};
	}

	private ExtendedHrvMetrics? ComputeExtended()
	{
		if (_extendedWindow.Count < 3)
		{
			return null;
		}

		var rrs = _extendedWindow.Select(b => b.RrMs).ToArray();

		var poincare = PoincarePlotCalculator.Compute(rrs);
		if (poincare is null)
		{
			return null;
		}

		var (sd1, sd2, ratio, sdnn) = poincare.Value;

		var freqResult = FrequencyDomainHrvCalculator.Compute(rrs);
		if (freqResult is null)
		{
			// Not enough data for frequency domain yet — return Poincaré only
			return new ExtendedHrvMetrics(0, 0, 0, sd1, sd2, ratio, sdnn);
		}

		var (lf, hf, lfHf) = freqResult.Value;
		return new ExtendedHrvMetrics(lf, hf, lfHf, sd1, sd2, ratio, sdnn);
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
