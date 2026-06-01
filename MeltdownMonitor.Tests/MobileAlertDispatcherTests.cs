using System.Reflection;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MobileAlertDispatcherTests
{
	[TestMethod]
	public void Alert_PlaysChime_WhenEnabled()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var chime = new RecordingChime();
			var notifier = new RecordingNotifier();
			using var _ = new MobileAlertDispatcher(
				pipeline,
				new MobileSettings { EnableChime = true, EnableNotifications = true },
				notifier,
				chime);

			RaiseAlert(pipeline);

			Assert.AreEqual(1, chime.Plays);
			Assert.AreEqual(1, notifier.Alerts);
		}
	}

	[TestMethod]
	public void Alert_DoesNotPlayChime_WhenDisabled()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var chime = new RecordingChime();
			using var _ = new MobileAlertDispatcher(
				pipeline,
				new MobileSettings { EnableChime = false, EnableNotifications = false },
				new RecordingNotifier(),
				chime);

			RaiseAlert(pipeline);

			Assert.AreEqual(0, chime.Plays);
		}
	}

	[TestMethod]
	public void HypoarousalAlert_DoesNotChime_ButStillNotifies()
	{
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var chime = new RecordingChime();
			var notifier = new RecordingNotifier();
			using var _ = new MobileAlertDispatcher(
				pipeline,
				new MobileSettings { EnableChime = true, EnableNotifications = true },
				notifier,
				chime);

			RaiseAlert(pipeline, AlertKind.Hypoarousal);

			Assert.AreEqual(0, chime.Plays, "A low-arousal alert must not play the jarring chime.");
			Assert.AreEqual(1, notifier.Alerts, "…but it must still post the (softened) notification.");
		}
	}

	[TestMethod]
	public void StateChanges_DoNotPlayChime()
	{
		// The detector fires AlertFired exactly once per episode (Warning →
		// Cooldown), so the chime is bound to AlertFired, never to StateChanged.
		// Re-entering states must stay silent.
		var (pipeline, repo) = NewPipeline();
		using (repo)
		using (pipeline)
		{
			var chime = new RecordingChime();
			using var _ = new MobileAlertDispatcher(
				pipeline,
				new MobileSettings { EnableChime = true, EnableNotifications = true },
				new RecordingNotifier(),
				chime);

			RaiseStateChanged(pipeline, DetectorState.Alerting);
			RaiseStateChanged(pipeline, DetectorState.Cooldown);
			RaiseStateChanged(pipeline, DetectorState.Watching);

			Assert.AreEqual(0, chime.Plays, "State transitions must not chime — only fired alerts do.");
		}
	}

	private static (Pipeline, MeltdownRepository) NewPipeline()
	{
		var repo = new MeltdownRepository(":memory:");
		var pipeline = new Pipeline(new MobileSettings(), repo, new EmptyBeatSource());
		return (pipeline, repo);
	}

	private static void RaiseAlert(Pipeline pipeline)
	{
		var payload = new AlertPayload(DateTimeOffset.UtcNow, "test", 20, 50);
		RaiseEvent<Action<AlertPayload>>(pipeline, "AlertFired", d => d(payload));
	}

	private static void RaiseAlert(Pipeline pipeline, AlertKind kind)
	{
		var payload = new AlertPayload(DateTimeOffset.UtcNow, "test", 20, 50, kind);
		RaiseEvent<Action<AlertPayload>>(pipeline, "AlertFired", d => d(payload));
	}

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

	private sealed class RecordingChime : IChimePlayer
	{
		public int Plays { get; private set; }

		public void PlayAlertChime() => Plays++;
	}

	private sealed class RecordingNotifier : INotificationDispatcher
	{
		public int Alerts { get; private set; }

		public Task<bool> RequestAuthorizationAsync() => Task.FromResult(true);

		public Task PostAlertAsync(AlertPayload payload)
		{
			Alerts++;
			return Task.CompletedTask;
		}

		public Task PostStatusAsync(DetectorState state) => Task.CompletedTask;
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
