using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

[TestClass]
public class RegulationFieldRampTests
{
	private static readonly DetectionThresholds Thresholds = new();

	private static HrvSample At(double rmssd, double hr) =>
		new(DateTimeOffset.UtcNow, rmssd, 20, hr, 50, 70, DetectorState.Watching);

	[TestMethod]
	public void Index_RisesThroughActivation_ThenFallsOnRecovery()
	{
		// Ramp: baseline → progressively lower RMSSD + higher HR → back to baseline.
		HrvSample[] rampUp =
		[
			At(50, 70),   // at baseline
			At(45, 73),
			At(40, 77),
			At(35, 80.5), // ~Warning threshold
			At(28, 86),
			At(20, 95),   // severe
		];

		double[] indices = rampUp
			.Select(s => RegulationFieldCalculator.Compute(s, Thresholds, 1, true).Index)
			.ToArray();

		// Monotonically increasing — the marker steadily enters the warm lobe.
		for (int i = 1; i < indices.Length; i++)
		{
			Assert.IsTrue(indices[i] > indices[i - 1],
				$"index should rise at step {i}: {indices[i - 1]} -> {indices[i]}");
		}

		// Crosses into clear activation by the Warning-threshold sample.
		Assert.IsTrue(indices[3] >= 0.55, $"expected ~0.6 at Warning, got {indices[3]}");
		Assert.AreEqual(1.0, indices[^1], 0.001);

		// Recovery: returning toward baseline pulls the index back down close to the
		// regulated centre — well below the Warning-threshold position (~0.6), not merely
		// below the saturated peak.
		double recovering = RegulationFieldCalculator.Compute(At(48, 71), Thresholds, 1, true).Index;
		Assert.IsTrue(recovering < indices[3], $"recovery should drop below the Warning position: {recovering}");
		Assert.IsTrue(recovering < 0.15, $"recovery should be near the regulated centre, got {recovering}");
	}
}
