using System.Runtime.CompilerServices;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.Tests;

[TestClass]
public class PipelineWarmStartTests
{
	[TestMethod]
	public async Task WarmStart_WithNullStore_IsNoOp()
	{
		using var repo = NewRepo();
		using var pipeline = new Pipeline(new MobileSettings(), repo, new EmptyBeatSource());

		await pipeline.WarmStartAsync(healthStore: null);

		// Tracker is unobservable directly, but a no-op leaves it cold so
		// the live run still picks up the live first sample.
		Assert.IsNull(pipeline.LatestSample);
	}

	[TestMethod]
	public async Task WarmStart_SeedsBaselineHr_FromHealthKitSamples()
	{
		using var repo = NewRepo();
		using var pipeline = new Pipeline(new MobileSettings(), repo, new EmptyBeatSource());

		// 30 minutes of HR samples — warm-start now seeds the HR baseline from the
		// robust median of the HealthKit readings, not a synthesized RR stream.
		var start = DateTimeOffset.UtcNow.AddMinutes(-30);
		var samples = new List<HrSample>();
		for (int i = 0; i < 30 * 60 * 5; i++)
		{
			samples.Add(new HrSample(start.AddMilliseconds(i * 200), HeartRateBpm: 72));
		}

		await pipeline.WarmStartAsync(new FakeHealthStore(samples));

		// Pipeline doesn't expose the tracker, but RunAsync would consume
		// the warm baseline on first beat — observe via a tiny live drip.
		double baselineHr = BaselineHr(pipeline);
		Assert.IsTrue(baselineHr > 60 && baselineHr < 85,
			$"Expected baseline HR near 72, got {baselineHr:F1}.");
	}

	[TestMethod]
	public async Task WarmStart_IsIdempotent_AcrossRepeatedCalls()
	{
		using var repo = NewRepo();
		using var pipeline = new Pipeline(new MobileSettings(), repo, new EmptyBeatSource());

		var start = DateTimeOffset.UtcNow.AddMinutes(-30);
		var samples = new List<HrSample>();
		for (int i = 0; i < 30 * 60 * 5; i++)
		{
			samples.Add(new HrSample(start.AddMilliseconds(i * 200), HeartRateBpm: 72));
		}

		var store = new FakeHealthStore(samples);
		await pipeline.WarmStartAsync(store);
		double afterFirst = BaselineHr(pipeline);

		// Calling again with the same history should converge to the same place,
		// not drift or double-count the contribution.
		await pipeline.WarmStartAsync(store);
		double afterSecond = BaselineHr(pipeline);

		Assert.AreEqual(afterFirst, afterSecond, 0.5,
			"Re-running warm-start with identical history must not move the baseline.");
	}

	[TestMethod]
	public async Task WarmStart_EmptyHealthKit_LeavesBaselineCold()
	{
		using var repo = NewRepo();
		using var pipeline = new Pipeline(new MobileSettings(), repo, new EmptyBeatSource());

		await pipeline.WarmStartAsync(new FakeHealthStore(Array.Empty<HrSample>()));

		Assert.AreEqual(0, BaselineHr(pipeline), 0.0001);
	}

	[TestMethod]
	public async Task WarmStart_SparseHealthKitSamples_StillSeedsBaseline()
	{
		// Real HealthKit HR samples are spaced seconds-to-minutes apart, not 5 Hz. Because warm-start
		// now reduces them to a single HR median (no per-sample RR resampling), sparseness is a non-issue.
		// Regression guard that sparse history still yields an HR baseline.
		using var repo = NewRepo();
		using var pipeline = new Pipeline(new MobileSettings(), repo, new EmptyBeatSource());

		// 30 minutes of samples one every 10 seconds (180 samples) — far sparser than the gap threshold.
		var start = DateTimeOffset.UtcNow.AddMinutes(-30);
		var samples = new List<HrSample>();
		for (int i = 0; i < 180; i++)
		{
			samples.Add(new HrSample(start.AddSeconds(i * 10), HeartRateBpm: 72));
		}

		await pipeline.WarmStartAsync(new FakeHealthStore(samples));

		double baselineHr = BaselineHr(pipeline);
		Assert.IsTrue(baselineHr > 60 && baselineHr < 85,
			$"Sparse HealthKit samples must still seed the baseline; got {baselineHr:F1}.");
	}

	[TestMethod]
	public async Task WarmStart_DoesNotFabricateRmssdBaseline()
	{
		// Audit B core fix: HealthKit HR carries no beat-to-beat detail, so warm-start must never
		// derive an RMSSD baseline from it. RMSSD stays cold until real live beats warm it.
		using var repo = NewRepo();
		using var pipeline = new Pipeline(new MobileSettings(), repo, new EmptyBeatSource());

		var start = DateTimeOffset.UtcNow.AddMinutes(-30);
		var samples = new List<HrSample>();
		for (int i = 0; i < 180; i++)
		{
			samples.Add(new HrSample(start.AddSeconds(i * 10), HeartRateBpm: 72));
		}

		await pipeline.WarmStartAsync(new FakeHealthStore(samples));

		Assert.AreEqual(0, BaselineRmssd(pipeline), 0.0001,
			"Warm-start must not fabricate an RMSSD baseline from HealthKit HR.");
		Assert.IsTrue(BaselineHr(pipeline) > 60 && BaselineHr(pipeline) < 85,
			"HR baseline must still be seeded.");
	}

	[TestMethod]
	public async Task WarmStart_SeedsRealRmssdAnchor_FromOwnPersistedHistory()
	{
		// Option C: mobile reads its OWN persisted HRV history (real RMSSD from prior live sessions)
		// to anchor the baseline, mirroring the desktop head — so a relaunch skips the cold window.
		using var repo = NewRepo();
		InsertRecentCleanSamples(repo, count: 20, rmssd: 50, hr: 70);
		using var pipeline = new Pipeline(new MobileSettings(), repo, new EmptyBeatSource());

		// Null HealthKit store: the history seed must work on its own, independent of HealthKit.
		await pipeline.WarmStartAsync(healthStore: null);

		Assert.AreEqual(50.0, BaselineRmssd(pipeline), 1.0,
			"Mobile must seed a real RMSSD baseline from its own persisted history.");
		Assert.AreEqual(70.0, BaselineHr(pipeline), 1.0);
		Assert.IsFalse(pipeline.IsColdCalibrated,
			"A real-RMSSD history anchor is not a cold calibration.");
	}

	[TestMethod]
	public async Task WarmStart_RealHistoryAnchor_WinsOverHealthKitHr()
	{
		// Order safety: the genuine beat-to-beat history (C) must win over the coarse HealthKit HR
		// estimate (B2). After a warm history seed, the HealthKit HR must not move the baseline.
		using var repo = NewRepo();
		InsertRecentCleanSamples(repo, count: 20, rmssd: 50, hr: 70);
		using var pipeline = new Pipeline(new MobileSettings(), repo, new EmptyBeatSource());

		var start = DateTimeOffset.UtcNow.AddMinutes(-30);
		var samples = new List<HrSample>();
		for (int i = 0; i < 180; i++)
		{
			samples.Add(new HrSample(start.AddSeconds(i * 10), HeartRateBpm: 200));
		}

		await pipeline.WarmStartAsync(new FakeHealthStore(samples));

		Assert.AreEqual(70.0, BaselineHr(pipeline), 1.0,
			"The HealthKit HR seed must not override a baseline already warm from real history.");
	}

	// Persist `count` recent clean (Watching-state) HRV samples so the warm-start history read
	// (Option C) has a genuine RMSSD anchor to recover — the seam the desktop head already uses.
	private static void InsertRecentCleanSamples(MeltdownRepository repo, int count, double rmssd, double hr)
	{
		var start = DateTimeOffset.UtcNow.AddMinutes(-30);
		for (int i = 0; i < count; i++)
		{
			repo.InsertHrvSample(new HrvSample(
				start.AddSeconds(i * 5),
				Rmssd: rmssd,
				Pnn50: 20,
				MeanHr: hr,
				BaselineRmssd: rmssd,
				BaselineHr: hr,
				DetectorState.Watching));
		}
	}

	private static MeltdownRepository NewRepo() =>
		new(":memory:");

	// Pipeline holds a private BaselineHrvTracker. The exposed seam is the
	// live state — and the warm-start needs to feed into the same tracker
	// that the live run uses. Read it back through the private field with
	// reflection so we keep Pipeline's public surface uncluttered.
	private static double BaselineHr(Pipeline pipeline) => Tracker(pipeline).BaselineHr;

	private static double BaselineRmssd(Pipeline pipeline) => Tracker(pipeline).BaselineRmssd;

	private static Core.Baseline.BaselineHrvTracker Tracker(Pipeline pipeline)
	{
		var field = typeof(Pipeline).GetField(
			"_baseline",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
		return (Core.Baseline.BaselineHrvTracker)field!.GetValue(pipeline)!;
	}

	private sealed class FakeHealthStore : IHealthStore
	{
		private readonly IReadOnlyList<HrSample> _samples;
		public FakeHealthStore(IReadOnlyList<HrSample> samples) => _samples = samples;

		public Task<bool> RequestAuthorizationAsync() => Task.FromResult(true);

		public async IAsyncEnumerable<HrSample> ReadRecentHeartRateAsync(TimeSpan lookback)
		{
			foreach (var s in _samples)
			{
				yield return s;
				await Task.Yield();
			}
		}

		public Task WriteHrSampleAsync(HrSample sample) => Task.CompletedTask;
		public Task WriteEpisodeAsync(EpisodeRecord episode) => Task.CompletedTask;
	}

	private sealed class EmptyBeatSource : IBeatSource
	{
#pragma warning disable CS1998 // intentionally empty async stream
		public async IAsyncEnumerable<Beat> GetBeatsAsync(
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			yield break;
		}
#pragma warning restore CS1998
	}
}
