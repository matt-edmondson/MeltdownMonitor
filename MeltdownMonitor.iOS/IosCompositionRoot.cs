using MeltdownMonitor.iOS.Services;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.iOS;

/// <summary>
/// Wires the platform-neutral Mobile view models to the iOS-specific
/// services (notifications, audio chime, NSUserDefaults, HealthKit).
/// Lives in the head project so the Mobile assembly never takes a
/// CoreBluetooth / UserNotifications / AVFoundation / HealthKit
/// dependency.
/// </summary>
public static class IosCompositionRoot
{
	private static MobileAlertDispatcher? _alertDispatcher;
	private static NotificationDispatcher? _notifications;
	private static HealthKitStore? _healthStore;

	/// <summary>HealthKit facade kept alive for the app's lifetime so the
	/// live pipeline can warm-start from it on every relaunch.</summary>
	public static IHealthStore? HealthStore => _healthStore;

	public static RootViewModel BuildRootViewModel()
	{
		var settings = new MobileSettings();
		var store = new NSUserDefaultsSettingsStore();
		settings.IsDisclaimerAccepted = store.LoadDisclaimerAccepted();

		_notifications = new NotificationDispatcher(settings);
		_healthStore = new HealthKitStore();

		var settingsTab = new SettingsViewModel(
			settings,
			requestNotifications: () => _notifications.RequestAuthorizationAsync(),
			requestHealthKit: () => _healthStore.RequestAuthorizationAsync());

		return new RootViewModel(
			settings,
			new NowViewModel(),
			new HistoryViewModel(),
			settingsTab,
			store);
	}

	/// <summary>
	/// Hook for when the live pipeline is composed — keeps the alert
	/// dispatcher alive for the app's lifetime. Safe to call before or
	/// after <see cref="BuildRootViewModel"/>.
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
