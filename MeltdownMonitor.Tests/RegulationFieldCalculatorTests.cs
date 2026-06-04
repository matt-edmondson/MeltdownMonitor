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

	private static HrvSample WithExtended(double rmssd, double meanHr, double sd1Sd2Ratio,
		double lfHfRatio, double baselineLfHf) =>
		new HrvSample(DateTimeOffset.UtcNow, rmssd, 20, meanHr, 50, 70, DetectorState.Watching)
		{
			BaselineLfHfRatio = baselineLfHf,
			Extended = new ExtendedHrvMetrics(
				LfPowerMs2: 0, HfPowerMs2: 0, LfHfRatio: lfHfRatio,
				SD1: 0, SD2: 0, SD1SD2Ratio: sd1Sd2Ratio, Sdnn: 0),
		};

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
	public void SevereActivation_IndexExceedsDisplayRange()
	{
		// RMSSD 60% below, HR 40% above — well past Warning. Index is not clamped so it
		// exceeds the ±1 display range; the marker and heatmap clip it, but the raw value
		// is preserved so extreme readings don't pile up in the edge histogram bucket.
		var r = RegulationFieldCalculator.Compute(Sample(20, 98), Thresholds, 1, true);
		Assert.IsTrue(r.Index > 1.0, $"expected index > 1 for severe activation, got {r.Index}");
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
	public void VagalTone_IsBaselineCentredAndSymmetricInLogRatio()
	{
		// At baseline → crossover (0.5).
		Assert.AreEqual(0.5, RegulationFieldCalculator.Compute(Sample(50, 70), Thresholds, 1, true).VagalTone, 0.001);

		// Equal proportional moves either side of baseline are symmetric about 0.5 (×2 vs ÷2).
		double above = RegulationFieldCalculator.Compute(Sample(100, 70), Thresholds, 1, true).VagalTone;
		double below = RegulationFieldCalculator.Compute(Sample(25, 70), Thresholds, 1, true).VagalTone;
		Assert.IsTrue(above > 0.5, $"above baseline should ride toward STEADY, got {above}");
		Assert.IsTrue(below < 0.5, $"below baseline should lift toward FRAGILE, got {below}");
		Assert.AreEqual(above - 0.5, 0.5 - below, 0.001, "log-ratio mapping is symmetric about the crossover");

		// Unlike VariabilityQuality, it keeps resolution *above* baseline rather than saturating at 1.
		double higher = RegulationFieldCalculator.Compute(Sample(200, 70), Thresholds, 1, true).VagalTone;
		Assert.IsTrue(higher > above, $"more RMSSD above baseline should ride lower/steadier, got {higher} vs {above}");
		Assert.IsTrue(higher < 1.0, $"should stay within range without clamping, got {higher}");
	}

	[TestMethod]
	public void VagalTone_NeutralWhenBaselineUnusable()
	{
		var r = RegulationFieldCalculator.Compute(Sample(50, 70, baselineRmssd: 0, baselineHr: 0), Thresholds, 1, true);
		Assert.AreEqual(0.5, r.VagalTone, 0.001);
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

	[TestMethod]
	public void LobeRoundness_DefaultsToNeutral_WhenNoExtended()
	{
		var r = RegulationFieldCalculator.Compute(Sample(50, 70), Thresholds, 1, true);
		Assert.AreEqual(0.5, r.LobeRoundness, 0.001);
	}

	[TestMethod]
	public void LobeRoundness_MapsSd1Sd2RatioBand()
	{
		// Band [0.2, 0.6] → [0, 1], clamped.
		Assert.AreEqual(0.0, RegulationFieldCalculator.Compute(WithExtended(50, 70, 0.2, 0, 0), Thresholds, 1, true).LobeRoundness, 0.001);
		Assert.AreEqual(0.5, RegulationFieldCalculator.Compute(WithExtended(50, 70, 0.4, 0, 0), Thresholds, 1, true).LobeRoundness, 0.001);
		Assert.AreEqual(1.0, RegulationFieldCalculator.Compute(WithExtended(50, 70, 0.6, 0, 0), Thresholds, 1, true).LobeRoundness, 0.001);
		Assert.AreEqual(1.0, RegulationFieldCalculator.Compute(WithExtended(50, 70, 0.9, 0, 0), Thresholds, 1, true).LobeRoundness, 0.001);
	}

	[TestMethod]
	public void LfHfBalance_ZeroWhenNoExtendedOrNoBaseline()
	{
		Assert.AreEqual(0.0, RegulationFieldCalculator.Compute(Sample(50, 70), Thresholds, 1, true).LfHfBalance, 0.001);
		Assert.AreEqual(0.0, RegulationFieldCalculator.Compute(WithExtended(50, 70, 0.4, 1.5, 0), Thresholds, 1, true).LfHfBalance, 0.001);
	}

	[TestMethod]
	public void LfHfBalance_SignedRelativeToBaseline_Clamped()
	{
		// 50% above baseline → +0.5
		Assert.AreEqual(0.5, RegulationFieldCalculator.Compute(WithExtended(50, 70, 0.4, 1.5, 1.0), Thresholds, 1, true).LfHfBalance, 0.001);
		// 50% below baseline → -0.5
		Assert.AreEqual(-0.5, RegulationFieldCalculator.Compute(WithExtended(50, 70, 0.4, 1.0, 2.0), Thresholds, 1, true).LfHfBalance, 0.001);
		// 400% above → clamps to +1
		Assert.AreEqual(1.0, RegulationFieldCalculator.Compute(WithExtended(50, 70, 0.4, 5.0, 1.0), Thresholds, 1, true).LfHfBalance, 0.001);
	}

	[TestMethod]
	public void Hypoarousal_HighWhenHrFarBelowBaselineAndVariabilityCollapsed()
	{
		// HR ~30% below baseline, RMSSD collapsed to 20% of baseline = low-arousal collapse.
		var r = RegulationFieldCalculator.Compute(Sample(rmssd: 10, meanHr: 49), Thresholds, 1, true);
		Assert.IsTrue(r.Hypoarousal > 0.7, $"expected strong hypoarousal, got {r.Hypoarousal}");
	}

	[TestMethod]
	public void Hypoarousal_ZeroForGenuineRest_HrBelowButVariabilityHigh()
	{
		// HR below baseline but RMSSD above baseline = real vagal rest, not shutdown.
		var r = RegulationFieldCalculator.Compute(Sample(rmssd: 70, meanHr: 60), Thresholds, 1, true);
		Assert.AreEqual(0.0, r.Hypoarousal, 0.001);
	}

	[TestMethod]
	public void Hypoarousal_ZeroWhenActivated()
	{
		// HR above baseline = sympathetic activation, not hypoarousal.
		var r = RegulationFieldCalculator.Compute(Sample(rmssd: 20, meanHr: 90), Thresholds, 1, true);
		Assert.AreEqual(0.0, r.Hypoarousal, 0.001);
	}

	[TestMethod]
	public void Hypoarousal_ZeroWhenBaselineUnusable()
	{
		var r = RegulationFieldCalculator.Compute(Sample(50, 70, baselineRmssd: 0, baselineHr: 0), Thresholds, 1, true);
		Assert.AreEqual(0.0, r.Hypoarousal, 0.001);
	}
}
