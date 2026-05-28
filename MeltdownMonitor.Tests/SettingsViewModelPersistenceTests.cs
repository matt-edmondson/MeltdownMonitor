using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.Tests;

[TestClass]
public class SettingsViewModelPersistenceTests
{
	[TestMethod]
	public void ChangingEnableChime_PersistsThroughStore()
	{
		var settings = new MobileSettings { EnableChime = true };
		var store = new RecordingStore();
		var vm = new SettingsViewModel(settings, store: store);

		vm.EnableChime = false;

		Assert.AreEqual(1, store.SaveCount);
		Assert.IsFalse(store.LastSavedSettings!.EnableChime);
	}

	[TestMethod]
	public void ChangingDeviceType_PersistsThroughStore()
	{
		var settings = new MobileSettings { DeviceType = PolarDeviceType.Auto };
		var store = new RecordingStore();
		var vm = new SettingsViewModel(settings, store: store);

		vm.DeviceType = PolarDeviceType.H10;

		Assert.AreEqual(1, store.SaveCount);
		Assert.AreEqual(PolarDeviceType.H10, store.LastSavedSettings!.DeviceType);
	}

	[TestMethod]
	public void ChangingThresholds_PersistsThroughStore()
	{
		var settings = new MobileSettings();
		var store = new RecordingStore();
		var vm = new SettingsViewModel(settings, store: store);

		vm.RmssdWarningDropPercent = 25;

		Assert.AreEqual(1, store.SaveCount);
		Assert.AreEqual(0.25, store.LastSavedSettings!.Thresholds.RmssdWarningDropFraction, 1e-6);
	}

	[TestMethod]
	public void ChangingWriteEpisodesToHealthKit_PersistsThroughStore()
	{
		var settings = new MobileSettings { WriteEpisodesToHealthKit = false };
		var store = new RecordingStore();
		var vm = new SettingsViewModel(settings, store: store);

		vm.WriteEpisodesToHealthKit = true;

		Assert.AreEqual(1, store.SaveCount);
		Assert.IsTrue(store.LastSavedSettings!.WriteEpisodesToHealthKit);
	}

	[TestMethod]
	public void PauseAndResume_PersistEachTransition()
	{
		var settings = new MobileSettings();
		var store = new RecordingStore();
		var vm = new SettingsViewModel(settings, store: store);

		vm.PauseOneHourCommand.Execute(null);
		Assert.AreEqual(1, store.SaveCount);
		Assert.IsNotNull(store.LastSavedSettings!.PausedUntil);

		vm.ResumeCommand.Execute(null);
		Assert.AreEqual(2, store.SaveCount);
		Assert.IsNull(store.LastSavedSettings.PausedUntil);
	}

	[TestMethod]
	public void NoOpAssignment_DoesNotPersist()
	{
		var settings = new MobileSettings { EnableChime = true };
		var store = new RecordingStore();
		var vm = new SettingsViewModel(settings, store: store);

		vm.EnableChime = true; // same value

		Assert.AreEqual(0, store.SaveCount);
	}

	private sealed class RecordingStore : IMobileSettingsStore
	{
		public int SaveCount { get; private set; }
		public MobileSettings? LastSavedSettings { get; private set; }

		public bool LoadDisclaimerAccepted() => false;
		public void SaveDisclaimerAccepted(bool accepted) { }
		public MobileSettings LoadSettings() => new();

		public void SaveSettings(MobileSettings settings)
		{
			SaveCount++;
			LastSavedSettings = settings;
		}
	}
}
