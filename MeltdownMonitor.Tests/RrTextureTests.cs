using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

[TestClass]
public sealed class RrTextureTests
{
	[TestMethod]
	public void BuildRrDeviations_returns_empty_below_min_beats()
		=> Assert.AreEqual(0, RrTexture.BuildRrDeviations([800, 810, 805]).Length);

	[TestMethod]
	public void BuildRrDeviations_normalises_diffs_into_minus1_to_1()
	{
		double[] rr = [800, 800, 800, 800, 800, 800, 800, 860]; // 8 beats; last diff +60 ms
		float[] dev = RrTexture.BuildRrDeviations(rr);
		Assert.AreEqual(rr.Length, dev.Length);
		Assert.AreEqual(0f, dev[1], 1e-4f, "no beat-to-beat change should map to zero deflection");
		Assert.AreEqual(1f, dev[^1], 1e-4f, "+60 ms exceeds the 30 ms full-deflection scale, so it clamps to +1");
	}

	[TestMethod]
	public void BuildRrDeviations_first_slot_carries_no_diff()
	{
		double[] rr = [900, 870, 900, 870, 900, 870, 900, 870];
		float[] dev = RrTexture.BuildRrDeviations(rr);
		Assert.AreEqual(0f, dev[0], 1e-4f, "dev[0] has no predecessor and must stay zero");
		Assert.AreEqual(-1f, dev[1], 1e-4f, "-30 ms maps to exactly -1 at the 30 ms scale");
	}
}
