using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

[TestClass]
public class HypoarousalVisualTests
{
	private static RegulationDynamics Rising => new(0.03, RegulationTrend.Escalating, 0.6);
	private static RegulationDynamics Falling => new(-0.03, RegulationTrend.DeEscalating, 0.6);
	private static RegulationDynamics Flat => RegulationDynamics.Steady;

	[TestMethod]
	public void Intensity_ZeroAtOrBelowFloor()
	{
		Assert.AreEqual(0.0, HypoarousalVisual.Intensity(0.0), 1e-9);
		Assert.AreEqual(0.0, HypoarousalVisual.Intensity(HypoarousalVisual.Floor), 1e-9);
		Assert.AreEqual(0.0, HypoarousalVisual.Intensity(HypoarousalVisual.Floor - 0.01), 1e-9);
	}

	[TestMethod]
	public void Intensity_RampsLinearlyAboveFloorToOne()
	{
		Assert.AreEqual(1.0, HypoarousalVisual.Intensity(1.0), 1e-9);
		double mid = HypoarousalVisual.Floor + ((1.0 - HypoarousalVisual.Floor) / 2.0);
		Assert.AreEqual(0.5, HypoarousalVisual.Intensity(mid), 1e-9);
	}

	[TestMethod]
	public void Intensity_NonFiniteIsZero()
	{
		Assert.AreEqual(0.0, HypoarousalVisual.Intensity(double.NaN), 1e-9);
		Assert.AreEqual(0.0, HypoarousalVisual.Intensity(double.PositiveInfinity), 1e-9);
	}

	[TestMethod]
	public void ShowCollapseArrow_TrueOnlyWhenAboveFloorAndRising()
	{
		Assert.IsTrue(HypoarousalVisual.ShowCollapseArrow(0.7, Rising));
		Assert.IsFalse(HypoarousalVisual.ShowCollapseArrow(0.7, Flat), "deep but steady is not an approach");
		Assert.IsFalse(HypoarousalVisual.ShowCollapseArrow(0.7, Falling), "receding from collapse is not a warning");
		Assert.IsFalse(HypoarousalVisual.ShowCollapseArrow(HypoarousalVisual.Floor, Rising), "below/at floor stays dormant");
	}

	[TestMethod]
	public void SuppressIndexArrow_TrueWhenCollapsePresentAndIndexEasing()
	{
		Assert.IsTrue(HypoarousalVisual.SuppressIndexArrow(0.7, Falling));
		Assert.IsFalse(HypoarousalVisual.SuppressIndexArrow(0.0, Falling));
		Assert.IsFalse(HypoarousalVisual.SuppressIndexArrow(0.7, Rising));
	}
}
