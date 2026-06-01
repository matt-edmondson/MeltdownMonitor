using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class RelativeTimeAxisTests
{
	[TestMethod]
	public void Ticks_FirstTickIsNowAtZero()
	{
		(double[] positions, string[] labels) = RelativeTimeAxis.Ticks(300.0);

		Assert.AreEqual(0.0, positions[0], 1e-9);
		Assert.AreEqual("now", labels[0]);
	}

	[TestMethod]
	public void Ticks_PositionsDescendByStepAndStopAtWindow()
	{
		// 5-min window -> 2-min step.
		(double[] positions, _) = RelativeTimeAxis.Ticks(300.0);

		CollectionAssert.AreEqual(new[] { 0.0, -120.0, -240.0 }, positions);
	}

	[TestMethod]
	public void Ticks_SubMinuteWindow_LabelsSecondsThenMinutes()
	{
		// 60-s window -> 30-s step.
		(_, string[] labels) = RelativeTimeAxis.Ticks(60.0);

		CollectionAssert.AreEqual(new[] { "now", "-30s", "-1 min" }, labels);
	}

	[TestMethod]
	public void Ticks_IncludesTheWindowEdge()
	{
		(double[] positions, _) = RelativeTimeAxis.Ticks(60.0);

		Assert.AreEqual(-60.0, positions[^1], 1e-9, "the far edge of the window gets a tick");
	}

	[TestMethod]
	public void Ticks_WideWindow_UsesCoarseStep()
	{
		// 60-min window -> 10-min step.
		(double[] positions, _) = RelativeTimeAxis.Ticks(3600.0);

		Assert.AreEqual(-600.0, positions[1], 1e-9);
	}

	[TestMethod]
	public void Ticks_LabelsAndPositionsAreSameLength()
	{
		(double[] positions, string[] labels) = RelativeTimeAxis.Ticks(900.0);

		Assert.AreEqual(positions.Length, labels.Length);
	}
}
