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

	[TestMethod]
	public void Latest_BeforeAnyUpdate_IsSteady()
	{
		var t = new RegulationVelocityTracker();
		Assert.AreEqual(RegulationDynamics.Steady, t.Latest);
	}

	[TestMethod]
	public void FirstUpdate_Seeds_ReturnsSteadyEvenForLargeIndex()
	{
		var t = new RegulationVelocityTracker();
		var d = t.Update(0.9, T0);
		Assert.AreEqual(RegulationTrend.Steady, d.Trend);
		Assert.AreEqual(0.0, d.Velocity, 1e-12);
		Assert.AreEqual(0.0, d.NormalizedSpeed, 1e-12);
	}

	[TestMethod]
	public void RisingIndex_IsEscalating_WithPositiveVelocity()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.0, T0);                              // seed
		var d = t.Update(0.3, T0.AddSeconds(5));        // raw = 0.06/s; EWMA from 0 -> 0.03/s
		Assert.AreEqual(RegulationTrend.Escalating, d.Trend);
		Assert.AreEqual(0.03, d.Velocity, 1e-9);
		Assert.AreEqual(0.6, d.NormalizedSpeed, 1e-9);  // 0.03 / 0.05 reference
	}

	[TestMethod]
	public void FallingIndex_IsDeEscalating_WithNegativeVelocity()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.5, T0);
		var d = t.Update(0.2, T0.AddSeconds(5));        // raw = -0.06/s; EWMA -> -0.03/s
		Assert.AreEqual(RegulationTrend.DeEscalating, d.Trend);
		Assert.AreEqual(-0.03, d.Velocity, 1e-9);
		Assert.AreEqual(0.6, d.NormalizedSpeed, 1e-9);
	}

	[TestMethod]
	public void SmallChangeWithinDeadband_IsSteady()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.0, T0);
		var d = t.Update(0.02, T0.AddSeconds(5));       // raw = 0.004/s; EWMA -> 0.002/s < 0.01 deadband
		Assert.AreEqual(RegulationTrend.Steady, d.Trend);
		Assert.AreEqual(0.002, d.Velocity, 1e-9);
	}

	[TestMethod]
	public void Velocity_ConvergesAndNormalizedSpeedClampsToOne()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.0, T0);
		double index = 0.0;
		var when = T0;
		RegulationDynamics d = default;
		for (int i = 0; i < 10; i++)
		{
			index += 0.3;                               // constant raw rate 0.06/s
			when = when.AddSeconds(5);
			d = t.Update(index, when);
		}

		Assert.AreEqual(RegulationTrend.Escalating, d.Trend);
		Assert.IsTrue(d.Velocity > 0.05 && d.Velocity <= 0.06 + 1e-9, $"velocity converging to 0.06, was {d.Velocity}");
		Assert.AreEqual(1.0, d.NormalizedSpeed, 1e-9);  // 0.06/0.05 > 1 -> clamped
	}

	[TestMethod]
	public void ShortInterval_IsClampedToMinDt()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.0, T0);
		var d = t.Update(0.3, T0.AddSeconds(0.1));      // dt clamped 0.1 -> 0.5; raw = 0.6/s; EWMA -> 0.3/s
		Assert.AreEqual(0.3, d.Velocity, 1e-9);         // not 1.5 (which an unclamped 0.1 s would give)
	}

	[TestMethod]
	public void LongGap_IsClampedToMaxDt()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.0, T0);
		var d = t.Update(0.3, T0.AddSeconds(600));      // dt clamped 600 -> 30; raw = 0.01/s; EWMA -> 0.005/s
		Assert.AreEqual(0.005, d.Velocity, 1e-9);
		Assert.AreEqual(RegulationTrend.Steady, d.Trend);
	}

	[TestMethod]
	public void Reset_ThenUpdate_ReseedsWithoutSpike()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.0, T0);
		t.Update(0.3, T0.AddSeconds(5));                // escalating
		t.Reset();
		var d = t.Update(0.9, T0.AddSeconds(10));       // big jump, but post-reset -> seed -> Steady/0
		Assert.AreEqual(RegulationTrend.Steady, d.Trend);
		Assert.AreEqual(0.0, d.Velocity, 1e-12);
		Assert.AreEqual(0.0, d.NormalizedSpeed, 1e-12);
	}

	[TestMethod]
	public void NonFiniteIndex_IsIgnored_ReturnsLastDynamics()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.0, T0);
		var prev = t.Update(0.3, T0.AddSeconds(5));
		var d = t.Update(double.NaN, T0.AddSeconds(10));
		Assert.AreEqual(prev, d);
	}

	[TestMethod]
	public void NonFiniteIndex_BeforeSeed_DoesNotSeed_NextCallStillSeeds()
	{
		var t = new RegulationVelocityTracker();
		t.Update(double.NaN, T0);                  // non-finite before any seed — must not count as a seed
		var d = t.Update(0.9, T0.AddSeconds(5));   // this is the real seed -> Steady, no derivative
		Assert.AreEqual(RegulationTrend.Steady, d.Trend);
		Assert.AreEqual(0.0, d.Velocity, 1e-12);
	}

	[TestMethod]
	public void VelocityExactlyAtDeadband_IsSteady()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.0, T0);
		// raw = 0.1/5 = 0.02/s; EWMA from 0 -> 0.01/s == TrendDeadband exactly -> Steady (strict comparison)
		var d = t.Update(0.1, T0.AddSeconds(5));
		Assert.AreEqual(RegulationTrend.Steady, d.Trend);
		Assert.AreEqual(0.01, d.Velocity, 1e-9);
	}
}
