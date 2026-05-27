using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class PoincarePlotCalculatorTests
{
	[TestMethod]
	public void TooFewIntervals_ReturnsNull()
	{
		Assert.IsNull(PoincarePlotCalculator.Compute([800, 810]));
	}

	[TestMethod]
	public void IdenticalBeats_SD1IsZero()
	{
		double[] rrs = [800, 800, 800, 800, 800];
		var result = PoincarePlotCalculator.Compute(rrs);
		Assert.IsNotNull(result);
		Assert.AreEqual(0.0, result.Value.SD1, 0.001);
	}

	// For identical beats SDNN=0 so SD2=0, ratio is 0
	[TestMethod]
	public void IdenticalBeats_SD2IsZero()
	{
		double[] rrs = [800, 800, 800, 800, 800];
		var result = PoincarePlotCalculator.Compute(rrs);
		Assert.IsNotNull(result);
		Assert.AreEqual(0.0, result.Value.SD2, 0.001);
	}

	// SD1 = RMSSD / sqrt(2). For rrs=[800,850,800,850], RMSSD = 50.
	[TestMethod]
	public void SD1_EqualsRmssdOverSqrt2()
	{
		double[] rrs = [800, 850, 800, 850];
		double rmssd = MeltdownMonitor.Core.Hrv.ShortWindowHrvCalculator.ComputeRmssd(rrs);
		var result = PoincarePlotCalculator.Compute(rrs);
		Assert.IsNotNull(result);
		Assert.AreEqual(rmssd / Math.Sqrt(2.0), result.Value.SD1, 0.001);
	}

	[TestMethod]
	public void Ratio_IsSD1DividedBySD2()
	{
		double[] rrs = [800, 820, 790, 830, 810, 800, 840, 810];
		var result = PoincarePlotCalculator.Compute(rrs);
		Assert.IsNotNull(result);
		var (sd1, sd2, ratio, _) = result.Value;
		if (sd2 > 0)
		{
			Assert.AreEqual(sd1 / sd2, ratio, 0.001);
		}
	}

	[TestMethod]
	public void Sdnn_MatchesManualCalculation()
	{
		double[] rrs = [800, 900, 700, 800];
		double mean = rrs.Average(); // 800
		double variance = rrs.Average(r => (r - mean) * (r - mean));
		double expectedSdnn = Math.Sqrt(variance);

		var result = PoincarePlotCalculator.Compute(rrs);
		Assert.IsNotNull(result);
		Assert.AreEqual(expectedSdnn, result.Value.Sdnn, 0.001);
	}
}
