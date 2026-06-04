using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

[TestClass]
public sealed class RegulationFieldGeometryTests
{
	[TestMethod]
	public void VagalToneOffsetY_baseline_sits_on_the_crossover()
		=> Assert.AreEqual(0f, RegulationFieldGeometry.VagalToneOffsetY(0.5, markerYClamp: 100f), 1e-4f);

	[TestMethod]
	public void VagalToneOffsetY_fragile_lifts_to_top()
		=> Assert.AreEqual(-100f, RegulationFieldGeometry.VagalToneOffsetY(0.0, 100f), 1e-4f);

	[TestMethod]
	public void VagalToneOffsetY_steady_drops_to_bottom()
		=> Assert.AreEqual(100f, RegulationFieldGeometry.VagalToneOffsetY(1.0, 100f), 1e-4f);

	[TestMethod]
	public void VagalToneOffsetY_clamps_out_of_range_tone()
		=> Assert.AreEqual(100f, RegulationFieldGeometry.VagalToneOffsetY(2.0, 100f), 1e-4f);
}
