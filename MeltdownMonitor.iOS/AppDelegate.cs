using Avalonia;
using Avalonia.iOS;
using Foundation;
using MeltdownMonitor.iOS.Services;
using UIKit;

namespace MeltdownMonitor.iOS;

[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<MeltdownMonitor.Mobile.App>
{
	// Notification tokens are retained for the app's lifetime so the observers
	// aren't collected. AvaloniaAppDelegate doesn't expose the UIApplication
	// lifecycle methods as overridable (scene-based lifecycle), so we observe
	// the notifications instead (design doc §6.2).
	private NSObject? _foregroundObserver;
	private NSObject? _terminateObserver;

	protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
	{
		// Create the BLE central manager synchronously, as early in the launch path as possible, so
		// iOS reliably delivers CoreBluetooth state restoration (WillRestoreState) on a background
		// relaunch. Deferring this to the async pipeline build (App.Started, below) risks iOS dropping
		// the restoration and leaving the app dead in the background (design doc §4.1). The pipeline
		// build reuses this instance.
		IosCompositionRoot.PrepareBleRestoration();

		// Install the iOS-specific factory so Avalonia's
		// OnFrameworkInitializationCompleted picks up a fully composed VM
		// instead of the stub used for design-time previews.
		MeltdownMonitor.Mobile.App.RootViewModelFactory =
			IosCompositionRoot.BuildRootViewModel;

		// Kick off the live BLE pipeline once Avalonia is up and the view
		// models exist (design doc §6.1). Started runs on the UI thread at the
		// end of framework init; the composition itself is async and hands the
		// heavy work to a background thread.
		MeltdownMonitor.Mobile.App.Started =
			() => _ = IosCompositionRoot.BuildAndStartPipelineAsync();

		// Activate the playback audio session so the optional alert chime
		// survives the screen lock. See design doc §4.6. Avalonia 12 made
		// FinishedLaunching non-virtual, so this is the surviving extension
		// point that still runs once per cold start.
		AudioSessionConfigurator.Configure();

		RegisterLifecycleObservers();

		return base.CustomizeAppBuilder(builder);
	}

	private void RegisterLifecycleObservers()
	{
		var center = NSNotificationCenter.DefaultCenter;

		// Foreground return: views may have torn down while backgrounded, so
		// refresh the state pill and reload history from the live pipeline.
		_foregroundObserver = center.AddObserver(
			UIApplication.WillEnterForegroundNotification,
			_ => IosCompositionRoot.OnEnterForeground());

		// Graceful stop so the repository's last writes land before iOS
		// reclaims us. Bounded — we can't block the terminate path for long.
		_terminateObserver = center.AddObserver(
			UIApplication.WillTerminateNotification,
			_ => IosCompositionRoot.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult());

		// DidEnterBackground is deliberately not observed: staying connected in
		// the background is the whole point of bluetooth-central mode, and the
		// TRUNCATE journal commits each write durably, so there is nothing to do.
	}
}
