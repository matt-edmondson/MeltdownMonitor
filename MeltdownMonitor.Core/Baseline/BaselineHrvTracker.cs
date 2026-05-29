using System.Linq;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Core.Baseline;

public class BaselineHrvTracker
{
	// α ≈ 0.005 per sample at 5s cadence ≈ 15-minute effective window
	private const double Alpha = 0.005;
	// Slower alpha for LF/HF since extended metrics arrive every 30s
	private const double AlphaExtended = 0.03;
	private const double WarmUpMinutes = 10.0;

	/// <summary>Look-back window (days) the owner should read for the anchor median.</summary>
	public const int AnchorWindowDays = 7;
	// Recent window whose median seeds the live EWMA at startup.
	private const double WarmStartWindowMinutes = 60.0;
	// Minimum recent clean samples required to warm-start (skip the live warm-up).
	private const int MinWarmStartSamples = 12;
	// Guardrail: the live baseline may not drift more than this fraction from the anchor.
	private const double MaxAnchorDrift = 0.40;

	private double _baselineRmssd;
	private double _baselineHr;
	private double _baselineLfHfRatio;
	private DateTimeOffset _firstSampleTime = DateTimeOffset.MinValue;
	private bool _isWarm;
	private double _anchorRmssd;
	private double _anchorHr;
	private double _anchorLfHfRatio;

	public double BaselineRmssd => _baselineRmssd;
	public double BaselineHr => _baselineHr;

	/// <summary>
	/// Baseline LF/HF ratio (EWMA). Zero until the first extended sample arrives.
	/// </summary>
	public double BaselineLfHfRatio => _baselineLfHfRatio;
	public bool IsWarm => _isWarm;

	/// <summary>0..1 progress toward the warm-up threshold, for UI display.</summary>
	public double WarmUpProgress
	{
		get
		{
			if (_isWarm)
			{
				return 1.0;
			}

			if (_firstSampleTime == DateTimeOffset.MinValue)
			{
				return 0.0;
			}

			double elapsed = (DateTimeOffset.UtcNow - _firstSampleTime).TotalMinutes;
			return Math.Clamp(elapsed / WarmUpMinutes, 0.0, 1.0);
		}
	}

	public void Update(HrvSample sample)
	{
		// Do not update during dysregulated states — prevents baseline from
		// chasing a sustained episode and blinding the detector.
		if (sample.State is DetectorState.Warning or DetectorState.Alerting)
		{
			return;
		}

		if (_firstSampleTime == DateTimeOffset.MinValue)
		{
			_firstSampleTime = sample.Timestamp;
			_baselineRmssd = sample.Rmssd;
			_baselineHr = sample.MeanHr;
		}
		else
		{
			_baselineRmssd = ((1.0 - Alpha) * _baselineRmssd) + (Alpha * sample.Rmssd);
			_baselineHr = ((1.0 - Alpha) * _baselineHr) + (Alpha * sample.MeanHr);
		}

		// Update LF/HF baseline when extended metrics are present
		if (sample.Extended is { LfHfRatio: > 0 } extended)
		{
			if (_baselineLfHfRatio == 0)
			{
				_baselineLfHfRatio = extended.LfHfRatio;
			}
			else
			{
				_baselineLfHfRatio = ((1.0 - AlphaExtended) * _baselineLfHfRatio)
									+ (AlphaExtended * extended.LfHfRatio);
			}
		}

		ClampToAnchor();

		if (!_isWarm && (sample.Timestamp - _firstSampleTime).TotalMinutes >= WarmUpMinutes)
		{
			_isWarm = true;
		}
	}

	/// <summary>
	/// Seeds the baseline from persisted history: a robust (median) long-term anchor
	/// over the whole supplied window, and a warm-start of the live EWMA from the most
	/// recent hour. Clean samples only (no Warning/Alerting states, positive values).
	/// Safe to call once before live samples flow; a no-op when no usable history exists.
	/// </summary>
	public void SeedFromHistory(IReadOnlyList<HrvSample> history)
	{
		List<HrvSample> clean = [.. history.Where(IsClean)];

		_anchorRmssd = Median([.. clean.Where(s => s.Rmssd > 0).Select(s => s.Rmssd)]);
		_anchorHr = Median([.. clean.Where(s => s.MeanHr > 0).Select(s => s.MeanHr)]);
		_anchorLfHfRatio = Median([.. clean.Where(s => s.Extended is { LfHfRatio: > 0 })
			.Select(s => s.Extended!.LfHfRatio)]);

		DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-WarmStartWindowMinutes);
		List<HrvSample> recent = [.. clean.Where(s => s.Timestamp >= cutoff)];
		List<double> recentRmssd = [.. recent.Where(s => s.Rmssd > 0).Select(s => s.Rmssd)];
		List<double> recentHr = [.. recent.Where(s => s.MeanHr > 0).Select(s => s.MeanHr)];

		if (recentRmssd.Count < MinWarmStartSamples || recentHr.Count < MinWarmStartSamples)
		{
			// Not enough recent data to trust a warm start; anchor (if any) still guards
			// the live warm-up that follows.
			return;
		}

		_baselineRmssd = Median(recentRmssd);
		_baselineHr = Median(recentHr);

		List<double> recentLfHf = [.. recent.Where(s => s.Extended is { LfHfRatio: > 0 })
			.Select(s => s.Extended!.LfHfRatio)];
		if (recentLfHf.Count > 0)
		{
			_baselineLfHfRatio = Median(recentLfHf);
		}

		_firstSampleTime = recent.Max(s => s.Timestamp);
		_isWarm = true;
	}

	private static bool IsClean(HrvSample sample) =>
		sample.State is not (DetectorState.Warning or DetectorState.Alerting);

	private static double Median(IReadOnlyList<double> values)
	{
		if (values.Count == 0)
		{
			return 0;
		}

		double[] sorted = [.. values.OrderBy(v => v)];
		int mid = sorted.Length / 2;
		return (sorted.Length % 2 == 0)
			? (sorted[mid - 1] + sorted[mid]) / 2.0
			: sorted[mid];
	}

	// Keep the live EWMA within +/-MaxAnchorDrift of the personalised anchor so a long
	// sub-threshold rough patch cannot silently re-normalise the baseline. No-op until
	// an anchor has been seeded.
	private void ClampToAnchor()
	{
		if (_anchorRmssd > 0)
		{
			_baselineRmssd = Math.Clamp(_baselineRmssd,
				_anchorRmssd * (1.0 - MaxAnchorDrift), _anchorRmssd * (1.0 + MaxAnchorDrift));
		}

		if (_anchorHr > 0)
		{
			_baselineHr = Math.Clamp(_baselineHr,
				_anchorHr * (1.0 - MaxAnchorDrift), _anchorHr * (1.0 + MaxAnchorDrift));
		}

		if (_anchorLfHfRatio > 0)
		{
			_baselineLfHfRatio = Math.Clamp(_baselineLfHfRatio,
				_anchorLfHfRatio * (1.0 - MaxAnchorDrift), _anchorLfHfRatio * (1.0 + MaxAnchorDrift));
		}
	}

	public void Reset()
	{
		_baselineRmssd = 0;
		_baselineHr = 0;
		_baselineLfHfRatio = 0;
		_firstSampleTime = DateTimeOffset.MinValue;
		_isWarm = false;
		_anchorRmssd = 0;
		_anchorHr = 0;
		_anchorLfHfRatio = 0;
	}
}
