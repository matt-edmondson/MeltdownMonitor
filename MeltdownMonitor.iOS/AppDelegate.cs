using Avalonia;
using Avalonia.iOS;
using Foundation;
using MeltdownMonitor.iOS.Services;
using UIKit;

namespace MeltdownMonitor.iOS;

[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<MeltdownMonitor.Mobile.App>
{
	protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
	{
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

		return base.CustomizeAppBuilder(builder);
	}

	// --- Background lifecycle (design doc §6.2) ---

	public override void DidEnterBackground(UIApplication application)
	{
		// Deliberately do NOT stop the pipeline — staying connected in the
		// background is the whole point of the bluetooth-central mode. The
		// TRUNCATE journal commits each write durably, so there is nothing to
		// flush here.
		base.DidEnterBackground(application);
	}

	public override void WillEnterForeground(UIApplication application)
	{
		base.WillEnterForeground(application);

		// Views may have torn down while backgrounded; refresh the pill and
		// reload history from the live pipeline.
		IosCompositionRoot.OnEnterForeground();
	}

	public override void WillTerminate(UIApplication application)
	{
		// Bounded graceful stop so the repository's last writes land before iOS
		// SIGKILLs us. We can't await on this callback, so block briefly.
		IosCompositionRoot.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

		base.WillTerminate(application);
	}
}
