using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.Tests;

[TestClass]
public class WatchCorroborationMonitorTests
{
	private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static WatchMetricSample Watch(double hr, DateTimeOffset ts,
		SensorContactStatus contact = SensorContactStatus.Detected) =>
		new(ts, hr, HrvSdnnMs: null, contact);

	[TestMethod]
	public void NoWatchReading_IsUnknown()
	{
		var monitor = new WatchCorroborationMonitor();

		Assert.AreEqual(WatchCorroboration.Unknown, monitor.Evaluate(strapHeartRateBpm: 70, T0));
	}

	[TestMethod]
	public void AgreeingHeartRate_IsConfirmed()
	{
		var monitor = new WatchCorroborationMonitor();
		monitor.Add(Watch(hr: 72, T0));

		// 70 vs 72 is well within the default 12 bpm tolerance.
		Assert.AreEqual(WatchCorroboration.Confirmed, monitor.Evaluate(strapHeartRateBpm: 70, T0));
	}

	[TestMethod]
	public void DisagreeingHeartRate_IsConflicted()
	{
		var monitor = new WatchCorroborationMonitor();
		monitor.Add(Watch(hr: 60, T0));

		// 90 (strap, spuriously elevated by artifact) vs 60 (calm wrist) — a 30 bpm gap.
		Assert.AreEqual(WatchCorroboration.Conflicted, monitor.Evaluate(strapHeartRateBpm: 90, T0));
	}

	[TestMethod]
	public void GapAtToleranceBoundary_IsConflicted()
	{
		var monitor = new WatchCorroborationMonitor { ConflictToleranceBpm = 12.0 };
		monitor.Add(Watch(hr: 70, T0));

		// Exactly at the tolerance counts as a conflict (>=).
		Assert.AreEqual(WatchCorroboration.Conflicted, monitor.Evaluate(strapHeartRateBpm: 82, T0));
		// Just inside is confirmed.
		Assert.AreEqual(WatchCorroboration.Confirmed, monitor.Evaluate(strapHeartRateBpm: 81.9, T0));
	}

	[TestMethod]
	public void StaleWatchReading_IsUnknown()
	{
		var monitor = new WatchCorroborationMonitor { Staleness = TimeSpan.FromSeconds(30) };
		monitor.Add(Watch(hr: 60, T0));

		// Strap sample arrives 31 s after the last watch reading — too old to corroborate.
		Assert.AreEqual(WatchCorroboration.Unknown,
			monitor.Evaluate(strapHeartRateBpm: 90, T0.AddSeconds(31)));
	}

	[TestMethod]
	public void WatchReadingFromTheFuture_BeyondStaleness_IsUnknown()
	{
		var monitor = new WatchCorroborationMonitor { Staleness = TimeSpan.FromSeconds(30) };
		monitor.Add(Watch(hr: 60, T0.AddSeconds(31)));

		// A watch reading dated well after the strap sample is equally untrustworthy for this sample.
		Assert.AreEqual(WatchCorroboration.Unknown, monitor.Evaluate(strapHeartRateBpm: 90, T0));
	}

	[TestMethod]
	public void WatchOffWrist_IsUnknown()
	{
		var monitor = new WatchCorroborationMonitor();
		monitor.Add(Watch(hr: 60, T0, contact: SensorContactStatus.NotDetected));

		Assert.AreEqual(WatchCorroboration.Unknown, monitor.Evaluate(strapHeartRateBpm: 90, T0));
	}

	[TestMethod]
	public void NonPositiveHeartRates_AreUnknown()
	{
		var monitor = new WatchCorroborationMonitor();
		monitor.Add(Watch(hr: 0, T0));
		Assert.AreEqual(WatchCorroboration.Unknown, monitor.Evaluate(strapHeartRateBpm: 70, T0));

		monitor.Add(Watch(hr: 70, T0));
		Assert.AreEqual(WatchCorroboration.Unknown, monitor.Evaluate(strapHeartRateBpm: 0, T0));
	}

	[TestMethod]
	public void Add_KeepsLatest_IgnoresOutOfOrder()
	{
		var monitor = new WatchCorroborationMonitor();
		monitor.Add(Watch(hr: 70, T0.AddSeconds(10)));
		// An older reading arriving late must not replace the newer value.
		monitor.Add(Watch(hr: 120, T0));

		Assert.AreEqual(WatchCorroboration.Confirmed,
			monitor.Evaluate(strapHeartRateBpm: 72, T0.AddSeconds(10)));
	}

	[TestMethod]
	public void Snapshot_TracksLatestVerdict()
	{
		var monitor = new WatchCorroborationMonitor();
		monitor.Add(Watch(hr: 60, T0));
		monitor.Evaluate(strapHeartRateBpm: 90, T0);

		WatchCorroborationSnapshot snap = monitor.Snapshot;
		Assert.AreEqual(WatchCorroboration.Conflicted, snap.Verdict);
		Assert.AreEqual(60, snap.WatchHeartRateBpm);
		Assert.AreEqual(90, snap.StrapHeartRateBpm);
	}

	[TestMethod]
	public void Reset_ClearsLatestReading()
	{
		var monitor = new WatchCorroborationMonitor();
		monitor.Add(Watch(hr: 60, T0));
		monitor.Reset();

		Assert.AreEqual(WatchCorroboration.Unknown, monitor.Evaluate(strapHeartRateBpm: 90, T0));
		Assert.AreEqual(WatchCorroboration.Unknown, monitor.Verdict);
	}
}
