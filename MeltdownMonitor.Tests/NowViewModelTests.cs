using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.Tests;

[TestClass]
public class NowViewModelTests
{
	// On a plain test thread Avalonia's Dispatcher.UIThread.CheckAccess()
	// returns true, so NowViewModel applies updates synchronously — no UI
	// pump needed to observe the results.

	[TestMethod]
	public void OnSampleUpdated_UpdatesReadoutsStateAndConnection()
	{
		var vm = new NowViewModel();
		Assert.AreEqual(ConnectionState.Disconnected, vm.Connection);

		vm.OnSampleUpdated(Sample(rmssd: 38, meanHr: 81, baseline: 55, state: DetectorState.Warning));

		Assert.AreEqual(81, vm.HeartRate, 0.001);
		Assert.AreEqual(38, vm.Rmssd, 0.001);
		Assert.AreEqual(55, vm.BaselineRmssd, 0.001);
		Assert.AreEqual(DetectorState.Warning, vm.State);
		Assert.AreEqual(ConnectionState.Connected, vm.Connection, "A flowing sample means the link is live.");
		Assert.AreEqual(1, vm.RmssdHistory.Count);
		Assert.AreEqual(1, vm.BaselineHistory.Count);
	}

	[TestMethod]
	public void OnStateChanged_UpdatesStatePillWithoutASample()
	{
		var vm = new NowViewModel();

		vm.OnStateChanged(DetectorState.Alerting);

		Assert.AreEqual(DetectorState.Alerting, vm.State);
		Assert.AreEqual(0, vm.RmssdHistory.Count, "A bare state change should not push a chart point.");
	}

	[TestMethod]
	public void StateLabel_ReflectsPauseOverride()
	{
		var vm = new NowViewModel();
		vm.OnStateChanged(DetectorState.Watching);

		vm.IsPaused = true;

		StringAssert.Contains(vm.StateLabel, "Paused", StringComparison.OrdinalIgnoreCase);
	}

	private static HrvSample Sample(double rmssd, double meanHr, double baseline, DetectorState state) =>
		new(
			DateTimeOffset.UtcNow,
			rmssd,
			Pnn50: 20,
			meanHr,
			BaselineRmssd: baseline,
			BaselineHr: 65,
			state);
}
