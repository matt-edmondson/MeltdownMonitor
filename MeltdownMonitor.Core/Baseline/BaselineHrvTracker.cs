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

	private double _baselineRmssd;
	private double _baselineHr;
	private double _baselineLfHfRatio;
	private DateTimeOffset _firstSampleTime = DateTimeOffset.MinValue;
	private bool _isWarm;

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

		if (!_isWarm && (sample.Timestamp - _firstSampleTime).TotalMinutes >= WarmUpMinutes)
		{
			_isWarm = true;
		}
	}

	public void Reset()
	{
		_baselineRmssd = 0;
		_baselineHr = 0;
		_baselineLfHfRatio = 0;
		_firstSampleTime = DateTimeOffset.MinValue;
		_isWarm = false;
	}
}
