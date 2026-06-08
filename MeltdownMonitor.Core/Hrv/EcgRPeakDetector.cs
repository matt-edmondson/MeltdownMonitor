namespace MeltdownMonitor.Core.Hrv;

/// <summary>
/// Streaming R-peak detector for raw ECG (the Polar H10's PMD ECG stream), producing RR intervals
/// for the HRV pipeline. A Pan–Tompkins pipeline — 5-point derivative → squaring → moving-window
/// integration → adaptive double-threshold with a refractory period — fed one sample at a time, with
/// the three additions that make derived RR trustworthy rather than merely plausible:
///
/// <list type="bullet">
/// <item><b>Device-time RR.</b> RR is the difference of the two R-peaks' <i>device</i> timestamps, not
/// a count of received samples. A dropped BLE notification (a whole ECG frame lost over the air) no
/// longer miscounts the interval into a plausible-but-wrong value — the device clock jumps by the true
/// elapsed time, so the gap surfaces as an honest (over-long) RR the artifact filter rejects, instead
/// of a silently halved/short one that slips through. Callers pass each sample's device time; the
/// timestamp-free overload assumes contiguous samples at the configured rate (tests, and a graceful
/// fallback for firmware that doesn't timestamp frames). Non-monotonic/zero device times are clamped
/// to contiguous spacing so a bad clock degrades to the old behaviour rather than corrupting RR.</item>
/// <item><b>Search-back.</b> If no QRS crosses the primary threshold within 1.66× the running mean RR,
/// the largest sub-threshold local max since the last beat (above the secondary threshold) is accepted
/// retroactively — recovering a weak beat that would otherwise double the next interval.</item>
/// <item><b>T-wave discrimination.</b> A peak arriving within 360 ms of the last that carries less than
/// half its QRS energy is treated as a T-wave, not a beat — so a tall T-wave can't halve the RR.</item>
/// <item><b>Learning phase.</b> The thresholds are seeded from the first ~1 s of signal (Pan–Tompkins
/// phase 1) rather than bootstrapping from zero, so the opening beats aren't anchored on noise.</item>
/// </list>
///
/// The integrated-signal peak lags the true R-peak by a fixed ~window/2, but RR is the difference of
/// two peak times so the constant lag cancels. Opt-in, platform-neutral, and unit-tested; like all BLE
/// behaviour the live stream is only fully verifiable on the app with a real sensor.
/// </summary>
public sealed class EcgRPeakDetector
{
	private readonly double _sampleRateHz;
	private readonly double _sampleIntervalSeconds;
	private readonly int _windowSize;
	private readonly double _refractorySeconds;
	private readonly int _learningSamples;

	private const double TWaveWindowSeconds = 0.360;        // a peak this soon after the last may be a T-wave
	private const double SearchBackRrFactor = 1.66;         // re-scan once this × mean RR has elapsed
	private const double MinPlausibleRrSeconds = 0.30;      // 200 bpm — RRs outside this band don't tune the mean
	private const double MaxPlausibleRrSeconds = 2.0;       // 30 bpm
	private const int RrAverageWindow = 8;

	private readonly double[] _recent = new double[5]; // newest at index 0, for the 5-point derivative
	private int _recentCount;

	private readonly double[] _integWindow;
	private int _integIndex;
	private double _integSum;

	private double _spki;   // running signal-peak estimate
	private double _npki;   // running noise-peak estimate

	private double _prevInteg;
	private double _prevPrevInteg;
	private double _prevSampleTime;
	private double _lastSampleTime;
	private bool _primed;

	// Learning phase: accumulate integrated statistics to seed the thresholds.
	private int _learnCount;
	private double _learnMax;
	private double _learnSum;

	// Accepted-peak timing.
	private bool _hasLastPeak;
	private double _lastPeakTime;
	private double _lastQrsAmplitude;

	// Search-back candidate: the largest sub-threshold local max since the last accepted peak.
	private bool _hasCandidate;
	private double _candidateValue;
	private double _candidateTime;

	// Running mean RR (seconds) over recent normal beats, for the search-back timing gate.
	private readonly Queue<double> _recentRr = new();
	private double _rrSum;

	/// <param name="sampleRateHz">ECG sample rate (130 Hz on the H10).</param>
	public EcgRPeakDetector(double sampleRateHz = 130.0)
	{
		_sampleRateHz = sampleRateHz;
		_sampleIntervalSeconds = 1.0 / sampleRateHz;
		_windowSize = Math.Max(1, (int)Math.Round(0.150 * sampleRateHz));   // ~150 ms integration window
		_refractorySeconds = 0.200;                                          // 200 ms ⇒ ≤ 300 bpm
		_learningSamples = Math.Max(1, (int)Math.Round(1.0 * sampleRateHz)); // ~1 s threshold learning
		_integWindow = new double[_windowSize];
	}

	/// <summary>
	/// True when the most recent <see cref="AddSample(double, double)"/> call accepted an R-peak —
	/// including the very first peak (which yields no RR) and search-back recoveries. Lets the waveform
	/// display mark every QRS, not just those that completed an interval.
	/// </summary>
	public bool LastSampleWasRPeak { get; private set; }

	/// <summary>
	/// Feeds one ECG sample (microvolts), assuming samples are contiguous at the configured rate. Use
	/// the timestamped overload when device frame timestamps are available so dropped frames are handled
	/// honestly. Returns the RR interval (ms) when this sample completes a new interval, otherwise null.
	/// </summary>
	public double? AddSample(double microVolts) =>
		AddSample(microVolts, _lastSampleTime + _sampleIntervalSeconds);

	/// <summary>
	/// Feeds one ECG sample (microvolts) at its device time (seconds, monotonic). Returns the RR
	/// interval in milliseconds when this sample completes a new beat-to-beat interval (an R-peak after
	/// a prior one, or a search-back recovery), otherwise null. <see cref="LastSampleWasRPeak"/> reflects
	/// whether an R-peak was accepted on this call. Times that don't advance past the previous sample
	/// (a zero or non-monotonic device clock) are clamped to one sample interval, so RR never goes
	/// negative and a bad clock degrades to contiguous-sample behaviour.
	/// </summary>
	public double? AddSample(double microVolts, double sampleTimeSeconds)
	{
		LastSampleWasRPeak = false;

		if (sampleTimeSeconds <= _lastSampleTime)
		{
			sampleTimeSeconds = _lastSampleTime + _sampleIntervalSeconds;
		}

		for (int i = _recent.Length - 1; i > 0; i--)
		{
			_recent[i] = _recent[i - 1];
		}

		_recent[0] = microVolts;
		if (_recentCount < _recent.Length)
		{
			_recentCount++;
		}

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

		// Learning phase: seed the thresholds from the first ~1 s rather than bootstrapping from zero.
		if (_learnCount < _learningSamples)
		{
			_learnCount++;
			_learnMax = Math.Max(_learnMax, integrated);
			_learnSum += integrated;
			if (_learnCount == _learningSamples)
			{
				_spki = 0.25 * _learnMax;
				_npki = 0.5 * (_learnSum / _learningSamples);
			}
		}

		double? rr = null;

		// Search-back first, so it acts on an earlier sample than the next real peak (they never collide):
		// if too long has passed since the last beat and a sub-threshold candidate is waiting, accept it.
		if (_hasLastPeak && _rrSum > 0 && _hasCandidate
			&& (sampleTimeSeconds - _lastPeakTime) > (SearchBackRrFactor * MeanRr))
		{
			_spki = (0.25 * _candidateValue) + (0.75 * _spki);
			rr = (_candidateTime - _lastPeakTime) * 1000.0;
			UpdateMeanRr(_candidateTime - _lastPeakTime);
			_lastPeakTime = _candidateTime;
			_lastQrsAmplitude = _candidateValue;
			_hasCandidate = false;
			LastSampleWasRPeak = true;
		}

		// Detect a local maximum at the previous integrated sample. Rising side non-strict, falling side
		// strict, so a flat-topped plateau (the integrator holds the QRS energy burst) is caught at its
		// last sample rather than skipped.
		if (rr is null && _primed && _prevInteg >= _prevPrevInteg && _prevInteg > integrated)
		{
			double peakValue = _prevInteg;
			double peakTime = _prevSampleTime;
			bool firstPeak = !_hasLastPeak;
			double threshold = ThresholdI1;

			if (peakValue > threshold)
			{
				double interval = peakTime - _lastPeakTime;
				if (firstPeak || interval > _refractorySeconds)
				{
					bool isTwave = !firstPeak && interval < TWaveWindowSeconds
						&& peakValue < (0.5 * _lastQrsAmplitude);
					if (isTwave)
					{
						// A T-wave (or other low-energy bump): treat as noise, don't emit a beat.
						_npki = (0.125 * peakValue) + (0.875 * _npki);
					}
					else
					{
						_spki = (0.125 * peakValue) + (0.875 * _spki);
						if (!firstPeak)
						{
							rr = interval * 1000.0;
							UpdateMeanRr(interval);
						}

						_lastPeakTime = peakTime;
						_lastQrsAmplitude = peakValue;
						_hasLastPeak = true;
						_hasCandidate = false;
						LastSampleWasRPeak = true;
					}
				}

				// Within the refractory window: a T-wave or noise spike — ignore.
			}
			else
			{
				_npki = (0.125 * peakValue) + (0.875 * _npki);

				// Remember the largest sub-threshold-but-credible local max as a search-back candidate.
				// It must sit beyond the T-wave window: a missed beat lands ~one RR out, whereas a low-energy
				// bump within 360 ms of the last beat is far more likely a T-wave, which must never be recovered.
				if (_hasLastPeak && peakValue > ThresholdI2
					&& (peakTime - _lastPeakTime) > TWaveWindowSeconds
					&& (!_hasCandidate || peakValue > _candidateValue))
				{
					_hasCandidate = true;
					_candidateValue = peakValue;
					_candidateTime = peakTime;
				}
			}
		}

		_prevPrevInteg = _prevInteg;
		_prevInteg = integrated;
		_prevSampleTime = sampleTimeSeconds;
		_lastSampleTime = sampleTimeSeconds;
		_primed = true;

		return rr;
	}

	// Primary detection threshold (Pan–Tompkins I1) and the secondary (I2) used for search-back.
	private double ThresholdI1 => _npki + (0.25 * (_spki - _npki));
	private double ThresholdI2 => 0.5 * ThresholdI1;

	private double MeanRr => _recentRr.Count > 0 ? _rrSum / _recentRr.Count : 0.0;

	private void UpdateMeanRr(double rrSeconds)
	{
		if (rrSeconds < MinPlausibleRrSeconds || rrSeconds > MaxPlausibleRrSeconds)
		{
			return;
		}

		_recentRr.Enqueue(rrSeconds);
		_rrSum += rrSeconds;
		while (_recentRr.Count > RrAverageWindow)
		{
			_rrSum -= _recentRr.Dequeue();
		}
	}

	/// <summary>Clears all state — call on reconnect so a stale peak history can't fabricate an RR.</summary>
	public void Reset()
	{
		Array.Clear(_recent);
		_recentCount = 0;
		Array.Clear(_integWindow);
		_integIndex = 0;
		_integSum = 0;
		_spki = 0;
		_npki = 0;
		_prevInteg = 0;
		_prevPrevInteg = 0;
		_prevSampleTime = 0;
		_lastSampleTime = 0;
		_primed = false;
		_learnCount = 0;
		_learnMax = 0;
		_learnSum = 0;
		_hasLastPeak = false;
		_lastPeakTime = 0;
		_lastQrsAmplitude = 0;
		_hasCandidate = false;
		_candidateValue = 0;
		_candidateTime = 0;
		_recentRr.Clear();
		_rrSum = 0;
		LastSampleWasRPeak = false;
	}
}
