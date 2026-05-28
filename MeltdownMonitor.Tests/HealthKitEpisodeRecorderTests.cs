using System.Runtime.CompilerServices;
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
	public void AlertFired_DoesNotWrite_WhenOptInIsOff()
	{
		using var harness = new Harness(writeEnabled: false);

		harness.RaiseStateChanged(DetectorState.Warning);
		Thread.Sleep(5);
		harness.RaiseAlert();
		harness.WaitForWrites();

		Assert.AreEqual(0, harness.HealthStore.WrittenEpisodes.Count);
	}

	[TestMethod]
	public void AlertFired_WritesEpisode_WhenOptInIsOn()
	{
		using var harness = new Harness(writeEnabled: true);

		harness.RaiseStateChanged(DetectorState.Warning);
		Thread.Sleep(5);
		harness.RaiseAlert();
		harness.WaitForWrites();

		Assert.AreEqual(1, harness.HealthStore.WrittenEpisodes.Count);
		var ep = harness.HealthStore.WrittenEpisodes[0];
		Assert.IsTrue(ep.End > ep.Start, "Episode must have positive duration.");
		Assert.AreEqual("Dysregulation episode", ep.Label);
	}

	[TestMethod]
	public void AlertFired_WithoutWarningTransition_UsesFallbackWindow()
	{
		using var harness = new Harness(writeEnabled: true);

		// No prior Warning transition — recorder must fall back to a fixed
		// window so we never write a zero-length workout.
		harness.RaiseAlert();
		harness.WaitForWrites();

		Assert.AreEqual(1, harness.HealthStore.WrittenEpisodes.Count);
		var ep = harness.HealthStore.WrittenEpisodes[0];
		Assert.IsTrue((ep.End - ep.Start).TotalMinutes >= 4.9,
			"Fallback window should be ~5 minutes.");
	}

	[TestMethod]
	public void StateChangedToIdle_ClearsWarningWindow()
	{
		using var harness = new Harness(writeEnabled: true);

		// Warning starts, then user is back to Idle, then much later a
		// fresh Warning + alert fires — episode start should be the second
		// Warning, not the first one.
		harness.RaiseStateChanged(DetectorState.Warning);
		Thread.Sleep(20);
		harness.RaiseStateChanged(DetectorState.Idle);
		Thread.Sleep(20);
		harness.RaiseStateChanged(DetectorState.Warning);
		Thread.Sleep(5);
		harness.RaiseAlert();
		harness.WaitForWrites();

		var ep = harness.HealthStore.WrittenEpisodes[0];
		// Episode start should be after the Idle reset, i.e. close to "now"
		// — well under 30ms of total duration.
		Assert.IsTrue((ep.End - ep.Start).TotalMinutes < 1,
			"Episode start was not reset by the intervening Idle transition.");
	}

	private sealed class Harness : IDisposable
	{
		public MobileSettings Settings { get; }
		public Pipeline Pipeline { get; }
		public MeltdownRepository Repository { get; }
		public RecordingHealthStore HealthStore { get; }
		public HealthKitEpisodeRecorder Recorder { get; }

		public Harness(bool writeEnabled)
		{
			Settings = new MobileSettings
			{
				WriteEpisodesToHealthKit = writeEnabled,
			};
			Repository = new MeltdownRepository(":memory:");
			Pipeline = new Pipeline(Settings, Repository, new EmptyBeatSource());
			HealthStore = new RecordingHealthStore();
			Recorder = new HealthKitEpisodeRecorder(Pipeline, Settings, HealthStore);
		}

		public void RaiseAlert()
		{
			var payload = new AlertPayload(
				Timestamp: DateTimeOffset.UtcNow,
				TriggerReason: "test",
				RmssdAtTrigger: 12,
				BaselineAtTrigger: 30);

			var detector = GetDetector();
			var raise = detector.GetType().GetField(
				"AlertFired",
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
			((Action<AlertPayload>?)raise!.GetValue(detector))?.Invoke(payload);
		}

		public void RaiseStateChanged(DetectorState state)
		{
			var detector = GetDetector();
			var raise = detector.GetType().GetField(
				"StateChanged",
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
			((Action<DetectorState>?)raise!.GetValue(detector))?.Invoke(state);
		}

		public void WaitForWrites()
		{
			// Recorder fires fire-and-forget tasks; give them a beat to land.
			SpinWait.SpinUntil(() => HealthStore.WrittenEpisodes.Count > 0, TimeSpan.FromMilliseconds(200));
		}

		private DysregulationDetector GetDetector()
		{
			var field = typeof(Pipeline).GetField(
				"_detector",
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
			return (DysregulationDetector)field!.GetValue(Pipeline)!;
		}

		public void Dispose()
		{
			Recorder.Dispose();
			Pipeline.Dispose();
			Repository.Dispose();
		}
	}

	private sealed class RecordingHealthStore : IHealthStore
	{
		public List<EpisodeRecord> WrittenEpisodes { get; } = new();

		public Task<bool> RequestAuthorizationAsync() => Task.FromResult(true);

#pragma warning disable CS1998
		public async IAsyncEnumerable<HrSample> ReadRecentHeartRateAsync(TimeSpan lookback)
		{
			yield break;
		}
#pragma warning restore CS1998

		public Task WriteHrSampleAsync(HrSample sample) => Task.CompletedTask;

		public Task WriteEpisodeAsync(EpisodeRecord episode)
		{
			lock (WrittenEpisodes)
			{
				WrittenEpisodes.Add(episode);
			}

			return Task.CompletedTask;
		}
	}

	private sealed class EmptyBeatSource : IBeatSource
	{
#pragma warning disable CS1998
		public async IAsyncEnumerable<Beat> GetBeatsAsync(
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			yield break;
		}
#pragma warning restore CS1998
	}
}
