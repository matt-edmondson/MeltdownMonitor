using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.Tests;

[TestClass]
public class SettingsViewModelTests
{
	[TestMethod]
	public void EnableMotionCorroboration_RoundTripsAndPersistsOnlyOnChange()
	{
		var settings = new MobileSettings { EnableMotionCorroboration = false };
		int saves = 0;
		var vm = new SettingsViewModel(settings, onChanged: () => saves++);

		vm.EnableMotionCorroboration = false; // unchanged → no persist
		Assert.AreEqual(0, saves);

		vm.EnableMotionCorroboration = true; // changed → persists onto settings
		Assert.IsTrue(settings.EnableMotionCorroboration);
		Assert.AreEqual(1, saves);
	}

	[TestMethod]
	public void ClearData_RequiresConfirmationBeforeWiping()
	{
		int cleared = 0;
		var vm = new SettingsViewModel(new MobileSettings(), clearData: () =>
		{
			cleared++;
			return Task.CompletedTask;
		});

		// Arming shows the confirm panel but does not wipe.
		vm.ClearDataCommand.Execute(null);
		Assert.IsTrue(vm.IsClearDataConfirmPending);
		Assert.AreEqual(0, cleared);

		// Cancelling dismisses without wiping.
		vm.CancelClearDataCommand.Execute(null);
		Assert.IsFalse(vm.IsClearDataConfirmPending);
		Assert.AreEqual(0, cleared);

		// Confirming wipes and reports.
		vm.ClearDataCommand.Execute(null);
		vm.ConfirmClearDataCommand.Execute(null);
		Assert.IsFalse(vm.IsClearDataConfirmPending);
		Assert.AreEqual(1, cleared);
		Assert.IsTrue(vm.HasClearDataStatus);
	}

	[TestMethod]
	public void ClearDataCommand_DisabledWithoutDelegate()
	{
		var vm = new SettingsViewModel(new MobileSettings());
		Assert.IsFalse(vm.ClearDataCommand.CanExecute(null), "No clear delegate ⇒ command disabled.");
	}

	[TestMethod]
	public void PreferredIntervalSource_RoundTripsAndPersistsOnlyOnChange()
	{
		var settings = new MobileSettings { PreferredIntervalSource = IntervalSource.HeartRateService };
		int saves = 0;
		var vm = new SettingsViewModel(settings, onChanged: () => saves++);

		vm.PreferredIntervalSource = IntervalSource.HeartRateService; // unchanged → no persist
		Assert.AreEqual(0, saves);

		vm.PreferredIntervalSource = IntervalSource.PolarPpi; // changed → persists
		Assert.AreEqual(IntervalSource.PolarPpi, settings.PreferredIntervalSource);
		Assert.AreEqual(1, saves);
	}

	[TestMethod]
	public void RegulationTrailLength_RoundTripsOntoSettings()
	{
		var settings = new MobileSettings();
		var vm = new SettingsViewModel(settings);

		vm.RegulationTrailLength = 120;

		Assert.AreEqual(120, settings.RegulationTrailLength);
		Assert.AreEqual(120, vm.RegulationTrailLength);
	}

	[TestMethod]
	public void RegulationTrailLength_ClampsToRange()
	{
		var settings = new MobileSettings();
		var vm = new SettingsViewModel(settings);

		vm.RegulationTrailLength = 5;
		Assert.AreEqual(12, settings.RegulationTrailLength, "below floor clamps to 12");

		vm.RegulationTrailLength = 9999;
		Assert.AreEqual(2160, settings.RegulationTrailLength, "above ceiling clamps to 2160");
	}

	[TestMethod]
	public void RegulationTrailLength_PersistsOnlyOnChange()
	{
		var settings = new MobileSettings { RegulationTrailLength = 48 };
		int saves = 0;
		var vm = new SettingsViewModel(settings, onChanged: () => saves++);

		vm.RegulationTrailLength = 48; // unchanged → no persist
		Assert.AreEqual(0, saves);

		vm.RegulationTrailLength = 60; // changed → one persist
		Assert.AreEqual(1, saves);
	}

	[TestMethod]
	public void JitterExaggeration_RoundTripsOntoSettings()
	{
		var settings = new MobileSettings();
		var vm = new SettingsViewModel(settings);

		vm.JitterExaggeration = 2.0;

		Assert.AreEqual(2.0, settings.JitterExaggeration, 1e-9);
		Assert.AreEqual(2.0, vm.JitterExaggeration, 1e-9);
	}

	[TestMethod]
	public void JitterExaggeration_ClampsToRange()
	{
		var settings = new MobileSettings();
		var vm = new SettingsViewModel(settings);

		vm.JitterExaggeration = -1.0;
		Assert.AreEqual(0.0, settings.JitterExaggeration, 1e-9, "below floor clamps to 0");

		vm.JitterExaggeration = 99.0;
		Assert.AreEqual(3.0, settings.JitterExaggeration, 1e-9, "above ceiling clamps to 3");
	}

	[TestMethod]
	public void JitterExaggeration_PersistsOnlyOnChange()
	{
		var settings = new MobileSettings { JitterExaggeration = 1.0 };
		int saves = 0;
		var vm = new SettingsViewModel(settings, onChanged: () => saves++);

		vm.JitterExaggeration = 1.0; // unchanged → no persist
		Assert.AreEqual(0, saves);

		vm.JitterExaggeration = 1.5; // changed → one persist
		Assert.AreEqual(1, saves);
	}

	[TestMethod]
	public void LobeThickness_RoundTripsOntoSettings()
	{
		var settings = new MobileSettings();
		var vm = new SettingsViewModel(settings);

		vm.LobeThickness = 2.0;

		Assert.AreEqual(2.0, settings.LobeThickness, 1e-9);
		Assert.AreEqual(2.0, vm.LobeThickness, 1e-9);
	}

	[TestMethod]
	public void LobeThickness_ClampsToRange()
	{
		var settings = new MobileSettings();
		var vm = new SettingsViewModel(settings);

		vm.LobeThickness = 0.0;
		Assert.AreEqual(0.5, settings.LobeThickness, 1e-9, "below floor clamps to 0.5");

		vm.LobeThickness = 99.0;
		Assert.AreEqual(3.0, settings.LobeThickness, 1e-9, "above ceiling clamps to 3");
	}

	[TestMethod]
	public void LobeThickness_PersistsOnlyOnChange()
	{
		var settings = new MobileSettings { LobeThickness = 1.0 };
		int saves = 0;
		var vm = new SettingsViewModel(settings, onChanged: () => saves++);

		vm.LobeThickness = 1.0; // unchanged → no persist
		Assert.AreEqual(0, saves);

		vm.LobeThickness = 1.5; // changed → one persist
		Assert.AreEqual(1, saves);
	}

	[TestMethod]
	public void LobeSegments_RoundTripsOntoSettings()
	{
		var settings = new MobileSettings();
		var vm = new SettingsViewModel(settings);

		vm.LobeSegments = 128;

		Assert.AreEqual(128, settings.LobeSegments);
		Assert.AreEqual(128, vm.LobeSegments);
	}

	[TestMethod]
	public void LobeSegments_ClampsToRange()
	{
		var settings = new MobileSettings();
		var vm = new SettingsViewModel(settings);

		vm.LobeSegments = 1;
		Assert.AreEqual(24, settings.LobeSegments, "below floor clamps to 24");

		vm.LobeSegments = 9999;
		Assert.AreEqual(256, settings.LobeSegments, "above ceiling clamps to 256");
	}

	[TestMethod]
	public void LobeSegments_PersistsOnlyOnChange()
	{
		var settings = new MobileSettings { LobeSegments = 96 };
		int saves = 0;
		var vm = new SettingsViewModel(settings, onChanged: () => saves++);

		vm.LobeSegments = 96; // unchanged → no persist
		Assert.AreEqual(0, saves);

		vm.LobeSegments = 120; // changed → one persist
		Assert.AreEqual(1, saves);
	}

	[TestMethod]
	public void RecoveryArrowSpeed_RoundTripsOntoSettings()
	{
		var settings = new MobileSettings();
		var vm = new SettingsViewModel(settings);

		vm.RecoveryArrowSpeed = 1.5;

		Assert.AreEqual(1.5, settings.RecoveryArrowSpeed, 1e-9);
		Assert.AreEqual(1.5, vm.RecoveryArrowSpeed, 1e-9);
	}

	[TestMethod]
	public void RecoveryArrowSpeed_ClampsToRange()
	{
		var settings = new MobileSettings();
		var vm = new SettingsViewModel(settings);

		vm.RecoveryArrowSpeed = 0.0;
		Assert.AreEqual(0.1, settings.RecoveryArrowSpeed, 1e-9, "below floor clamps to 0.1");

		vm.RecoveryArrowSpeed = 99.0;
		Assert.AreEqual(3.0, settings.RecoveryArrowSpeed, 1e-9, "above ceiling clamps to 3");
	}

	[TestMethod]
	public void RecoveryArrowCount_RoundTripsOntoSettings()
	{
		var settings = new MobileSettings();
		var vm = new SettingsViewModel(settings);

		vm.RecoveryArrowCount = 5;

		Assert.AreEqual(5, settings.RecoveryArrowCount);
		Assert.AreEqual(5, vm.RecoveryArrowCount);
	}

	[TestMethod]
	public void RecoveryArrowCount_ClampsToRange()
	{
		var settings = new MobileSettings();
		var vm = new SettingsViewModel(settings);

		vm.RecoveryArrowCount = 0;
		Assert.AreEqual(1, settings.RecoveryArrowCount, "below floor clamps to 1");

		vm.RecoveryArrowCount = 99;
		Assert.AreEqual(6, settings.RecoveryArrowCount, "above ceiling clamps to 6");
	}

	[TestMethod]
	public void RecoveryArrowCount_PersistsOnlyOnChange()
	{
		var settings = new MobileSettings { RecoveryArrowCount = 3 };
		int saves = 0;
		var vm = new SettingsViewModel(settings, onChanged: () => saves++);

		vm.RecoveryArrowCount = 3; // unchanged → no persist
		Assert.AreEqual(0, saves);

		vm.RecoveryArrowCount = 4; // changed → one persist
		Assert.AreEqual(1, saves);
	}
}
