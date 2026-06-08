using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class EcgRPeakDetectorTests
{
	private const double Fs = 130.0;

	// Builds a synthetic ECG: a sharp triangular QRS spike every `periodSamples`, flat baseline
	// between. Sharp slopes are what the derivative-based detector keys on.
	private static double[] SyntheticEcg(int beats, int periodSamples, double amplitude = 1000.0, int firstPeak = 20)
	{
		int length = firstPeak + (beats * periodSamples) + periodSamples;
		var signal = new double[length];
		for (int b = 0; b < beats; b++)
		{
			int centre = firstPeak + (b * periodSamples);
			// Triangle: up over 3 samples, down over 3.
			for (int k = -3; k <= 3; k++)
			{
				int i = centre + k;
				if (i >= 0 && i < length)
				{
					signal[i] = amplitude * (1.0 - (Math.Abs(k) / 3.0));
				}
			}
		}

		return signal;
	}

	[TestMethod]
	public void DetectsRegularRhythm_RecoversRrAndCount()
	{
		// 30 beats at 100 samples/beat ⇒ RR = 100/130 s ≈ 769.2 ms.
		const int beats = 30;
		const int period = 100;
		double expectedRr = period / Fs * 1000.0;

		var detector = new EcgRPeakDetector(Fs);
		var rrs = new List<double>();
		foreach (double s in SyntheticEcg(beats, period))
		{
			if (detector.AddSample(s) is { } rr)
			{
				rrs.Add(rr);
			}
		}

		// First peak yields no RR, so at most beats-1 intervals; allow the detector a beat or two to
		// settle its adaptive threshold.
		Assert.IsTrue(rrs.Count >= beats - 3, $"Expected ~{beats - 1} intervals, got {rrs.Count}.");
		foreach (double rr in rrs)
		{
			Assert.AreEqual(expectedRr, rr, 12.0, "Recovered RR should match the synthetic period within a sample or two.");
		}
	}

	[TestMethod]
	public void TracksRateChange()
	{
		// Fast run (period 80 ≈ 615 ms) then slow run (period 130 = 1000 ms).
		var detector = new EcgRPeakDetector(Fs);
		var rrs = new List<double>();
		void Feed(double[] sig)
		{
			foreach (double s in sig)
			{
				if (detector.AddSample(s) is { } rr)
				{
					rrs.Add(rr);
				}
			}
		}

		Feed(SyntheticEcg(beats: 15, periodSamples: 80));
		Feed(SyntheticEcg(beats: 15, periodSamples: 130));

		Assert.IsTrue(rrs.Count > 10);
		double last = rrs[^1];
		Assert.AreEqual(1000.0, last, 15.0, "The final intervals should reflect the slower rate.");
	}

	[TestMethod]
	public void LastSampleWasRPeak_FlagsEveryPeakIncludingTheFirst()
	{
		// 6 beats ⇒ 6 peaks (the first yields no RR but is still flagged), 5 intervals.
		var detector = new EcgRPeakDetector(Fs);
		int peakFlags = 0;
		int intervals = 0;
		foreach (double s in SyntheticEcg(beats: 6, periodSamples: 100))
		{
			double? rr = detector.AddSample(s);
			if (detector.LastSampleWasRPeak)
			{
				peakFlags++;
			}

			if (rr is not null)
			{
				intervals++;
			}
		}

		Assert.AreEqual(peakFlags - 1, intervals, "Every peak but the first completes an interval.");
		Assert.IsTrue(peakFlags >= 5, $"Expected ~6 peak flags, got {peakFlags}.");
	}

	[TestMethod]
	public void FlatSignal_ProducesNoIntervals()
	{
		var detector = new EcgRPeakDetector(Fs);
		var rrs = new List<double>();
		for (int i = 0; i < 500; i++)
		{
			if (detector.AddSample(0.0) is { } rr)
			{
				rrs.Add(rr);
			}
		}

		Assert.AreEqual(0, rrs.Count, "A flat trace has no R-peaks.");
	}

	[TestMethod]
	public void Reset_ClearsPeakHistory()
	{
		var detector = new EcgRPeakDetector(Fs);
		foreach (double s in SyntheticEcg(beats: 5, periodSamples: 100))
		{
			detector.AddSample(s);
		}

		detector.Reset();

		// After reset the first new peak must not fabricate an RR against a pre-reset peak.
		var rrs = new List<double>();
		foreach (double s in SyntheticEcg(beats: 3, periodSamples: 100))
		{
			if (detector.AddSample(s) is { } rr)
			{
				rrs.Add(rr);
			}
		}

		// 3 beats ⇒ at most 2 intervals, none absurdly large from a stale anchor.
		Assert.IsTrue(rrs.Count <= 2);
		foreach (double rr in rrs)
		{
			Assert.IsTrue(rr < 1500, "No interval should span the reset boundary.");
		}
	}

	// Builds a synthetic ECG with a per-beat amplitude, so a single weak/strong beat can be injected.
	private static double[] SyntheticEcgVariable(double[] amplitudes, int periodSamples, int firstPeak = 20)
	{
		int beats = amplitudes.Length;
		int length = firstPeak + (beats * periodSamples) + periodSamples;
		var signal = new double[length];
		for (int b = 0; b < beats; b++)
		{
			int centre = firstPeak + (b * periodSamples);
			for (int k = -3; k <= 3; k++)
			{
				int i = centre + k;
				if (i >= 0 && i < length)
				{
					signal[i] = amplitudes[b] * (1.0 - (Math.Abs(k) / 3.0));
				}
			}
		}

		return signal;
	}

	[TestMethod]
	public void TimestampedOverload_MatchesContiguousCounting()
	{
		// With contiguous device times the timestamped overload must produce exactly the RR the
		// timestamp-free (sample-counting) overload does — the device-time path is a superset.
		double[] sig = SyntheticEcg(beats: 12, periodSamples: 100);
		double dt = 1.0 / Fs;

		var auto = new EcgRPeakDetector(Fs);
		var timed = new EcgRPeakDetector(Fs);
		var autoRr = new List<double>();
		var timedRr = new List<double>();
		for (int i = 0; i < sig.Length; i++)
		{
			if (auto.AddSample(sig[i]) is { } a) autoRr.Add(a);
			if (timed.AddSample(sig[i], i * dt) is { } t) timedRr.Add(t);
		}

		Assert.AreEqual(autoRr.Count, timedRr.Count);
		for (int i = 0; i < autoRr.Count; i++)
		{
			// Accumulated vs multiplied sample times differ only in floating-point ULPs.
			Assert.AreEqual(autoRr[i], timedRr[i], 1e-6);
		}
	}

	[TestMethod]
	public void DroppedFrame_RrReflectsTrueElapsedTime()
	{
		// Simulate a lost BLE frame: drop the samples around one beat but keep feeding the rest with their
		// real device times. The interval spanning the gap must reflect the true ~2x elapsed time (so the
		// artifact filter can reject it) rather than being miscounted into a plausible-but-wrong short RR.
		const int period = 100;
		double dt = 1.0 / Fs;
		double expected = period / Fs * 1000.0;
		double[] sig = SyntheticEcg(beats: 10, periodSamples: period);

		int droppedCentre = 20 + (5 * period);
		int dropStart = droppedCentre - 15;
		int dropEnd = droppedCentre + 15;

		var detector = new EcgRPeakDetector(Fs);
		var rrs = new List<double>();
		for (int i = 0; i < sig.Length; i++)
		{
			if (i >= dropStart && i <= dropEnd)
			{
				continue; // this frame never arrived
			}

			if (detector.AddSample(sig[i], i * dt) is { } rr)
			{
				rrs.Add(rr);
			}
		}

		Assert.IsTrue(
			rrs.Any(r => r > 1.5 * expected && r < 2.5 * expected),
			$"Cross-gap RR should be ~2x the period (true elapsed), got: {string.Join(", ", rrs.Select(x => Math.Round(x)))}");
	}

	[TestMethod]
	public void SearchBack_RecoversWeakBeat()
	{
		// One beat far weaker than its neighbours falls under the primary threshold. Without search-back the
		// next interval doubles; with it, the weak beat is recovered and every interval stays near one period.
		const int period = 100;
		double dt = 1.0 / Fs;
		double expected = period / Fs * 1000.0;
		var amplitudes = new double[12];
		Array.Fill(amplitudes, 1000.0);
		amplitudes[6] = 450.0; // ~0.2x the integrated energy ⇒ below I1, above I2

		double[] sig = SyntheticEcgVariable(amplitudes, period);
		var detector = new EcgRPeakDetector(Fs);
		var rrs = new List<double>();
		int peaks = 0;
		for (int i = 0; i < sig.Length; i++)
		{
			double? rr = detector.AddSample(sig[i], i * dt);
			if (detector.LastSampleWasRPeak) peaks++;
			if (rr is { } v) rrs.Add(v);
		}

		Assert.IsTrue(
			rrs.All(r => r < 1.6 * expected),
			$"Search-back should prevent a doubled interval across the weak beat, got: {string.Join(", ", rrs.Select(x => Math.Round(x)))}");
		Assert.IsTrue(peaks >= amplitudes.Length - 1, $"The weak beat should be recovered; detected {peaks} peaks.");
	}

	[TestMethod]
	public void TWave_DoesNotProduceSpuriousBeat()
	{
		// Each QRS is followed ~285 ms later by a broad bump at ~0.65x amplitude — a T-wave. It clears the
		// refractory window and (without discrimination) the primary threshold, so it must be rejected on
		// its low QRS energy, not registered as an extra (short-interval) beat.
		const int period = 100;
		const int firstPeak = 20;
		double dt = 1.0 / Fs;
		double expected = period / Fs * 1000.0;
		const int beats = 12;
		int length = firstPeak + (beats * period) + period;
		var sig = new double[length];
		for (int b = 0; b < beats; b++)
		{
			int c = firstPeak + (b * period);
			for (int k = -3; k <= 3; k++)
			{
				int i = c + k;
				if (i >= 0 && i < length) sig[i] += 1000.0 * (1.0 - (Math.Abs(k) / 3.0));
			}

			int tc = c + 37; // ~285 ms after the QRS — past the 200 ms refractory
			for (int k = -4; k <= 4; k++)
			{
				int i = tc + k;
				if (i >= 0 && i < length) sig[i] += 650.0 * (1.0 - (Math.Abs(k) / 4.0));
			}
		}

		var detector = new EcgRPeakDetector(Fs);
		var rrs = new List<double>();
		for (int i = 0; i < length; i++)
		{
			if (detector.AddSample(sig[i], i * dt) is { } rr) rrs.Add(rr);
		}

		Assert.IsTrue(
			rrs.All(r => r > 0.7 * expected),
			$"T-waves must not create short intervals, got: {string.Join(", ", rrs.Select(x => Math.Round(x)))}");
	}
}
