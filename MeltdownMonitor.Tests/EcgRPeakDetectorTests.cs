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
}
