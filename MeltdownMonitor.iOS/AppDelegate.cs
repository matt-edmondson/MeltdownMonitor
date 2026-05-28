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

		return base.CustomizeAppBuilder(builder);
	}

	public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
	{
		bool result = base.FinishedLaunching(application, launchOptions);

		// Activate the playback audio session so the optional alert chime
		// survives the screen lock. See design doc §4.6.
		AudioSessionConfigurator.Configure();

		// On returning users who already accepted the disclaimer we can
		// boot the pipeline immediately. First-launch users wait until
		// they tap "I understand" so the BLE / HealthKit permission
		// prompts come after the disclaimer screen (design doc §4.4) —
		// RootViewModel's disclaimer-accepted callback handles that case.
		if (IosCompositionRoot.Settings?.IsDisclaimerAccepted == true)
		{
			_ = IosCompositionRoot.StartPipelineAsync();
		}

		return result;
	}

	public override void DidEnterBackground(UIApplication application)
	{
		// Do NOT stop the pipeline — the whole point of bluetooth-central
		// background mode (design doc §4.1) is staying connected so we can
		// fire alerts while the app isn't foregrounded. The BLE delegate
		// keeps running on the framework's behalf.
		base.DidEnterBackground(application);
	}

	public override void WillEnterForeground(UIApplication application)
	{
		// On return-to-foreground, ensure the pipeline is running. Gated on
		// disclaimer acceptance same as FinishedLaunching — the user might
		// background-then-foreground the app from the disclaimer screen.
		// AttachPipeline is idempotent, and StartPipelineAsync short-circuits
		// when the pipeline is already running.
		if (IosCompositionRoot.Settings?.IsDisclaimerAccepted == true)
		{
			_ = IosCompositionRoot.StartPipelineAsync();
		}

		base.WillEnterForeground(application);
	}

	public override void WillTerminate(UIApplication application)
	{
		// Best-effort flush before iOS reclaims the process; bounded so
		// we don't trip the watchdog.
		try
		{
			IosCompositionRoot.ShutdownPipelineAsync(TimeSpan.FromSeconds(1))
				.GetAwaiter()
				.GetResult();
		}
		catch
		{
			// Swallow — we're terminating anyway.
		}

		base.WillTerminate(application);
	}
}
