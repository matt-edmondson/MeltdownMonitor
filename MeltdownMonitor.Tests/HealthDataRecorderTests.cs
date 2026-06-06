using System.Reflection;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.Tests;

[TestClass]
public class HealthDataRecorderTests
{
	private static readonly DateTimeOffset T0 = new(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);

	[TestMethod]
	public void Samples_WriteDownsampledHr_WhenOptedIn()
	{
		var (pipeline, repo) = NewPipeline(out var settings);
		settings.RecordToHealth = true;
		using (repo)
		using (pipeline)
		{
			var store = new RecordingHealthStore();
			using var _ = new HealthDataRecorder(
				pipeline, settings, store,
				hrWriteInterval: TimeSpan.FromMinutes(1),
				hrvWriteInterval: TimeSpan.FromMinutes(1));

			// Samples 10 s apart over ~2 min. With a 1-min HR cadence that's 3 writes
			// (the first, then one each minute after).
			for (int i = 0; i <= 12; i++)
			{
				RaiseSample(pipeline, Sample(T0.AddSeconds(i * 10), hr: 70 + i));
			}

			Assert.AreEqual(3, store.HrSamples.Count);
			Assert.AreEqual(70, store.HrSamples[0].HeartRateBpm, 0.001);
		}
	}

	[TestMethod]
	public void Samples_WriteHrv_OnlyWhenExtendedWarm()
	{
		var (pipeline, repo) = NewPipeline(out var settings);
		settings.RecordToHealth = true;
		using (repo)
		using (pipeline)
		{
			var store = new RecordingHealthStore();
			using var _ = new HealthDataRecorder(
				pipeline, settings, store,
				hrvWriteInterval: TimeSpan.FromMinutes(1));

			// No extended metrics yet → no HRV write.
			RaiseSample(pipeline, Sample(T0, rmssd: 42, extended: null));
			Assert.AreEqual(0, store.HrvSamples.Count);

			// Extended warm → one HRV write carrying both RMSSD and SDNN.
			RaiseSample(pipeline, Sample(T0.AddMinutes(2), rmssd: 42, extended: Extended(sdnn: 55)));
			Assert.AreEqual(1, store.HrvSamples.Count);
			Assert.AreEqual(42, store.HrvSamples[0].RmssdMs, 0.001);
			Assert.AreEqual(55, store.HrvSamples[0].SdnnMs, 0.001);
		}
	}

	[TestMethod]
	public void Beats_FlushAsHeartbeatSeries_OnCount()
	{
		var (pipeline, repo) = NewPipeline(out var settings);
		settings.RecordToHealth = true;
		using (repo)
		using (pipeline)
		{
			var store = new RecordingHealthStore();
			using var _ = new HealthDataRecorder(
				pipeline, settings, store,
				seriesFlushInterval: TimeSpan.FromHours(1),
				seriesFlushCount: 5);

			for (int i = 0; i < 12; i++)
			{
				RaiseBeat(pipeline, new Beat(T0.AddSeconds(i), 800, 75, IsArtifact: false));
			}

			// 12 beats, flush every 5 → two full series of 5; 2 remain buffered.
			Assert.AreEqual(2, store.Series.Count);
			Assert.AreEqual(5, store.Series[0].Count);
			Assert.AreEqual(800, store.Series[0][0].RrMs, 0.001);
		}
	}

	[TestMethod]
	public void Beats_ArtifactsExcludedFromSeries()
	{
		var (pipeline, repo) = NewPipeline(out var settings);
		settings.RecordToHealth = true;
		using (repo)
		using (pipeline)
		{
			var store = new RecordingHealthStore();
			using var _ = new HealthDataRecorder(
				pipeline, settings, store, seriesFlushCount: 3);

			RaiseBeat(pipeline, new Beat(T0, 800, 75, IsArtifact: true));
			RaiseBeat(pipeline, new Beat(T0.AddSeconds(1), 810, 74, IsArtifact: false));
			RaiseBeat(pipeline, new Beat(T0.AddSeconds(2), 790, 76, IsArtifact: false));
			RaiseBeat(pipeline, new Beat(T0.AddSeconds(3), 805, 75, IsArtifact: false));

			Assert.AreEqual(1, store.Series.Count);
			Assert.AreEqual(3, store.Series[0].Count, "Artifact beat must not enter the series.");
		}
	}

	[TestMethod]
	public void NothingWritten_WhenOptedOut()
	{
		var (pipeline, repo) = NewPipeline(out var settings);
		settings.RecordToHealth = false;
		using (repo)
		using (pipeline)
		{
			var store = new RecordingHealthStore();
			using var _ = new HealthDataRecorder(pipeline, settings, store, seriesFlushCount: 2);

			RaiseSample(pipeline, Sample(T0, extended: Extended(sdnn: 55)));
			RaiseBeat(pipeline, new Beat(T0, 800, 75, IsArtifact: false));
			RaiseBeat(pipeline, new Beat(T0.AddSeconds(1), 800, 75, IsArtifact: false));

			Assert.AreEqual(0, store.HrSamples.Count);
			Assert.AreEqual(0, store.HrvSamples.Count);
			Assert.AreEqual(0, store.Series.Count);
		}
	}

	[TestMethod]
	public void Dispose_FlushesBufferedBeats()
	{
		var (pipeline, repo) = NewPipeline(out var settings);
		settings.RecordToHealth = true;
		using (repo)
		using (pipeline)
		{
			var store = new RecordingHealthStore();
			var recorder = new HealthDataRecorder(
				pipeline, settings, store,
				seriesFlushInterval: TimeSpan.FromHours(1),
				seriesFlushCount: 100);

			RaiseBeat(pipeline, new Beat(T0, 800, 75, IsArtifact: false));
			RaiseBeat(pipeline, new Beat(T0.AddSeconds(1), 810, 74, IsArtifact: false));
			Assert.AreEqual(0, store.Series.Count, "Below threshold — not flushed yet.");

			recorder.Dispose();
			Assert.AreEqual(1, store.Series.Count);
			Assert.AreEqual(2, store.Series[0].Count);
		}
	}

	private static HrvSample Sample(
		DateTimeOffset ts, double hr = 70, double rmssd = 40, ExtendedHrvMetrics? extended = null) =>
		new(ts, rmssd, 10, hr, 40, 70, DetectorState.Watching) { Extended = extended };

	private static ExtendedHrvMetrics Extended(double sdnn) =>
		new(0, 0, 0, 0, 0, 0, sdnn);

	private static (Pipeline, MeltdownRepository) NewPipeline(out MobileSettings settings)
	{
		var repo = new MeltdownRepository(":memory:");
		settings = new MobileSettings();
		var pipeline = new Pipeline(settings, repo, new EmptyBeatSource());
		return (pipeline, repo);
	}

	private static void RaiseSample(Pipeline pipeline, HrvSample sample) =>
		Invoke<Action<HrvSample>>(pipeline, "SampleUpdated", h => h(sample));

	private static void RaiseBeat(Pipeline pipeline, Beat beat) =>
		Invoke<Action<Beat>>(pipeline, "BeatReceived", h => h(beat));

	private static void Invoke<T>(Pipeline pipeline, string eventName, Action<T> invoke)
		where T : class
	{
		var field = typeof(Pipeline).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
		if (field?.GetValue(pipeline) is T handler)
		{
			invoke(handler);
		}
	}

	private sealed class RecordingHealthStore : IHealthStore
	{
		public List<HrSample> HrSamples { get; } = [];
		public List<HealthHrvSample> HrvSamples { get; } = [];
		public List<IReadOnlyList<RrIntervalSample>> Series { get; } = [];

		public Task<bool> RequestAuthorizationAsync() => Task.FromResult(true);

		public IAsyncEnumerable<HrSample> ReadRecentHeartRateAsync(TimeSpan lookback) => Empty();

		public Task WriteHrSampleAsync(HrSample sample)
		{
			HrSamples.Add(sample);
			return Task.CompletedTask;
		}

		public Task WriteHrvSampleAsync(HealthHrvSample sample)
		{
			HrvSamples.Add(sample);
			return Task.CompletedTask;
		}

		public Task WriteHeartbeatSeriesAsync(IReadOnlyList<RrIntervalSample> beats)
		{
			Series.Add(beats);
			return Task.CompletedTask;
		}

		public Task WriteEpisodeAsync(EpisodeRecord episode) => Task.CompletedTask;

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
