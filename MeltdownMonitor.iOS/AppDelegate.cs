using Avalonia;
using Avalonia.iOS;
using Foundation;
using MeltdownMonitor.iOS.Services;

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

		// Activate the playback audio session so the optional alert chime
		// survives the screen lock. See design doc §4.6. Avalonia 12 made
		// FinishedLaunching non-virtual, so this is the surviving extension
		// point that still runs once per cold start.
		AudioSessionConfigurator.Configure();

		return base.CustomizeAppBuilder(builder);
	}
}
