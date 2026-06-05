using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.Android;

/// <summary>
/// Foreground service that keeps the process — and therefore the BLE GATT
/// connection — alive once monitoring begins (design doc §5.1 / §13 Phase 3).
/// Android does not relaunch the process from a BLE event the way iOS state
/// restoration does, so a running foreground service with its mandatory ongoing
/// notification is what lets <c>onCharacteristicChanged</c> keep firing with the
/// screen off.
///
/// <para>
/// The pipeline itself lives in application scope on
/// <see cref="AndroidCompositionRoot"/>, not on this service, so a config-change
/// Activity teardown never disturbs it (design doc §5.8). This service's job is
/// purely to hold the process up and to render the ongoing notification, which
/// doubles as the live status surface (design doc §5.5).
/// </para>
/// </summary>
// CA1416: ForegroundServiceType.TypeConnectedDevice is API 29, above the API 26
// floor, and an attribute argument can't be version-guarded the way the runtime
// StartForeground call below is. The manifest already gates the typed permission
// (FOREGROUND_SERVICE_CONNECTED_DEVICE) on API 34, and pre-29 devices ignore the
// foregroundServiceType, so this is safe to assert here.
#pragma warning disable CA1416
[Service(
	Exported = false,
	ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
#pragma warning restore CA1416
public sealed class MonitoringService : Service
{
	/// <summary>Low-importance, silent channel for the ongoing monitoring notification (§5.4).</summary>
	public const string StatusChannelId = "meltdownmonitor.status";

	private const int NotificationId = 1;

	// Latest content to render. Updated by OngoingNotificationActivityController
	// (the ILiveActivityController implementation) and read when (re)building the
	// notification. Defaults to a generic "monitoring" line before any sample.
	// A nullable struct can't be volatile, so a lock guards the cross-thread
	// access (the publisher updates off the UI thread; OnStartCommand reads on it).
	private static readonly object _contentGate = new();
	private static LiveActivityContent? _content;

	private static LiveActivityContent? CurrentContent()
	{
		lock (_contentGate)
		{
			return _content;
		}
	}

	public override IBinder? OnBind(Intent? intent) => null;

	public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
	{
		var notification = BuildNotification(this, CurrentContent());

		if (OperatingSystem.IsAndroidVersionAtLeast(29))
		{
			StartForeground(NotificationId, notification, ForegroundService.TypeConnectedDevice);
		}
		else
		{
			StartForeground(NotificationId, notification);
		}

		// Sticky: if the OS reclaims us under memory pressure, restart so monitoring
		// resumes. autoConnect on the GATT connection re-establishes the sensor link.
		return StartCommandResult.Sticky;
	}

	/// <summary>Starts the foreground service (idempotent at the OS level).</summary>
	public static void Start(Context context)
	{
		var intent = new Intent(context, typeof(MonitoringService));
		if (OperatingSystem.IsAndroidVersionAtLeast(26))
		{
			context.StartForegroundService(intent);
		}
		else
		{
			context.StartService(intent);
		}
	}

	/// <summary>Stops the service and dismisses its ongoing notification.</summary>
	public static void Stop(Context context)
	{
		lock (_contentGate)
		{
			_content = null;
		}

		context.StopService(new Intent(context, typeof(MonitoringService)));
	}

	/// <summary>
	/// Pushes fresh live content into the ongoing notification (design doc §5.5).
	/// A no-op when the service is not running — the next <see cref="OnStartCommand"/>
	/// picks up the stored content.
	/// </summary>
	public static void UpdateContent(Context context, LiveActivityContent content)
	{
		lock (_contentGate)
		{
			_content = content;
		}

		var manager = (NotificationManager?)context.GetSystemService(NotificationService);
		manager?.Notify(NotificationId, BuildNotification(context, content));
	}

	private static Notification BuildNotification(Context context, LiveActivityContent? content)
	{
		EnsureChannel(context);

		string title = "Meltdown Monitor";
		string text = content is { } c
			? FormatLine(c)
			: "Monitoring heart-rate variability.";

		// Tap opens the app rather than launching a fresh task (SingleTask).
		var launch = new Intent(context, typeof(MainActivity));
		launch.SetFlags(ActivityFlags.SingleTop);
		var pendingFlags = OperatingSystem.IsAndroidVersionAtLeast(31)
			? PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent
			: PendingIntentFlags.UpdateCurrent;
		var contentIntent = PendingIntent.GetActivity(context, 0, launch, pendingFlags);

		var builder = new Notification.Builder(context, StatusChannelId)
			.SetContentTitle(title)!
			.SetContentText(text)!
			.SetSmallIcon(global::Android.Resource.Drawable.IcMenuInfoDetails)!
			.SetOngoing(true)!
			.SetOnlyAlertOnce(true)!
			.SetContentIntent(contentIntent)!;

		if (content is { } cc && TryParseHexColor(cc.ColorHex, out int color))
		{
			builder.SetColor(color);
		}

		// Build() is typed nullable in the binding but never returns null here.
		return builder.Build()!;
	}

	private static string FormatLine(LiveActivityContent c)
	{
		string label = c.IsPaused ? "Paused" : c.StateLabel;
		string hr = c.HeartRate > 0 ? $" · {c.HeartRate} bpm" : string.Empty;
		string ratio = c.RmssdRatio > 0 ? $" · RMSSD {c.RmssdRatio * 100:F0}%" : string.Empty;
		return $"{label}{hr}{ratio}";
	}

	private static void EnsureChannel(Context context)
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(26))
		{
			return;
		}

		var manager = (NotificationManager?)context.GetSystemService(NotificationService);
		if (manager is null || manager.GetNotificationChannel(StatusChannelId) is not null)
		{
			return;
		}

		var channel = new NotificationChannel(
			StatusChannelId,
			"Monitoring status",
			NotificationImportance.Low)
		{
			Description = "The ongoing notification shown while Meltdown Monitor is watching.",
		};
		channel.SetShowBadge(false);
		manager.CreateNotificationChannel(channel);
	}

	private static bool TryParseHexColor(string? hex, out int color)
	{
		color = 0;
		if (string.IsNullOrEmpty(hex))
		{
			return false;
		}

		try
		{
			color = Color.ParseColor(hex);
			return true;
		}
		catch (global::Java.Lang.IllegalArgumentException)
		{
			return false;
		}
	}
}
