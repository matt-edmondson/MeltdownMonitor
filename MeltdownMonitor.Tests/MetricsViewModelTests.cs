using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.Tests;

[TestClass]
public sealed class MetricsViewModelTests
{
	private static HrvSample SampleAt(DateTimeOffset t, double rmssd, double hr) =>
		new(t, rmssd, Pnn50: 10, hr, BaselineRmssd: 50, BaselineHr: 70, DetectorState.Idle)
		{
			BaselineLfHfRatio = 1.5,
			SensorContact = SensorContactStatus.Detected,
		};

	[TestMethod]
	public void OnSampleUpdated_appends_rmssd_and_baseline_with_timestamps()
	{
		var vm = new MetricsViewModel();
		var t0 = DateTimeOffset.UnixEpoch.AddSeconds(1000);
		vm.OnSampleUpdated(SampleAt(t0, rmssd: 40, hr: 80));
		vm.OnSampleUpdated(SampleAt(t0.AddSeconds(5), rmssd: 42, hr: 82));

		CollectionAssert.AreEqual(new[] { 40.0, 42.0 }, vm.Rmssd.ToList());
		CollectionAssert.AreEqual(new[] { 50.0, 50.0 }, vm.BaselineRmssd.ToList());
		CollectionAssert.AreEqual(new[] { 80.0, 82.0 }, vm.MeanHr.ToList());
		Assert.AreEqual(2, vm.RmssdTimestamps.Count);
	}

	[TestMethod]
	public void OnBeatReceived_collects_non_artifact_rr_only()
	{
		var vm = new MetricsViewModel();
		vm.OnBeatReceived(new Beat(DateTimeOffset.UnixEpoch, 820, 73, IsArtifact: false));
		vm.OnBeatReceived(new Beat(DateTimeOffset.UnixEpoch, 9999, 73, IsArtifact: true));
		vm.OnBeatReceived(new Beat(DateTimeOffset.UnixEpoch, 810, 74, IsArtifact: false));
		CollectionAssert.AreEqual(new[] { 820.0, 810.0 }, vm.RecentRr.ToList());
	}

	[TestMethod]
	public void Capacity_trims_oldest_when_window_exceeded()
	{
		// 1-minute window at 5 s cadence floors to the 60-sample minimum capacity.
		var vm = new MetricsViewModel(windowMinutesProvider: () => 1, emitIntervalProvider: () => 5.0);
		var t0 = DateTimeOffset.UnixEpoch.AddSeconds(1000);
		for (int i = 0; i < 80; i++)
		{
			vm.OnSampleUpdated(SampleAt(t0.AddSeconds(i * 5), rmssd: i, hr: 70));
		}

		Assert.AreEqual(60, vm.Rmssd.Count, "should retain exactly the 60-sample capacity floor");
		Assert.AreEqual(79.0, vm.Rmssd[^1], 1e-9, "newest sample must be kept");
		Assert.AreEqual(20.0, vm.Rmssd[0], 1e-9, "oldest retained should be sample index 20 (80 - 60)");
	}

	[TestMethod]
	public void Backfill_seeds_series_from_persisted_samples_oldest_first()
	{
		var vm = new MetricsViewModel();
		var t0 = DateTimeOffset.UnixEpoch.AddSeconds(1000);
		var history = new List<HrvSample>
		{
			SampleAt(t0, rmssd: 30, hr: 70),
			SampleAt(t0.AddSeconds(5), rmssd: 33, hr: 72),
		};

		vm.Backfill(history, batteries: []);

		CollectionAssert.AreEqual(new[] { 30.0, 33.0 }, vm.Rmssd.ToList());
		Assert.AreEqual(2, vm.RmssdTimestamps.Count);
	}
}
