namespace MeltdownMonitor.Core.Hrv;

/// <summary>
/// Streaming R-peak detector for raw ECG (the Polar H10's PMD ECG stream), producing RR intervals
/// for the HRV pipeline. A pragmatic Pan–Tompkins pipeline — 5-point derivative → squaring → moving-
/// window integration → adaptive double-threshold with a refractory period — fed one sample at a time.
///
/// The integrated-signal peak lags the true R-peak by a fixed ~window/2, but RR is the difference of
/// two peak times so the constant lag cancels — RR is accurate even though the absolute peak instants
/// are delayed. This is a deliberately simplified detector (no T-wave discrimination or search-back):
/// enough for clean chest-strap ECG, opt-in, and — like all BLE behaviour — only fully verifiable on
/// the live app with a real sensor. It is platform-neutral and unit-tested.
/// </summary>
public sealed class EcgRPeakDetector
{
	private readonly double _sampleRateHz;
	private readonly int _windowSize;
	private readonly int _refractorySamples;

	private readonly double[] _recent = new double[5]; // newest at index 0, for the 5-point derivative
	private int _recentCount;

	private readonly double[] _integWindow;
	private int _integIndex;
	private double _integSum;

	private long _sampleIndex;
	private double _spki;   // running signal-peak estimate
	private double _npki;   // running noise-peak estimate
	private double _threshold;
	private long _lastPeakIndex = long.MinValue;

	private double _prevInteg;
	private double _prevPrevInteg;
	private bool _primed;

	/// <param name="sampleRateHz">ECG sample rate (130 Hz on the H10).</param>
	public EcgRPeakDetector(double sampleRateHz = 130.0)
	{
		_sampleRateHz = sampleRateHz;
		_windowSize = Math.Max(1, (int)Math.Round(0.150 * sampleRateHz));   // ~150 ms integration window
		_refractorySamples = (int)Math.Round(0.200 * sampleRateHz);          // 200 ms ⇒ ≤ 300 bpm
		_integWindow = new double[_windowSize];
	}

	/// <summary>
	/// True when the most recent <see cref="AddSample"/> call detected an R-peak — including the very
	/// first peak (which yields no RR). Lets the waveform display mark every QRS, not just those that
	/// completed an interval.
	/// </summary>
	public bool LastSampleWasRPeak { get; private set; }

	/// <summary>
	/// Feeds one ECG sample (microvolts). Returns the RR interval in milliseconds when this sample
	/// completes a new beat-to-beat interval (an R-peak detected after a prior one), otherwise null.
	/// <see cref="LastSampleWasRPeak"/> reflects whether this sample was an R-peak regardless of RR.
	/// </summary>
	public double? AddSample(double microVolts)
	{
		LastSampleWasRPeak = false;
		for (int i = _recent.Length - 1; i > 0; i--)
		{
			_recent[i] = _recent[i - 1];
		}

		_recent[0] = microVolts;
		if (_recentCount < _recent.Length)
		{
			_recentCount++;
		}

		_sampleIndex++;

		// 5-point derivative (Pan–Tompkins): emphasises the steep QRS slope, removes baseline drift.
		double derivative = _recentCount >= 5
			? ((2 * _recent[0]) + _recent[1] - _recent[3] - (2 * _recent[4])) / 8.0
			: 0.0;
		double squared = derivative * derivative;

		// Moving-window integration.
		_integSum -= _integWindow[_integIndex];
		_integWindow[_integIndex] = squared;
		_integSum += squared;
		_integIndex = (_integIndex + 1) % _windowSize;
		double integrated = _integSum / _windowSize;

		double? rr = null;

		// Detect a local maximum at the previous integrated sample, then threshold it. Rising side is
		// non-strict and falling side strict so a flat-topped peak — the moving-window integrator holds
		// a plateau while the whole QRS energy burst sits inside its window — is caught at the plateau's
		// last sample rather than skipped entirely.
		if (_primed && _prevInteg >= _prevPrevInteg && _prevInteg > integrated)
		{
			long peakIndex = _sampleIndex - 1;
			bool firstPeak = _lastPeakIndex == long.MinValue;
			if (_prevInteg > _threshold)
			{
				// The first peak has no predecessor to measure a refractory gap against (and the
				// subtraction would overflow), so accept it to anchor timing without emitting an RR.
				if (firstPeak || peakIndex - _lastPeakIndex > _refractorySamples)
				{
					LastSampleWasRPeak = true;
					_spki = (0.125 * _prevInteg) + (0.875 * _spki);
					if (!firstPeak)
					{
						rr = (peakIndex - _lastPeakIndex) / _sampleRateHz * 1000.0;
					}

					_lastPeakIndex = peakIndex;
				}

				// Within the refractory window: a T-wave or noise spike — ignore.
			}
			else
			{
				_npki = (0.125 * _prevInteg) + (0.875 * _npki);
			}

			_threshold = _npki + (0.25 * (_spki - _npki));
		}

		_prevPrevInteg = _prevInteg;
		_prevInteg = integrated;
		_primed = true;

		return rr;
	}

	/// <summary>Clears all state — call on reconnect so a stale peak history can't fabricate an RR.</summary>
	public void Reset()
	{
		Array.Clear(_recent);
		_recentCount = 0;
		Array.Clear(_integWindow);
		_integIndex = 0;
		_integSum = 0;
		_sampleIndex = 0;
		_spki = 0;
		_npki = 0;
		_threshold = 0;
		_lastPeakIndex = long.MinValue;
		_prevInteg = 0;
		_prevPrevInteg = 0;
		_primed = false;
		LastSampleWasRPeak = false;
	}
}
