using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class RrTimeAxisTests
{
	[TestMethod]
	public void CumulativeSeconds_NewestBeatIsAtZero()
	{
		double[] x = RrTimeAxis.CumulativeSeconds([800.0, 900.0, 1000.0]);

		Assert.AreEqual(0.0, x[^1], 1e-9, "the most recent beat anchors the axis at 0");
	}

	[TestMethod]
	public void CumulativeSeconds_OlderBeatsAreNegativeAndMonotonic()
	{
		double[] x = RrTimeAxis.CumulativeSeconds([800.0, 900.0, 1000.0]);

		Assert.IsTrue(x[0] < 0.0, "the oldest beat is before 'now'");
		for (int i = 1; i < x.Length; i++)
		{
			Assert.IsTrue(x[i] > x[i - 1], $"x must increase toward 0; x[{i}]={x[i]} x[{i - 1}]={x[i - 1]}");
		}
	}

	[TestMethod]
	public void CumulativeSeconds_SpacingEqualsRrIntervalInSeconds()
	{
		double[] x = RrTimeAxis.CumulativeSeconds([800.0, 900.0, 1000.0]);

		// Beat i sits its own RR interval (ms -> s) after beat i-1.
		Assert.AreEqual(0.900, x[1] - x[0], 1e-9);
		Assert.AreEqual(1.000, x[2] - x[1], 1e-9);
	}

	[TestMethod]
	public void CumulativeSeconds_SingleBeat_IsZero()
	{
		double[] x = RrTimeAxis.CumulativeSeconds([850.0]);

		Assert.AreEqual(1, x.Length);
		Assert.AreEqual(0.0, x[0], 1e-9);
	}

	[TestMethod]
	public void CumulativeSeconds_Empty_ReturnsEmpty()
	{
		double[] x = RrTimeAxis.CumulativeSeconds([]);

		Assert.AreEqual(0, x.Length);
	}
}
