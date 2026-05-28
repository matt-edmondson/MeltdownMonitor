using System.Runtime.CompilerServices;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
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

		// 30 minutes of HR samples at 5 Hz — plenty to make the tracker warm
		// (>10 min) and converge the EWMA onto the synthesized HR.
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
	public async Task WarmStart_EmptyHealthKit_LeavesBaselineCold()
	{
		using var repo = NewRepo();
		using var pipeline = new Pipeline(new MobileSettings(), repo, new EmptyBeatSource());

		await pipeline.WarmStartAsync(new FakeHealthStore(Array.Empty<HrSample>()));

		Assert.AreEqual(0, BaselineHr(pipeline), 0.0001);
	}

	private static MeltdownRepository NewRepo() =>
		new(":memory:");

	// Pipeline holds a private BaselineHrvTracker. The exposed seam is the
	// live state — and the warm-start needs to feed into the same tracker
	// that the live run uses. Read it back through the private field with
	// reflection so we keep Pipeline's public surface uncluttered.
	private static double BaselineHr(Pipeline pipeline)
	{
		var field = typeof(Pipeline).GetField(
			"_baseline",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
		var tracker = (Core.Baseline.BaselineHrvTracker)field!.GetValue(pipeline)!;
		return tracker.BaselineHr;
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
