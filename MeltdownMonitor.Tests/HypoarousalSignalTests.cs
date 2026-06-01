using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.Tests;

[TestClass]
public class HypoarousalSignalTests
{
	// Baseline used throughout: RMSSD 50 ms, HR 70 bpm.
	private const double BaselineRmssd = 50;
	private const double BaselineHr = 70;

	[TestMethod]
	public void DeepCollapse_IsStrong()
	{
		// HR 30% below baseline (49 bpm), RMSSD collapsed to 20% (10 ms).
		// hrFall 0.30 → ramp saturates to 1; quality 0.2 → gate 0.8 → signal 0.8.
		double s = HypoarousalSignal.Compute(rmssd: 10, meanHr: 49, BaselineRmssd, BaselineHr);
		Assert.AreEqual(0.8, s, 1e-9);
	}

	[TestMethod]
	public void GenuineRest_IsZero()
	{
		// HR below baseline but RMSSD *above* it = real vagal rest, not shutdown.
		Assert.AreEqual(0.0, HypoarousalSignal.Compute(rmssd: 70, meanHr: 60, BaselineRmssd, BaselineHr), 1e-9);
	}

	[TestMethod]
	public void Activated_IsZero()
	{
		// HR above baseline = sympathetic activation, the opposite edge.
		Assert.AreEqual(0.0, HypoarousalSignal.Compute(rmssd: 20, meanHr: 90, BaselineRmssd, BaselineHr), 1e-9);
	}

	[TestMethod]
	public void HrFallBelowBand_IsZero()
	{
		// HR only 10% below baseline (63 bpm) sits exactly at the band edge → no signal yet,
		// even with collapsed RMSSD. The HR drop must clear the band before anything accrues.
		Assert.AreEqual(0.0, HypoarousalSignal.Compute(rmssd: 5, meanHr: 63, BaselineRmssd, BaselineHr), 1e-9);
	}

	[TestMethod]
	public void RequiresBothTerms_NeitherAloneSuffices()
	{
		// Deep HR drop but healthy variability → gate is 0.
		Assert.AreEqual(0.0, HypoarousalSignal.Compute(rmssd: 60, meanHr: 49, BaselineRmssd, BaselineHr), 1e-9);
		// Collapsed variability but HR at baseline → ramp is 0.
		Assert.AreEqual(0.0, HypoarousalSignal.Compute(rmssd: 5, meanHr: 70, BaselineRmssd, BaselineHr), 1e-9);
	}

	[TestMethod]
	public void ModerateCollapse_WorkedExamples()
	{
		// HR 20% below (56 bpm), RMSSD 50% (25 ms): ramp clamp((0.20-0.10)/0.15)=0.6667, gate 0.5 → ~0.333.
		Assert.AreEqual(0.3333, HypoarousalSignal.Compute(rmssd: 25, meanHr: 56, BaselineRmssd, BaselineHr), 1e-3);
		// HR 25% below (52.5 bpm), RMSSD 30% (15 ms): ramp saturates to 1, gate 0.7 → 0.70.
		Assert.AreEqual(0.70, HypoarousalSignal.Compute(rmssd: 15, meanHr: 52.5, BaselineRmssd, BaselineHr), 1e-9);
	}

	[TestMethod]
	public void UnusableBaseline_IsZero()
	{
		Assert.AreEqual(0.0, HypoarousalSignal.Compute(rmssd: 50, meanHr: 70, baselineRmssd: 0, baselineHr: 0), 1e-9);
		Assert.AreEqual(0.0, HypoarousalSignal.Compute(rmssd: 50, meanHr: 70, baselineRmssd: -1, baselineHr: 70), 1e-9);
	}

	[TestMethod]
	public void NonFiniteInputs_AreZero()
	{
		Assert.AreEqual(0.0, HypoarousalSignal.Compute(rmssd: double.NaN, meanHr: 50, BaselineRmssd, BaselineHr), 1e-9);
		Assert.AreEqual(0.0, HypoarousalSignal.Compute(rmssd: 10, meanHr: double.NaN, BaselineRmssd, BaselineHr), 1e-9);
		Assert.AreEqual(0.0, HypoarousalSignal.Compute(rmssd: 10, meanHr: 50, baselineRmssd: double.PositiveInfinity, baselineHr: BaselineHr), 1e-9);
	}
}
