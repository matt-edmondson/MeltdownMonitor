using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.Tests;

[TestClass]
public class HealthPromptViewModelTests
{
	[TestMethod]
	public void Visible_WhenAvailable_NotRecording_NotDismissed()
	{
		var settings = new MobileSettings();
		var vm = new HealthPromptViewModel(settings, isAvailable: () => true);
		Assert.IsTrue(vm.IsVisible);
	}

	[TestMethod]
	public void Hidden_WhenStoreUnavailable()
	{
		var settings = new MobileSettings();
		var vm = new HealthPromptViewModel(settings, isAvailable: () => false);
		Assert.IsFalse(vm.IsVisible);
	}

	[TestMethod]
	public void Hidden_WhenAlreadyRecording()
	{
		var settings = new MobileSettings { RecordToHealth = true };
		var vm = new HealthPromptViewModel(settings, isAvailable: () => true);
		Assert.IsFalse(vm.IsVisible);
	}

	[TestMethod]
	public void Enable_OnGrant_TurnsOnRecordingAndEpisodes_AndPersists()
	{
		var settings = new MobileSettings();
		int saves = 0;
		var vm = new HealthPromptViewModel(
			settings,
			requestAuthorization: () => Task.FromResult(true),
			isAvailable: () => true,
			onChanged: () => saves++);

		vm.EnableCommand.Execute(null);

		Assert.IsTrue(settings.RecordToHealth);
		Assert.IsTrue(settings.WriteEpisodesToHealthKit);
		Assert.IsTrue(settings.HealthPromptDismissed);
		Assert.IsFalse(vm.IsVisible);
		Assert.AreEqual(1, saves);
	}

	[TestMethod]
	public void Enable_OnDenial_DoesNotRecord_ButStopsNagging()
	{
		var settings = new MobileSettings();
		var vm = new HealthPromptViewModel(
			settings,
			requestAuthorization: () => Task.FromResult(false),
			isAvailable: () => true);

		vm.EnableCommand.Execute(null);

		Assert.IsFalse(settings.RecordToHealth);
		Assert.IsTrue(settings.HealthPromptDismissed);
		Assert.IsFalse(vm.IsVisible);
	}

	[TestMethod]
	public void Dismiss_HidesAndPersists()
	{
		var settings = new MobileSettings();
		int saves = 0;
		var vm = new HealthPromptViewModel(settings, isAvailable: () => true, onChanged: () => saves++);

		vm.DismissCommand.Execute(null);

		Assert.IsTrue(settings.HealthPromptDismissed);
		Assert.IsFalse(settings.RecordToHealth);
		Assert.IsFalse(vm.IsVisible);
		Assert.AreEqual(1, saves);
	}
}
