using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

[TestClass]
public class RegulationVelocityTrackerTests
{
	private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

	[TestMethod]
	public void Steady_Default_IsZeroAndSteady()
	{
		var s = RegulationDynamics.Steady;
		Assert.AreEqual(0.0, s.Velocity, 1e-12);
		Assert.AreEqual(RegulationTrend.Steady, s.Trend);
		Assert.AreEqual(0.0, s.NormalizedSpeed, 1e-12);
	}
}
