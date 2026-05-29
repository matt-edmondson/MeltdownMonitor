using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

[TestClass]
public class RegulationFieldCalculatorTests
{
	private static readonly DetectionThresholds Thresholds = new()
	{
		RmssdWarningDropFraction = 0.30,
		HrWarningRiseFraction = 0.15,
		RmssdAlertingDropFraction = 0.50,
	};

	private static HrvSample Sample(double rmssd, double meanHr,
		double baselineRmssd = 50, double baselineHr = 70) =>
		new(DateTimeOffset.UtcNow, rmssd, 20, meanHr, baselineRmssd, baselineHr, DetectorState.Watching);

	[TestMethod]
	public void AtBaseline_IndexIsZero()
	{
		var r = RegulationFieldCalculator.Compute(Sample(50, 70), Thresholds, warmUpProgress: 1, baselineWarm: true);
		Assert.AreEqual(0.0, r.Index, 0.001);
	}

	[TestMethod]
	public void AtWarningThreshold_IndexIsAboutPointSix()
	{
		// RMSSD 30% below, HR 15% above — both exactly at their Warning thresholds.
		var r = RegulationFieldCalculator.Compute(Sample(35, 80.5), Thresholds, 1, true);
		Assert.AreEqual(0.6, r.Index, 0.02);
	}

	[TestMethod]
	public void SevereActivation_IndexSaturatesToOne()
	{
		// RMSSD 60% below, HR 40% above — well past Warning.
		var r = RegulationFieldCalculator.Compute(Sample(20, 98), Thresholds, 1, true);
		Assert.AreEqual(1.0, r.Index, 0.001);
	}

	[TestMethod]
	public void CalmerThanBaseline_IndexIsNegative()
	{
		// RMSSD above baseline, HR below baseline = rest/recovery.
		var r = RegulationFieldCalculator.Compute(Sample(70, 60), Thresholds, 1, true);
		Assert.IsTrue(r.Index < 0, $"expected negative index, got {r.Index}");
	}

	[TestMethod]
	public void VariabilityQuality_IsRmssdOverBaselineClampedToOne()
	{
		Assert.AreEqual(0.5, RegulationFieldCalculator.Compute(Sample(25, 70), Thresholds, 1, true).VariabilityQuality, 0.001);
		Assert.AreEqual(1.0, RegulationFieldCalculator.Compute(Sample(80, 70), Thresholds, 1, true).VariabilityQuality, 0.001);
		Assert.AreEqual(0.0, RegulationFieldCalculator.Compute(Sample(0, 70), Thresholds, 1, true).VariabilityQuality, 0.001);
	}

	[TestMethod]
	public void NotWarm_ConfidenceFollowsWarmUpProgress()
	{
		var r = RegulationFieldCalculator.Compute(Sample(50, 70), Thresholds, warmUpProgress: 0.4, baselineWarm: false);
		Assert.AreEqual(0.4, r.Confidence, 0.001);
	}

	[TestMethod]
	public void InvalidBaseline_ReturnsNeutralWithNoConfidence()
	{
		var r = RegulationFieldCalculator.Compute(Sample(50, 70, baselineRmssd: 0, baselineHr: 0), Thresholds, 1, true);
		Assert.AreEqual(0.0, r.Index, 0.001);
		Assert.AreEqual(0.0, r.Confidence, 0.001);
		Assert.AreEqual(1.0, r.VariabilityQuality, 0.001);
	}

	[TestMethod]
	public void WarmUpProgress_IsClamped()
	{
		var r = RegulationFieldCalculator.Compute(Sample(50, 70), Thresholds, warmUpProgress: 1.5, baselineWarm: false);
		Assert.AreEqual(1.0, r.Confidence, 0.001);
	}

	[TestMethod]
	public void NaNBaseline_ReturnsNeutralWithNoConfidence()
	{
		var r = RegulationFieldCalculator.Compute(
			Sample(50, 70, baselineRmssd: double.NaN, baselineHr: 70), Thresholds, 1, true);
		Assert.AreEqual(0.0, r.Index, 0.001);
		Assert.AreEqual(0.0, r.Confidence, 0.001);
	}
}
