using MeltdownMonitor.Mobile.Controls;

namespace MeltdownMonitor.Tests;

[TestClass]
public sealed class ChartScaleTests
{
	[TestMethod]
	public void FitRange_pads_min_and_max()
	{
		var (min, max) = ChartScale.FitRange([[10.0, 20.0, 30.0]], padFraction: 0.1);
		Assert.AreEqual(8.0, min, 1e-9);
		Assert.AreEqual(32.0, max, 1e-9);
	}

	[TestMethod]
	public void FitRange_flat_series_expands_to_a_visible_band()
	{
		var (min, max) = ChartScale.FitRange([[50.0, 50.0]], padFraction: 0.1);
		Assert.IsTrue(max > min, "a flat series must still produce a non-zero range");
	}

	[TestMethod]
	public void FitRange_ignores_null_and_empty_series()
	{
		var (min, max) = ChartScale.FitRange([null, [], [5.0, 15.0]], padFraction: 0.0);
		Assert.AreEqual(5.0, min, 1e-9);
		Assert.AreEqual(15.0, max, 1e-9);
	}

	[TestMethod]
	public void Y_maps_max_to_top_and_min_to_bottom()
	{
		Assert.AreEqual(0.0, ChartScale.Y(30, 10, 30, height: 100), 1e-9);
		Assert.AreEqual(100.0, ChartScale.Y(10, 10, 30, height: 100), 1e-9);
	}

	[TestMethod]
	public void TimeX_places_now_at_right_edge_and_window_start_at_left()
	{
		Assert.AreEqual(200.0, ChartScale.TimeX(1000, now: 1000, windowSec: 60, width: 200), 1e-9);
		Assert.AreEqual(0.0, ChartScale.TimeX(940, now: 1000, windowSec: 60, width: 200), 1e-9);
	}
}
