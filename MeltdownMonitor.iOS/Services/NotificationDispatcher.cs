using Foundation;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;
using UserNotifications;

namespace MeltdownMonitor.iOS.Services;

/// <summary>
/// <see cref="INotificationDispatcher"/> backed by
/// <c>UNUserNotificationCenter</c>. Per design doc §4.2 we split into two
/// notification categories: time-sensitive alerts with a soft sound, and
/// silent status updates that only refresh the badge.
/// </summary>
public sealed class NotificationDispatcher : INotificationDispatcher
{
	private const string AlertCategoryId = "meltdownmonitor.alert";
	private const string StatusCategoryId = "meltdownmonitor.status";

	private readonly MobileSettings _settings;

	public NotificationDispatcher(MobileSettings settings)
	{
		_settings = settings;

		var alertCategory = UNNotificationCategory.FromIdentifier(
			AlertCategoryId,
			actions: Array.Empty<UNNotificationAction>(),
			intentIdentifiers: Array.Empty<string>(),
			options: UNNotificationCategoryOptions.None);

		var statusCategory = UNNotificationCategory.FromIdentifier(
			StatusCategoryId,
			actions: Array.Empty<UNNotificationAction>(),
			intentIdentifiers: Array.Empty<string>(),
			options: UNNotificationCategoryOptions.None);

		UNUserNotificationCenter.Current.SetNotificationCategories(
			new NSSet<UNNotificationCategory>(alertCategory, statusCategory));
	}

	public Task<bool> RequestAuthorizationAsync()
	{
		var tcs = new TaskCompletionSource<bool>();
		const UNAuthorizationOptions options =
			UNAuthorizationOptions.Alert
			| UNAuthorizationOptions.Sound
			| UNAuthorizationOptions.Badge;

		UNUserNotificationCenter.Current.RequestAuthorization(options, (granted, _) =>
		{
			tcs.TrySetResult(granted);
		});

		return tcs.Task;
	}

	public Task PostAlertAsync(AlertPayload payload)
	{
		var content = new UNMutableNotificationContent
		{
			Title = "Meltdown Monitor",
			Body = _settings.AlertSuggestion,
			Subtitle = $"RMSSD {payload.RmssdAtTrigger:F1} ms (baseline {payload.BaselineAtTrigger:F1} ms)",
			CategoryIdentifier = AlertCategoryId,
			Sound = UNNotificationSound.Default,
			InterruptionLevel = UNNotificationInterruptionLevel.TimeSensitive,
		};

		// Immediate delivery — no trigger.
		var request = UNNotificationRequest.FromIdentifier(
			$"alert-{payload.Timestamp.ToUnixTimeMilliseconds()}",
			content,
			trigger: null);

		var tcs = new TaskCompletionSource<bool>();
		UNUserNotificationCenter.Current.AddNotificationRequest(request, _ => tcs.TrySetResult(true));
		return tcs.Task;
	}

	public Task PostStatusAsync(DetectorState state)
	{
		// Status updates are intentionally lightweight — badge-only, no sound,
		// no banner. They exist so the user can glance at the lock screen and
		// see "still watching" without us spamming notifications.
		if (state != DetectorState.Cooldown)
		{
			return Task.CompletedTask;
		}

		var content = new UNMutableNotificationContent
		{
			Title = "Meltdown Monitor",
			Body = "Returning to baseline.",
			CategoryIdentifier = StatusCategoryId,
			Sound = null,
		};

		var request = UNNotificationRequest.FromIdentifier(
			$"status-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
			content,
			trigger: null);

		var tcs = new TaskCompletionSource<bool>();
		UNUserNotificationCenter.Current.AddNotificationRequest(request, _ => tcs.TrySetResult(true));
		return tcs.Task;
	}
}
