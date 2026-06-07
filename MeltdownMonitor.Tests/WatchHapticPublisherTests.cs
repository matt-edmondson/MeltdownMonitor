using System.Reflection;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Core.Regulation;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.Tests;

[TestClass]
public class WatchHapticPublisherTests
{
	[TestMethod]
	public void FirstReading_PushesState_WhenEnabled()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var session = new RecordingWatchSession();
			using var _ = new WatchHapticPublisher(
				pipeline, session, new MobileSettings { EnableWatchHaptics = true });

			RaiseReading(pipeline, Reading(index: 0.5));

			Assert.AreEqual(1, session.States.Count);
			// Default ceiling is Low (0.4); intensity = clamp(index) * ceiling.
			Assert.AreEqual(0.5 * 0.4, session.States[0].Intensity, 1e-9);
			Assert.IsTrue(session.States[0].BreathPeriodSeconds > 0);
		}
	}

	[TestMethod]
	public void Disabled_NeverPushes()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var session = new RecordingWatchSession();
			using var _ = new WatchHapticPublisher(
				pipeline, session, new MobileSettings { EnableWatchHaptics = false });

			RaiseReading(pipeline, Reading(0.8));
			RaiseStateChanged(pipeline, DetectorState.Alerting);

			Assert.AreEqual(0, session.States.Count);
			Assert.AreEqual(0, session.Cues.Count);
		}
	}

	[TestMethod]
	public void ColdBaseline_PushesSilentState()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var session = new RecordingWatchSession();
			using var _ = new WatchHapticPublisher(
				pipeline, session, new MobileSettings { EnableWatchHaptics = true });

			RaiseReading(pipeline, Reading(index: 0.8, confidence: 0.1));

			Assert.AreEqual(1, session.States.Count);
			Assert.AreEqual(0.0, session.States[0].Intensity, 1e-9);
		}
	}

	[TestMethod]
	public void RapidReadings_AreThrottledToOneHz()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var clock = new FakeClock();
			var session = new RecordingWatchSession();
			using var _ = new WatchHapticPublisher(
				pipeline, session, new MobileSettings { EnableWatchHaptics = true },
				minUpdateInterval: TimeSpan.FromSeconds(1), clock: clock.Now);

			RaiseReading(pipeline, Reading(0.3));                 // t=0 first push
			clock.Advance(TimeSpan.FromMilliseconds(200));
			RaiseReading(pipeline, Reading(0.4));                 // dropped
			clock.Advance(TimeSpan.FromMilliseconds(400));
			RaiseReading(pipeline, Reading(0.5));                 // dropped
			clock.Advance(TimeSpan.FromMilliseconds(500));
			RaiseReading(pipeline, Reading(0.6));                 // +1.1 s — update

			Assert.AreEqual(2, session.States.Count);
			Assert.AreEqual(0.6 * 0.4, session.States[1].Intensity, 1e-9);
		}
	}

	[TestMethod]
	public void StateChange_BypassesThrottle_AndSendsCue()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var clock = new FakeClock();
			var session = new RecordingWatchSession();
			using var _ = new WatchHapticPublisher(
				pipeline, session, new MobileSettings { EnableWatchHaptics = true },
				minUpdateInterval: TimeSpan.FromSeconds(1), clock: clock.Now);

			RaiseReading(pipeline, Reading(0.3));                 // start
			clock.Advance(TimeSpan.FromMilliseconds(100));        // well inside the window
			RaiseStateChanged(pipeline, DetectorState.Alerting);

			Assert.AreEqual(2, session.States.Count, "State change must push immediately.");
			Assert.AreEqual(DetectorState.Alerting, session.States[1].State);
			CollectionAssert.AreEqual(
				new[] { WatchHapticCue.EscalatedToAlerting }, session.Cues);
		}
	}

	[TestMethod]
	public void DisablingAtRuntime_PushesSilentStateOnce()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var session = new RecordingWatchSession();
			var settings = new MobileSettings { EnableWatchHaptics = true };
			using var _ = new WatchHapticPublisher(pipeline, session, settings);

			RaiseReading(pipeline, Reading(0.8));
			Assert.AreEqual(1, session.States.Count);

			settings.EnableWatchHaptics = false;
			RaiseReading(pipeline, Reading(0.9));   // final silent push
			RaiseReading(pipeline, Reading(0.9));   // already stopped — nothing

			Assert.AreEqual(2, session.States.Count);
			Assert.AreEqual(0.0, session.States[1].Intensity, 1e-9);
		}
	}

	[TestMethod]
	public void Paused_PushesSilentState()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var clock = new FakeClock();
			var session = new RecordingWatchSession();
			var settings = new MobileSettings
			{
				EnableWatchHaptics = true,
				PausedUntil = clock.Value.AddHours(1),
			};
			using var _ = new WatchHapticPublisher(pipeline, session, settings, clock: clock.Now);

			RaiseReading(pipeline, Reading(0.8));

			Assert.IsTrue(session.States[0].IsPaused);
			Assert.AreEqual(0.0, session.States[0].Intensity, 1e-9);
			Assert.AreEqual("Paused", session.States[0].StateLabel);
		}
	}

	[TestMethod]
	public async Task StopAsync_PushesFinalSilentState()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var session = new RecordingWatchSession();
			var publisher = new WatchHapticPublisher(
				pipeline, session, new MobileSettings { EnableWatchHaptics = true });

			RaiseReading(pipeline, Reading(0.8));
			await publisher.StopAsync();

			Assert.AreEqual(2, session.States.Count);
			Assert.AreEqual(0.0, session.States[^1].Intensity, 1e-9);
		}
	}

	[TestMethod]
	public void ThrowingSession_NeverPropagates()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			using var _ = new WatchHapticPublisher(
				pipeline, new ThrowingWatchSession(), new MobileSettings { EnableWatchHaptics = true });

			// A throwing watch session must never surface into the BLE/pipeline path.
			RaiseReading(pipeline, Reading(0.8));
			RaiseStateChanged(pipeline, DetectorState.Alerting);
		}
	}

	private static RegulationReading Reading(double index, double confidence = 1.0) =>
		new(index, VariabilityQuality: 1.0, confidence, LobeRoundness: 0.5, LfHfBalance: 0.0);

	private static (Pipeline, MeltdownRepository) NewPipeline()
	{
		var repo = new MeltdownRepository(":memory:");
		var pipeline = new Pipeline(new MobileSettings(), repo, new EmptyBeatSource());
		return (pipeline, repo);
	}

	private static void RaiseReading(Pipeline pipeline, RegulationReading reading) =>
		RaiseEvent<Action<RegulationReading>>(pipeline, "ReadingUpdated", d => d(reading));

	private static void RaiseStateChanged(Pipeline pipeline, DetectorState state) =>
		RaiseEvent<Action<DetectorState>>(pipeline, "StateChanged", d => d(state));

	private static void RaiseEvent<TDelegate>(Pipeline pipeline, string eventName, Action<TDelegate> invoke)
		where TDelegate : class
	{
		var field = typeof(Pipeline).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
		if (field?.GetValue(pipeline) is TDelegate handler)
		{
			invoke(handler);
		}
	}

	private sealed class RecordingWatchSession : IWatchSession
	{
		public List<WatchHapticState> States { get; } = [];
		public List<WatchHapticCue> Cues { get; } = [];
		public bool IsReachable => true;

		public Task UpdateStateAsync(WatchHapticState state)
		{
			States.Add(state);
			return Task.CompletedTask;
		}

		public Task SendCueAsync(WatchHapticCue cue)
		{
			Cues.Add(cue);
			return Task.CompletedTask;
		}
	}

	private sealed class ThrowingWatchSession : IWatchSession
	{
		public bool IsReachable => true;
		public Task UpdateStateAsync(WatchHapticState state) => throw new InvalidOperationException();
		public Task SendCueAsync(WatchHapticCue cue) => throw new InvalidOperationException();
	}

	private sealed class FakeClock
	{
		public DateTimeOffset Value { get; private set; } = new(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);

		public DateTimeOffset Now() => Value;

		public void Advance(TimeSpan by) => Value += by;
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
