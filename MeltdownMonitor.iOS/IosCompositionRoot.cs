using MeltdownMonitor.iOS.Services;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.iOS;

/// <summary>
/// Wires the platform-neutral Mobile view models to the iOS-specific
/// services (notifications, audio chime, NSUserDefaults). Lives in the
/// head project so the Mobile assembly never takes a CoreBluetooth /
/// UserNotifications / AVFoundation dependency.
///
/// Phase 4 wires alerts, chime, the disclaimer flag, and permission asks.
/// The live BLE <c>Pipeline</c> is composed here too so the alert
/// dispatcher has something to listen to; the HealthKit warm-start
/// (Phase 5) is still to come.
/// </summary>
public static class IosCompositionRoot
{
	private static MobileAlertDispatcher? _alertDispatcher;
	private static NotificationDispatcher? _notifications;

	public static RootViewModel BuildRootViewModel()
	{
		var settings = new MobileSettings();
		var store = new NSUserDefaultsSettingsStore();
		settings.IsDisclaimerAccepted = store.LoadDisclaimerAccepted();

		_notifications = new NotificationDispatcher(settings);
		var chime = new AudioChimePlayer();

		var settingsTab = new SettingsViewModel(
			settings,
			requestNotifications: () => _notifications.RequestAuthorizationAsync(),
			requestHealthKit: null);

		return new RootViewModel(
			settings,
			new NowViewModel(),
			new HistoryViewModel(),
			settingsTab,
			store);
	}

	/// <summary>
	/// Hook for when the live pipeline is composed (Phase 5) — keeps the
	/// alert dispatcher alive for the app's lifetime. Safe to call before
	/// or after <see cref="BuildRootViewModel"/>.
	/// </summary>
	public static void AttachAlertDispatcher(
		Pipeline pipeline,
		MobileSettings settings,
		IChimePlayer chime)
	{
		if (_notifications is null)
		{
			return;
		}

		_alertDispatcher?.Dispose();
		_alertDispatcher = new MobileAlertDispatcher(pipeline, settings, _notifications, chime);
	}
}
