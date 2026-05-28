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

		return result;
	}
}
