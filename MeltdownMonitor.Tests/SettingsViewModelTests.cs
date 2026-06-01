using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.Tests;

[TestClass]
public class SettingsViewModelTests
{
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
}
