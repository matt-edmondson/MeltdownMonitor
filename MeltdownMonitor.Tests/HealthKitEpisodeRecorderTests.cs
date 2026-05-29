using System.Reflection;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.Tests;

[TestClass]
public class HealthKitEpisodeRecorderTests
{
	[TestMethod]
	public void Alert_WritesEpisode_WhenOptedIn()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var store = new RecordingHealthStore();
			using var _ = new HealthKitEpisodeRecorder(
				pipeline,
				new MobileSettings { WriteEpisodesToHealthKit = true },
				store);

			RaiseAlert(pipeline, "Sustained: Warning conditions held");

			Assert.AreEqual(1, store.Episodes.Count);
			var episode = store.Episodes[0];
			Assert.IsTrue(episode.End > episode.Start, "Episode window must be non-empty.");
			StringAssert.Contains(episode.Notes ?? "", "Sustained", StringComparison.Ordinal);
		}
	}

	[TestMethod]
	public void Alert_WritesNothing_WhenOptedOut()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var store = new RecordingHealthStore();
			using var _ = new HealthKitEpisodeRecorder(
				pipeline,
				new MobileSettings { WriteEpisodesToHealthKit = false },
				store);

			RaiseAlert(pipeline, "anything");

			Assert.AreEqual(0, store.Episodes.Count);
		}
	}

	private static (Pipeline, MeltdownRepository) NewPipeline()
	{
		var repo = new MeltdownRepository(":memory:");
		var pipeline = new Pipeline(new MobileSettings(), repo, new EmptyBeatSource());
		return (pipeline, repo);
	}

	private static void RaiseAlert(Pipeline pipeline, string reason)
	{
		var payload = new AlertPayload(DateTimeOffset.UtcNow, reason, 20, 50);
		var field = typeof(Pipeline).GetField("AlertFired", BindingFlags.Instance | BindingFlags.NonPublic);
		if (field?.GetValue(pipeline) is Action<AlertPayload> handler)
		{
			handler(payload);
		}
	}

	private sealed class RecordingHealthStore : IHealthStore
	{
		public List<EpisodeRecord> Episodes { get; } = [];

		public Task<bool> RequestAuthorizationAsync() => Task.FromResult(true);

		public IAsyncEnumerable<HrSample> ReadRecentHeartRateAsync(TimeSpan lookback) =>
			Empty();

		public Task WriteHrSampleAsync(HrSample sample) => Task.CompletedTask;

		public Task WriteEpisodeAsync(EpisodeRecord episode)
		{
			Episodes.Add(episode);
			return Task.CompletedTask;
		}

#pragma warning disable CS1998
		private static async IAsyncEnumerable<HrSample> Empty()
		{
			yield break;
		}
#pragma warning restore CS1998
	}

	private sealed class EmptyBeatSource : IBeatSource
	{
#pragma warning disable CS1998
		public async IAsyncEnumerable<Beat> GetBeatsAsync(
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
		{
			yield break;
		}
#pragma warning restore CS1998
	}
}
