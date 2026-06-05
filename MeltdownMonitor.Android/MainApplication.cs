using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using MobileApp = MeltdownMonitor.Mobile.App;

namespace MeltdownMonitor.Android;

/// <summary>
/// Avalonia 12 Android bootstrap. The framework moved app-builder configuration
/// out of the activity and into an <c>Application</c> subclass, so this is where
/// the composition root's factory and start hook are installed before Avalonia
/// initializes — the Android counterpart to the iOS <c>AppDelegate</c>'s
/// <c>CustomizeAppBuilder</c> (design doc §5 / §13 Phase 1).
/// </summary>
[Application]
public sealed class MainApplication : AvaloniaAndroidApplication<MobileApp>
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
	{
		// Install the Android factory so the shared App builds a fully composed
		// view model instead of the design-time stub, and kick off the live
		// pipeline once the view models exist (design doc §5). Set before
		// base.CustomizeAppBuilder runs framework init.
		MobileApp.RootViewModelFactory = AndroidCompositionRoot.BuildRootViewModel;
		MobileApp.Started = () => _ = AndroidCompositionRoot.BuildAndStartPipelineAsync();

		// FluentTheme (set in the shared App.axaml) falls back to the platform
		// font (Roboto) on Android, so no extra font package is pulled in here.
		return base.CustomizeAppBuilder(builder);
	}
}
