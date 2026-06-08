using MeltdownMonitor.Core.Beats;

namespace MeltdownMonitor.Tests;

[TestClass]
public class BeatDiagnosticsAggregatorTests
{
	private static BeatDiagnostic D(IntervalSource source, double rr, int bpm = 60, bool artifact = false) =>
		new(DateTimeOffset.UnixEpoch, source, rr, bpm, artifact);

	[TestMethod]
	public void TracksPerSourceStatsAndHrsVsEcgBias()
	{
		var agg = new BeatDiagnosticsAggregator(window: 10);
		foreach (double rr in new[] { 800.0, 820.0, 810.0 })
		{
			agg.Add(D(IntervalSource.HeartRateService, rr));
		}

		foreach (double rr in new[] { 700.0, 720.0, 710.0 })
		{
			agg.Add(D(IntervalSource.PolarEcg, rr));
		}

		var snap = agg.Snapshot();
		var hrs = snap.Sources.Single(x => x.Source == IntervalSource.HeartRateService);
		var ecg = snap.Sources.Single(x => x.Source == IntervalSource.PolarEcg);

		Assert.AreEqual(3, hrs.Count);
		Assert.AreEqual(810.0, hrs.MeanRrMs, 0.001);
		Assert.AreEqual(710.0, ecg.MeanRrMs, 0.001);
		Assert.AreEqual(810.0, hrs.MedianRrMs, 0.001);
		Assert.IsNotNull(snap.HrsVsEcgRrBiasMs);
		Assert.AreEqual(100.0, snap.HrsVsEcgRrBiasMs!.Value, 0.001);
	}

	[TestMethod]
	public void ArtifactsAreCountedButExcludedFromTheMean()
	{
		var agg = new BeatDiagnosticsAggregator();
		agg.Add(D(IntervalSource.HeartRateService, 800));
		agg.Add(D(IntervalSource.HeartRateService, 5000, artifact: true));

		var hrs = agg.Snapshot().Sources.Single();
		Assert.AreEqual(2, hrs.Count);
		Assert.AreEqual(1, hrs.ArtifactCount);
		Assert.AreEqual(0.5, hrs.ArtifactRate, 1e-9);
		Assert.AreEqual(800.0, hrs.MeanRrMs, 0.001, "The artifact must not pull the mean.");
	}

	[TestMethod]
	public void BiasIsNullUntilBothStreamsHaveCleanData()
	{
		var agg = new BeatDiagnosticsAggregator();
		agg.Add(D(IntervalSource.HeartRateService, 800));
		Assert.IsNull(agg.Snapshot().HrsVsEcgRrBiasMs);

		agg.Add(D(IntervalSource.PolarEcg, 760));
		Assert.IsNotNull(agg.Snapshot().HrsVsEcgRrBiasMs);
	}

	[TestMethod]
	public void RecentWindowIsCappedButCountKeepsGrowing()
	{
		var agg = new BeatDiagnosticsAggregator(window: 5);
		for (int i = 0; i < 20; i++)
		{
			agg.Add(D(IntervalSource.HeartRateService, 800 + i));
		}

		var hrs = agg.Snapshot().Sources.Single();
		Assert.AreEqual(20, hrs.Count);
		Assert.AreEqual(5, hrs.RecentRrMs.Count);
		Assert.AreEqual(819.0, hrs.RecentRrMs[^1], 0.001);
	}

	[TestMethod]
	public void ResetClearsAllStreams()
	{
		var agg = new BeatDiagnosticsAggregator();
		agg.Add(D(IntervalSource.PolarEcg, 700));
		agg.Reset();
		Assert.AreEqual(0, agg.Snapshot().Sources.Count);
	}
}
