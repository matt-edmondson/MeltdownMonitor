using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.Tests;

[TestClass]
public class DebugViewModelTests
{
	private static BeatDiagnostic D(IntervalSource source, double rr) =>
		new(DateTimeOffset.UnixEpoch, source, rr, 60, false);

	[TestMethod]
	public void BeatDiagnostics_PopulateAbSummariesAndBias()
	{
		var vm = new DebugViewModel();
		vm.OnBeatDiagnostic(D(IntervalSource.HeartRateService, 800));
		vm.OnBeatDiagnostic(D(IntervalSource.PolarEcg, 760));

		StringAssert.Contains(vm.HrsSummary, "800");
		StringAssert.Contains(vm.EcgSummary, "760");
		StringAssert.Contains(vm.AbBiasText, "ms");
		StringAssert.Contains(vm.SourceText, "HRS");
		StringAssert.Contains(vm.SourceText, "ECG");
	}

	[TestMethod]
	public void PpiRow_HiddenUntilPpiArrives()
	{
		var vm = new DebugViewModel();
		Assert.IsFalse(vm.HasPpi);

		vm.OnBeatDiagnostic(D(IntervalSource.PolarPpi, 900));
		Assert.IsTrue(vm.HasPpi);
	}

	[TestMethod]
	public void SampleUpdate_PopulatesHrvDump()
	{
		var vm = new DebugViewModel();
		var extended = new ExtendedHrvMetrics(120, 60, 2.0, 30, 50, 0.6, 48);
		var sample = new HrvSample(DateTimeOffset.UnixEpoch, 42.0, 12.0, 65.0, 40.0, 60.0, DetectorState.Watching)
		{
			Extended = extended,
		};

		vm.OnSampleUpdated(sample);

		StringAssert.Contains(vm.RmssdText, "42");
		StringAssert.Contains(vm.MeanHrText, "65");
		StringAssert.Contains(vm.SdnnText, "48");
		StringAssert.Contains(vm.LfHfText, "2.00");
	}

	[TestMethod]
	public void NoData_ShowsPlaceholders()
	{
		var vm = new DebugViewModel();
		StringAssert.Contains(vm.HrsSummary, "—");
		StringAssert.Contains(vm.RmssdText, "—");
		StringAssert.Contains(vm.AbBiasText, "—");
	}
}
