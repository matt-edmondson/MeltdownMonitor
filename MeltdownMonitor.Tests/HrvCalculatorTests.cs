using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class HrvCalculatorTests
{
	// Hand-computed: diffs = [10, -10, 10], sumSq = 300, RMSSD = sqrt(300/3) = 10
	[TestMethod]
	public void ComputeRmssd_KnownSequence()
	{
		double[] rrs = [800, 810, 800, 810];
		double rmssd = ShortWindowHrvCalculator.ComputeRmssd(rrs);
		Assert.AreEqual(10.0, rmssd, 0.01);
	}

	[TestMethod]
	public void ComputeRmssd_IdenticalBeats_ReturnsZero()
	{
		double[] rrs = [800, 800, 800, 800];
		Assert.AreEqual(0.0, ShortWindowHrvCalculator.ComputeRmssd(rrs), 0.001);
	}

	[TestMethod]
	public void ComputeRmssd_TwoBeats_MatchesDiff()
	{
		// Single diff of 50ms → RMSSD = 50
		double[] rrs = [800, 850];
		Assert.AreEqual(50.0, ShortWindowHrvCalculator.ComputeRmssd(rrs), 0.001);
	}

	[TestMethod]
	public void ComputeRmssd_SingleBeat_ReturnsZero()
	{
		double[] rrs = [800];
		Assert.AreEqual(0.0, ShortWindowHrvCalculator.ComputeRmssd(rrs));
	}

	// All successive diffs > 50ms → pNN50 = 100%
	[TestMethod]
	public void ComputePnn50_AllDiffsOver50_Returns100()
	{
		double[] rrs = [800, 860, 800, 860];
		Assert.AreEqual(100.0, ShortWindowHrvCalculator.ComputePnn50(rrs), 0.001);
	}

	// No successive diffs > 50ms → pNN50 = 0%
	[TestMethod]
	public void ComputePnn50_NoDiffsOver50_ReturnsZero()
	{
		double[] rrs = [800, 820, 810, 815];
		Assert.AreEqual(0.0, ShortWindowHrvCalculator.ComputePnn50(rrs), 0.001);
	}

	[TestMethod]
	public void ComputePnn50_HalfDifsOver50_Returns50()
	{
		// diffs: 60 (>50), 30 (<50) → 1/2 = 50%
		double[] rrs = [800, 860, 890];
		Assert.AreEqual(50.0, ShortWindowHrvCalculator.ComputePnn50(rrs), 0.001);
	}

	[TestMethod]
	public void AddBeat_ArtifactBeat_NotAddedToWindow()
	{
		var calc = new ShortWindowHrvCalculator();
		var now = DateTimeOffset.UtcNow;
		var artifactBeat = new MeltdownMonitor.Core.Beats.Beat(now, 400, 70, IsArtifact: true);
		var result = calc.AddBeat(artifactBeat, 0, 0, MeltdownMonitor.Core.Detection.DetectorState.Idle);
		Assert.IsNull(result);
	}

	[TestMethod]
	public void AddBeat_EmitsAfterIntervalWithEnoughBeats()
	{
		var calc = new ShortWindowHrvCalculator();
		var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
		HrvSample? emitted = null;

		// Feed 5 seconds worth of beats (≈7 beats at 800ms) so the emit threshold passes
		for (int i = 0; i < 10; i++)
		{
			var ts = start.AddMilliseconds(i * 800);
			var beat = new MeltdownMonitor.Core.Beats.Beat(ts, 800, 75, IsArtifact: false);
			var sample = calc.AddBeat(beat, 50, 75, MeltdownMonitor.Core.Detection.DetectorState.Watching);
			if (sample is not null)
			{
				emitted = sample;
			}
		}

		Assert.IsNotNull(emitted, "Expected at least one sample to be emitted");
		Assert.IsTrue(emitted.Rmssd >= 0);
		Assert.IsTrue(emitted.MeanHr > 0);
	}
}
