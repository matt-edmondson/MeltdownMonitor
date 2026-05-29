using System.Reflection;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.Tests;

[TestClass]
public class LiveActivityPublisherTests
{
	[TestMethod]
	public void FirstSample_StartsActivity_WhenEnabled()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var clock = new FakeClock();
			var controller = new RecordingController();
			using var _ = new LiveActivityPublisher(
				pipeline,
				controller,
				new MobileSettings { EnableLiveActivity = true },
				clock: clock.Now);

			RaiseSample(pipeline, SampleAt(clock.Value, DetectorState.Watching, rmssd: 40, baseline: 50, hr: 72));

			Assert.AreEqual(1, controller.Starts.Count);
			Assert.AreEqual(0, controller.Updates.Count);
			var content = controller.Starts[0];
			Assert.AreEqual(DetectorState.Watching, content.State);
			Assert.AreEqual(72, content.HeartRate);
			Assert.AreEqual(0.8, content.RmssdRatio, 1e-9);
			Assert.AreEqual("#2980D8", content.ColorHex);
		}
	}

	[TestMethod]
	public void Disabled_NeverStarts()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var controller = new RecordingController();
			using var _ = new LiveActivityPublisher(
				pipeline,
				controller,
				new MobileSettings { EnableLiveActivity = false });

			RaiseSample(pipeline, SampleAt(DateTimeOffset.UtcNow, DetectorState.Watching, 40, 50, 72));

			Assert.AreEqual(0, controller.Starts.Count);
			Assert.IsFalse(controller.IsActive);
		}
	}

	[TestMethod]
	public void RapidSamples_AreThrottledToOneHz()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var clock = new FakeClock();
			var controller = new RecordingController();
			using var _ = new LiveActivityPublisher(
				pipeline,
				controller,
				new MobileSettings { EnableLiveActivity = true },
				minUpdateInterval: TimeSpan.FromSeconds(1),
				clock: clock.Now);

			// t=0 starts the activity.
			RaiseSample(pipeline, SampleAt(clock.Value, DetectorState.Watching, 40, 50, 70));
			// +200 ms and +600 ms are inside the 1 s window — both dropped.
			clock.Advance(TimeSpan.FromMilliseconds(200));
			RaiseSample(pipeline, SampleAt(clock.Value, DetectorState.Watching, 41, 50, 71));
			clock.Advance(TimeSpan.FromMilliseconds(400));
			RaiseSample(pipeline, SampleAt(clock.Value, DetectorState.Watching, 42, 50, 72));
			// +500 ms crosses the 1 s mark — one update.
			clock.Advance(TimeSpan.FromMilliseconds(500));
			RaiseSample(pipeline, SampleAt(clock.Value, DetectorState.Watching, 43, 50, 73));

			Assert.AreEqual(1, controller.Starts.Count);
			Assert.AreEqual(1, controller.Updates.Count, "Only the sample past the 1 s window should update.");
			Assert.AreEqual(73, controller.Updates[0].HeartRate);
		}
	}

	[TestMethod]
	public void StateChange_BypassesThrottle()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var clock = new FakeClock();
			var controller = new RecordingController();
			using var _ = new LiveActivityPublisher(
				pipeline,
				controller,
				new MobileSettings { EnableLiveActivity = true },
				minUpdateInterval: TimeSpan.FromSeconds(1),
				clock: clock.Now);

			RaiseSample(pipeline, SampleAt(clock.Value, DetectorState.Watching, 40, 50, 70));
			// Well inside the throttle window, but a state transition must still
			// flip the Lock Screen colour immediately.
			clock.Advance(TimeSpan.FromMilliseconds(100));
			RaiseStateChanged(pipeline, DetectorState.Alerting);

			Assert.AreEqual(1, controller.Updates.Count);
			Assert.AreEqual(DetectorState.Alerting, controller.Updates[0].State);
			Assert.AreEqual("#E04A3F", controller.Updates[0].ColorHex);
		}
	}

	[TestMethod]
	public void DisablingAtRuntime_EndsActivity()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var clock = new FakeClock();
			var controller = new RecordingController();
			var settings = new MobileSettings { EnableLiveActivity = true };
			using var _ = new LiveActivityPublisher(pipeline, controller, settings, clock: clock.Now);

			RaiseSample(pipeline, SampleAt(clock.Value, DetectorState.Watching, 40, 50, 70));
			Assert.IsTrue(controller.IsActive);

			settings.EnableLiveActivity = false;
			clock.Advance(TimeSpan.FromSeconds(2));
			RaiseSample(pipeline, SampleAt(clock.Value, DetectorState.Watching, 41, 50, 71));

			Assert.AreEqual(1, controller.Ends);
			Assert.IsFalse(controller.IsActive);
		}
	}

	[TestMethod]
	public void Paused_ShowsPausedContent()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var clock = new FakeClock();
			var controller = new RecordingController();
			var settings = new MobileSettings
			{
				EnableLiveActivity = true,
				PausedUntil = clock.Value.AddHours(1),
			};
			using var _ = new LiveActivityPublisher(pipeline, controller, settings, clock: clock.Now);

			RaiseSample(pipeline, SampleAt(clock.Value, DetectorState.Watching, 40, 50, 70));

			Assert.IsTrue(controller.Starts[0].IsPaused);
			Assert.AreEqual("Paused", controller.Starts[0].StateLabel);
			Assert.AreEqual("#555555", controller.Starts[0].ColorHex);
		}
	}

	[TestMethod]
	public void WarmingBaseline_RatioIsNeutral()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var controller = new RecordingController();
			using var _ = new LiveActivityPublisher(
				pipeline,
				controller,
				new MobileSettings { EnableLiveActivity = true });

			// baseline 0 = still warming; ratio should fall back to 1.0, not divide by zero.
			RaiseSample(pipeline, SampleAt(DateTimeOffset.UtcNow, DetectorState.Idle, rmssd: 40, baseline: 0, hr: 60));

			Assert.AreEqual(1.0, controller.Starts[0].RmssdRatio, 1e-9);
		}
	}

	[TestMethod]
	public async Task StopAsync_EndsRunningActivity()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var controller = new RecordingController();
			var publisher = new LiveActivityPublisher(
				pipeline,
				controller,
				new MobileSettings { EnableLiveActivity = true });

			RaiseSample(pipeline, SampleAt(DateTimeOffset.UtcNow, DetectorState.Watching, 40, 50, 70));
			await publisher.StopAsync();

			Assert.AreEqual(1, controller.Ends);
			Assert.IsFalse(controller.IsActive);
		}
	}

	private static HrvSample SampleAt(
		DateTimeOffset at, DetectorState state, double rmssd, double baseline, double hr) =>
		new(at, rmssd, Pnn50: 0, MeanHr: hr, BaselineRmssd: baseline, BaselineHr: hr, State: state);

	private static (Pipeline, MeltdownRepository) NewPipeline()
	{
		var repo = new MeltdownRepository(":memory:");
		var pipeline = new Pipeline(new MobileSettings(), repo, new EmptyBeatSource());
		return (pipeline, repo);
	}

	private static void RaiseSample(Pipeline pipeline, HrvSample sample) =>
		RaiseEvent<Action<HrvSample>>(pipeline, "SampleUpdated", d => d(sample));

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

	private sealed class RecordingController : ILiveActivityController
	{
		public List<LiveActivityContent> Starts { get; } = [];
		public List<LiveActivityContent> Updates { get; } = [];
		public int Ends { get; private set; }
		public bool IsActive { get; private set; }

		public Task StartAsync(LiveActivityContent content)
		{
			Starts.Add(content);
			IsActive = true;
			return Task.CompletedTask;
		}

		public Task UpdateAsync(LiveActivityContent content)
		{
			Updates.Add(content);
			return Task.CompletedTask;
		}

		public Task EndAsync()
		{
			Ends++;
			IsActive = false;
			return Task.CompletedTask;
		}
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
