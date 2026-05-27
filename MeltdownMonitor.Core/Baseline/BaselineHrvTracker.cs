using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Core.Baseline;

public class BaselineHrvTracker
{
	// α ≈ 0.005 per sample at 5s cadence ≈ 15-minute effective window
	private const double Alpha = 0.005;
	private const double WarmUpMinutes = 10.0;

	private double _baselineRmssd;
	private double _baselineHr;
	private int _sampleCount;
	private DateTimeOffset _firstSampleTime = DateTimeOffset.MinValue;
	private bool _isWarm;

	public double BaselineRmssd => _baselineRmssd;
	public double BaselineHr => _baselineHr;
	public bool IsWarm => _isWarm;

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

		_sampleCount++;

		if (!_isWarm && (sample.Timestamp - _firstSampleTime).TotalMinutes >= WarmUpMinutes)
		{
			_isWarm = true;
		}
	}

	public void Reset()
	{
		_baselineRmssd = 0;
		_baselineHr = 0;
		_sampleCount = 0;
		_firstSampleTime = DateTimeOffset.MinValue;
		_isWarm = false;
	}
}
