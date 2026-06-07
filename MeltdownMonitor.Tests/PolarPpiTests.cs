using MeltdownMonitor.Core.Beats.Polar;

namespace MeltdownMonitor.Tests;

[TestClass]
public class PolarPpiTests
{
	private static PmdPpiSample Ppi(
		int ppi = 800,
		int hr = 75,
		int error = 5,
		bool blocker = false,
		bool contact = true,
		bool contactSupported = true) =>
		new(hr, ppi, error, blocker, contact, contactSupported);

	[TestMethod]
	public void CleanSample_IsNotLowQuality()
	{
		Assert.IsFalse(PolarPpi.IsLowQuality(Ppi()));
	}

	[TestMethod]
	public void BlockerFlag_IsLowQuality()
	{
		Assert.IsTrue(PolarPpi.IsLowQuality(Ppi(blocker: true)));
	}

	[TestMethod]
	public void LostContact_IsLowQuality()
	{
		Assert.IsTrue(PolarPpi.IsLowQuality(Ppi(contact: false)));
	}

	[TestMethod]
	public void ContactFlagIgnoredWhenUnsupported()
	{
		// contact=false but the sensor doesn't report contact → not a quality strike on that basis.
		Assert.IsFalse(PolarPpi.IsLowQuality(Ppi(contact: false, contactSupported: false)));
	}

	[TestMethod]
	public void HighErrorEstimate_IsLowQuality()
	{
		Assert.IsTrue(PolarPpi.IsLowQuality(Ppi(error: 40)));
		Assert.IsFalse(PolarPpi.IsLowQuality(Ppi(error: 40), maxErrorEstimateMs: 50));
	}

	[TestMethod]
	public void ToBeat_CarriesIntervalAndHr()
	{
		var ts = DateTimeOffset.UnixEpoch;
		var beat = PolarPpi.ToBeat(Ppi(ppi: 850, hr: 70), ts, timingArtifact: false);
		Assert.AreEqual(850, beat.RrMs, 0.001);
		Assert.AreEqual(70, beat.HeartRateBpm);
		Assert.IsFalse(beat.IsArtifact);
		Assert.AreEqual(ts, beat.Timestamp);
	}

	[TestMethod]
	public void ToBeat_TimingArtifactMarksBeat()
	{
		var beat = PolarPpi.ToBeat(Ppi(), DateTimeOffset.UnixEpoch, timingArtifact: true);
		Assert.IsTrue(beat.IsArtifact, "A timing-filter rejection marks the beat even when PPI quality is fine.");
	}

	[TestMethod]
	public void ToBeat_QualityArtifactMarksBeat()
	{
		var beat = PolarPpi.ToBeat(Ppi(blocker: true), DateTimeOffset.UnixEpoch, timingArtifact: false);
		Assert.IsTrue(beat.IsArtifact, "A PPI quality strike marks the beat even when timing is fine.");
	}
}
