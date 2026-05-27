using System.Numerics;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class FrequencyDomainHrvCalculatorTests
{
	[TestMethod]
	public void TooShortSignal_ReturnsNull()
	{
		// 30 seconds of beats at ~75 bpm (800 ms RR)
		double[] rrs = Enumerable.Repeat(800.0, 38).ToArray(); // ~30s
		Assert.IsNull(FrequencyDomainHrvCalculator.Compute(rrs));
	}

	[TestMethod]
	public void MinimumWindow_ReturnsResult()
	{
		// ~130 s at 75 bpm — comfortably above the 120 s threshold
		double[] rrs = Enumerable.Repeat(800.0, 165).ToArray();
		var result = FrequencyDomainHrvCalculator.Compute(rrs);
		Assert.IsNotNull(result);
	}

	// A pure HF sine wave should have HF >> LF
	[TestMethod]
	public void HfSine_MostPowerInHfBand()
	{
		double[] rrs = MakeSineModulatedRrs(frequencyHz: 0.25, amplitudeMs: 30, durationSec: 300);
		var result = FrequencyDomainHrvCalculator.Compute(rrs);
		Assert.IsNotNull(result);
		var (lf, hf, ratio) = result.Value;
		Assert.IsTrue(hf > lf, $"Expected HF ({hf:F2}) > LF ({lf:F2}) for 0.25 Hz sine");
		Assert.IsTrue(ratio < 1.0, $"LF/HF should be < 1, got {ratio:F3}");
	}

	// A pure LF sine wave should have LF >> HF
	[TestMethod]
	public void LfSine_MostPowerInLfBand()
	{
		double[] rrs = MakeSineModulatedRrs(frequencyHz: 0.10, amplitudeMs: 30, durationSec: 300);
		var result = FrequencyDomainHrvCalculator.Compute(rrs);
		Assert.IsNotNull(result);
		var (lf, hf, ratio) = result.Value;
		Assert.IsTrue(lf > hf, $"Expected LF ({lf:F2}) > HF ({hf:F2}) for 0.10 Hz sine");
		Assert.IsTrue(ratio > 1.0, $"LF/HF should be > 1, got {ratio:F3}");
	}

	// Both bands present: total power ≈ A²/2 + B²/2
	[TestMethod]
	public void BothBands_TotalPowerApproximate()
	{
		double ampLf = 20, ampHf = 15;
		double[] lfRrs = MakeSineModulatedRrs(0.10, ampLf, 300);
		double[] hfRrs = MakeSineModulatedRrs(0.25, ampHf, 300);
		double[] combined = lfRrs.Zip(hfRrs, (a, b) => 800 + (a - 800) + (b - 800)).ToArray();

		var result = FrequencyDomainHrvCalculator.Compute(combined);
		Assert.IsNotNull(result);
		var (lf, hf, _) = result.Value;

		// Total spectral power should be within 50% of expected (A²/2 each band)
		double expectedLf = ampLf * ampLf / 2.0;
		double expectedHf = ampHf * ampHf / 2.0;
		Assert.IsTrue(lf > expectedLf * 0.5 && lf < expectedLf * 2.0,
			$"LF power {lf:F1} not in range [{expectedLf * 0.5:F1}, {expectedLf * 2.0:F1}]");
		Assert.IsTrue(hf > expectedHf * 0.5 && hf < expectedHf * 2.0,
			$"HF power {hf:F1} not in range [{expectedHf * 0.5:F1}, {expectedHf * 2.0:F1}]");
	}

	[TestMethod]
	public void Fft_KnownSine_PeakAtCorrectBin()
	{
		// A 4-point FFT of [1, 0, -1, 0] should have peaks at bins 1 and 3
		var data = new Complex[] { 1, 0, -1, 0 };
		FrequencyDomainHrvCalculator.Fft(data);
		// |X[1]| = |X[3]| = 2, |X[0]| = |X[2]| = 0
		Assert.AreEqual(0.0, data[0].Magnitude, 1e-9);
		Assert.AreEqual(2.0, data[1].Magnitude, 1e-9);
		Assert.AreEqual(0.0, data[2].Magnitude, 1e-9);
		Assert.AreEqual(2.0, data[3].Magnitude, 1e-9);
	}

	/// <summary>Creates RR intervals modulated by a sine wave at the given frequency.</summary>
	private static double[] MakeSineModulatedRrs(double frequencyHz, double amplitudeMs, double durationSec)
	{
		var rrs = new List<double>();
		double t = 0;
		while (t < durationSec)
		{
			double rr = 800 + amplitudeMs * Math.Sin(2 * Math.PI * frequencyHz * t);
			rrs.Add(rr);
			t += rr / 1000.0;
		}

		return [.. rrs];
	}
}
