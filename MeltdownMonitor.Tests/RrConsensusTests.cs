using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class RrConsensusTests
{
	private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

	private static RrConsensus Seeded(double rr = 800, int n = 6)
	{
		var c = new RrConsensus(toleranceFraction: 0.20);
		for (int i = 0; i < n; i++)
		{
			c.AddReference(rr, T0.AddSeconds(i));
		}

		return c;
	}

	[TestMethod]
	public void NoReference_IsUnknown()
	{
		var c = new RrConsensus();
		Assert.AreEqual(ConsensusVerdict.Unknown, c.Check(800, T0));
	}

	[TestMethod]
	public void AgreeingBeat_IsConfirmed()
	{
		var c = Seeded(800);
		Assert.AreEqual(ConsensusVerdict.Confirmed, c.Check(810, T0.AddSeconds(6)));
	}

	[TestMethod]
	public void DoubledBeat_Conflicts()
	{
		var c = Seeded(800);
		// A missed ECG beat reads as ~2x the reference rhythm.
		Assert.AreEqual(ConsensusVerdict.Conflicted, c.Check(1600, T0.AddSeconds(6)));
	}

	[TestMethod]
	public void HalvedBeat_Conflicts()
	{
		var c = Seeded(800);
		// A T-wave read as a beat reads as ~half the reference rhythm.
		Assert.AreEqual(ConsensusVerdict.Conflicted, c.Check(400, T0.AddSeconds(6)));
	}

	[TestMethod]
	public void StaleReference_IsUnknown()
	{
		var c = Seeded(800);
		// Last reference was at T0+5 s; a check 30 s later has no fresh witness.
		Assert.AreEqual(ConsensusVerdict.Unknown, c.Check(1600, T0.AddSeconds(35)));
	}

	[TestMethod]
	public void ConflictRate_TracksRecentChecks()
	{
		var c = Seeded(800);
		var t = T0.AddSeconds(6);
		c.Check(800, t);   // confirmed
		c.Check(1600, t);  // conflicted
		c.Check(810, t);   // confirmed
		c.Check(400, t);   // conflicted
		Assert.AreEqual(0.5, c.ConflictRate, 1e-9);
	}

	[TestMethod]
	public void Reset_ClearsReference()
	{
		var c = Seeded(800);
		c.Reset();
		Assert.AreEqual(ConsensusVerdict.Unknown, c.Check(1600, T0.AddSeconds(6)));
		Assert.IsNull(c.ReferenceMedianRrMs);
	}
}
