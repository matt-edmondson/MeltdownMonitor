using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.Tests;

[TestClass]
public class EcgViewModelTests
{
	[TestMethod]
	public void Initially_IsIdle()
	{
		var vm = new EcgViewModel();
		Assert.IsTrue(vm.IsIdle);
		Assert.IsFalse(vm.IsStreaming);
		Assert.IsFalse(vm.Overlay.HasBeats);
		StringAssert.Contains(vm.HeartRateText, "—", StringComparison.Ordinal);
	}

	[TestMethod]
	public void OnEcgUpdated_BuildsTheBeatOverlayAndSurfacesQuality()
	{
		var vm = new EcgViewModel();
		// Three peaks one second apart at 130 Hz: two completed beats plus the live one.
		int[] samples = [.. Enumerable.Range(0, 520)];
		vm.OnEcgUpdated(new EcgWaveformSnapshot(samples, [130, 260, 390], 0, 519, 130.0, EcgSignalQuality.Good));

		Assert.IsTrue(vm.IsStreaming);
		Assert.IsFalse(vm.IsIdle);
		Assert.AreEqual(2, vm.Overlay.Beats.Count);
		Assert.IsNotNull(vm.Overlay.Live);
		StringAssert.Contains(vm.QualityText, "good", StringComparison.OrdinalIgnoreCase);
	}

	[TestMethod]
	public void OnEcgUpdated_DerivesHeartRateFromPeakSpacing()
	{
		var vm = new EcgViewModel();
		// Peaks 130 samples apart at 130 Hz ⇒ 1000 ms RR ⇒ 60 bpm.
		var samples = Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? 100 : -100).ToArray();
		vm.OnEcgUpdated(new EcgWaveformSnapshot(samples, [10, 140], 100, 100, 130.0, EcgSignalQuality.Good));

		StringAssert.Contains(vm.HeartRateText, "60 bpm", StringComparison.Ordinal);
	}

	[TestMethod]
	public void OnEcgUpdated_PoorQualityIsSurfaced()
	{
		var vm = new EcgViewModel();
		vm.OnEcgUpdated(new EcgWaveformSnapshot([1, 1, 1], [], 1, 1, 130.0, EcgSignalQuality.Poor));
		StringAssert.Contains(vm.QualityText, "poor", StringComparison.OrdinalIgnoreCase);
	}
}
