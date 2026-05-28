using System.Runtime.CompilerServices;
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
	public void AlertFired_PlaysChime_WhenEnableChimeTrue()
	{
		using var harness = new Harness(enableChime: true, enableNotifications: false);

		harness.RaiseAlert();

		Assert.AreEqual(1, harness.Chime.PlayCount);
	}

	[TestMethod]
	public void AlertFired_SkipsChime_WhenEnableChimeFalse()
	{
		using var harness = new Harness(enableChime: false, enableNotifications: false);

		harness.RaiseAlert();

		Assert.AreEqual(0, harness.Chime.PlayCount);
	}

	[TestMethod]
	public void AlertFired_PostsNotification_WhenEnableNotificationsTrue()
	{
		using var harness = new Harness(enableChime: false, enableNotifications: true);

		harness.RaiseAlert();

		Assert.AreEqual(1, harness.Notifications.AlertCount);
	}

	[TestMethod]
	public void AlertFired_SkipsNotification_WhenEnableNotificationsFalse()
	{
		using var harness = new Harness(enableChime: false, enableNotifications: false);

		harness.RaiseAlert();

		Assert.AreEqual(0, harness.Notifications.AlertCount);
	}

	[TestMethod]
	public void StateChanged_PostsStatus_WhenEnableNotificationsTrue()
	{
		using var harness = new Harness(enableChime: false, enableNotifications: true);

		harness.RaiseStateChanged(DetectorState.Warning);

		Assert.AreEqual(1, harness.Notifications.StatusCount);
	}

	[TestMethod]
	public void Dispose_UnsubscribesFromPipelineEvents()
	{
		var harness = new Harness(enableChime: true, enableNotifications: true);

		harness.Dispatcher.Dispose();
		harness.RaiseAlert();

		Assert.AreEqual(0, harness.Chime.PlayCount);
		Assert.AreEqual(0, harness.Notifications.AlertCount);

		harness.Dispose();
	}

	private sealed class Harness : IDisposable
	{
		public MobileSettings Settings { get; }
		public Pipeline Pipeline { get; }
		public MeltdownRepository Repository { get; }
		public FakeNotificationDispatcher Notifications { get; }
		public FakeChimePlayer Chime { get; }
		public MobileAlertDispatcher Dispatcher { get; }

		public Harness(bool enableChime, bool enableNotifications)
		{
			Settings = new MobileSettings
			{
				EnableChime = enableChime,
				EnableNotifications = enableNotifications,
			};
			Repository = new MeltdownRepository(":memory:");
			Pipeline = new Pipeline(Settings, Repository, new EmptyBeatSource());
			Notifications = new FakeNotificationDispatcher();
			Chime = new FakeChimePlayer();
			Dispatcher = new MobileAlertDispatcher(Pipeline, Settings, Notifications, Chime);
		}

		public void RaiseAlert()
		{
			var payload = new AlertPayload(
				Timestamp: DateTimeOffset.UtcNow,
				TriggerReason: "test",
				RmssdAtTrigger: 12,
				BaselineAtTrigger: 30);
			InvokeDetectorAlert(payload);
		}

		public void RaiseStateChanged(DetectorState state) => InvokeDetectorStateChanged(state);

		private void InvokeDetectorAlert(AlertPayload payload)
		{
			var field = typeof(Pipeline).GetField(
				"_detector",
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
			var detector = (DysregulationDetector)field!.GetValue(Pipeline)!;
			var ev = typeof(DysregulationDetector).GetEvent("AlertFired")!;
			var raise = detector.GetType()
				.GetField("AlertFired", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
			var del = (Action<AlertPayload>?)raise!.GetValue(detector);
			del?.Invoke(payload);
		}

		private void InvokeDetectorStateChanged(DetectorState state)
		{
			var field = typeof(Pipeline).GetField(
				"_detector",
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
			var detector = (DysregulationDetector)field!.GetValue(Pipeline)!;
			var raise = detector.GetType()
				.GetField("StateChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
			var del = (Action<DetectorState>?)raise!.GetValue(detector);
			del?.Invoke(state);
		}

		public void Dispose()
		{
			Dispatcher.Dispose();
			Pipeline.Dispose();
			Repository.Dispose();
		}
	}

	private sealed class FakeNotificationDispatcher : INotificationDispatcher
	{
		public int AlertCount { get; private set; }
		public int StatusCount { get; private set; }

		public Task<bool> RequestAuthorizationAsync() => Task.FromResult(true);

		public Task PostAlertAsync(AlertPayload payload)
		{
			AlertCount++;
			return Task.CompletedTask;
		}

		public Task PostStatusAsync(DetectorState state)
		{
			StatusCount++;
			return Task.CompletedTask;
		}
	}

	private sealed class FakeChimePlayer : IChimePlayer
	{
		public int PlayCount { get; private set; }

		public void PlayAlertChime() => PlayCount++;
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
