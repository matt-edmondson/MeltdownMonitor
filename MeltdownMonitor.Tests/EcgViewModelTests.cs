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
		StringAssert.Contains(vm.HeartRateText, "—", StringComparison.Ordinal);
	}

	[TestMethod]
	public void OnEcgUpdated_PopulatesTracePeaksAndQuality()
	{
		var vm = new EcgViewModel();
		var snapshot = new EcgWaveformSnapshot(
			MicroVolts: [10, 20, 30, 40],
			RPeakIndices: [1, 3],
			MinMicroVolts: 10,
			MaxMicroVolts: 40,
			SampleRateHz: 130.0,
			Quality: EcgSignalQuality.Good);

		vm.OnEcgUpdated(snapshot);

		Assert.IsTrue(vm.IsStreaming);
		Assert.IsFalse(vm.IsIdle);
		Assert.AreEqual(4, vm.Samples.Count);
		CollectionAssert.AreEqual(new[] { 1, 3 }, vm.RPeakIndices.ToArray());
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
