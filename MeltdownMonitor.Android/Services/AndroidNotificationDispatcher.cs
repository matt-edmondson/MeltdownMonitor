using Android.App;
using Android.Content;
using Android.OS;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.Android.Services;

/// <summary>
/// <see cref="INotificationDispatcher"/> backed by the framework
/// <c>NotificationManager</c> with channels (mandatory since Android 8 / API 26,
/// our floor). Two channels mirror the iOS split (design doc §5.4): a
/// high-importance Alert channel for hyperarousal meltdown alerts, and a
/// low-importance Status channel (shared with the ongoing monitoring
/// notification) for the quiet cooldown update.
///
/// <para>
/// Hypoarousal / shutdown alerts get the same gentle treatment the iOS
/// dispatcher applies — silent, softly worded, no high-priority interruption —
/// because a jarring cue can deepen a shutdown (design doc §5.4 / sensory
/// overload).
/// </para>
/// </summary>
public sealed class AndroidNotificationDispatcher : INotificationDispatcher
{
	public const string AlertChannelId = "meltdownmonitor.alert";

	private readonly Context _context;
	private readonly MobileSettings _settings;
	private int _alertIdSeed = 1000;

	public AndroidNotificationDispatcher(Context context, MobileSettings settings)
	{
		_context = context ?? throw new ArgumentNullException(nameof(context));
		_settings = settings ?? throw new ArgumentNullException(nameof(settings));
		EnsureChannels();
	}

	/// <summary>
	/// On Android the POST_NOTIFICATIONS runtime grant (API 33+) is driven from the
	/// Activity, so this reports the current grant state rather than presenting the
	/// system dialog itself. Always true below API 33, where the grant is implicit.
	/// </summary>
	public Task<bool> RequestAuthorizationAsync()
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(33))
		{
			return Task.FromResult(true);
		}

		bool granted = _context.CheckSelfPermission("android.permission.POST_NOTIFICATIONS")
			== global::Android.Content.PM.Permission.Granted;
		return Task.FromResult(granted);
	}

	public Task PostAlertAsync(AlertPayload payload)
	{
		bool gentle = payload.Kind == AlertKind.Hypoarousal;

		string body = gentle
			? "A low, flat moment. When you're ready, a small movement or a sip of water can help you re-engage."
			: _settings.AlertSuggestion;

		var builder = new Notification.Builder(_context, gentle ? MonitoringService.StatusChannelId : AlertChannelId)
			.SetContentTitle("Meltdown Monitor")!
			.SetContentText(body)!
			.SetSubText($"RMSSD {payload.RmssdAtTrigger:F1} ms (baseline {payload.BaselineAtTrigger:F1} ms)")!
			.SetSmallIcon(global::Android.Resource.Drawable.IcDialogAlert)!
			.SetAutoCancel(true)!;

		if (!gentle)
		{
			builder.SetCategory(Notification.CategoryAlarm!);
			if (OperatingSystem.IsAndroidVersionAtLeast(26))
			{
				// Channel importance carries the heads-up behaviour on API 26+.
			}
			else
			{
#pragma warning disable CA1422 // Priority is the pre-channel mechanism for API < 26.
				builder.SetPriority((int)NotificationPriority.High);
#pragma warning restore CA1422
			}
		}

		Notify(NextAlertId(), builder.Build());
		return Task.CompletedTask;
	}

	public Task PostStatusAsync(DetectorState state)
	{
		// Lightweight, like the iOS badge-only status: only the cooldown return is
		// worth a quiet line; everything else is carried by the ongoing notification.
		if (state != DetectorState.Cooldown)
		{
			return Task.CompletedTask;
		}

		var builder = new Notification.Builder(_context, MonitoringService.StatusChannelId)
			.SetContentTitle("Meltdown Monitor")!
			.SetContentText("Returning to baseline.")!
			.SetSmallIcon(global::Android.Resource.Drawable.IcMenuInfoDetails)!
			.SetAutoCancel(true)!;

		Notify(NextAlertId(), builder.Build());
		return Task.CompletedTask;
	}

	private int NextAlertId() => System.Threading.Interlocked.Increment(ref _alertIdSeed);

	private void Notify(int id, Notification notification)
	{
		var manager = (NotificationManager?)_context.GetSystemService(Context.NotificationService);
		manager?.Notify(id, notification);
	}

	private void EnsureChannels()
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(26))
		{
			return;
		}

		var manager = (NotificationManager?)_context.GetSystemService(Context.NotificationService);
		if (manager is null)
		{
			return;
		}

		if (manager.GetNotificationChannel(AlertChannelId) is null)
		{
			var alert = new NotificationChannel(AlertChannelId, "Alerts", NotificationImportance.High)
			{
				Description = "Heads-up alerts when dysregulation is detected.",
			};
			manager.CreateNotificationChannel(alert);
		}

		// The Status channel is also created by MonitoringService; creating it here
		// too is harmless (CreateNotificationChannel is idempotent) and means status
		// notifications work even before the service has started.
		if (manager.GetNotificationChannel(MonitoringService.StatusChannelId) is null)
		{
			var status = new NotificationChannel(
				MonitoringService.StatusChannelId, "Monitoring status", NotificationImportance.Low)
			{
				Description = "The ongoing monitoring notification and quiet status updates.",
			};
			status.SetShowBadge(false);
			manager.CreateNotificationChannel(status);
		}
	}
}
