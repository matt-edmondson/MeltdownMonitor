using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class HrvSampleTests
{
	private static HrvSample Make() => new(
		Timestamp: DateTimeOffset.UnixEpoch,
		Rmssd: 40, Pnn50: 10, MeanHr: 70,
		BaselineRmssd: 45, BaselineHr: 68,
		State: DetectorState.Watching);

	[TestMethod]
	public void SensorContact_DefaultsToNotSupported()
	{
		Assert.AreEqual(SensorContactStatus.NotSupported, Make().SensorContact);
	}

	[TestMethod]
	public void SensorContact_RoundTripsViaInit()
	{
		var s = Make() with { SensorContact = SensorContactStatus.NotDetected };
		Assert.AreEqual(SensorContactStatus.NotDetected, s.SensorContact);
	}
}
