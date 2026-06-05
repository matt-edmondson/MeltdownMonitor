using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using MobileApp = MeltdownMonitor.Mobile.App;

namespace MeltdownMonitor.Android;

/// <summary>
/// Single-Activity Avalonia host for the Android head. Mirrors the iOS
/// <c>AppDelegate</c>: installs the composition root's factory and start hook
/// before Avalonia initializes, then lets the shared Mobile <c>App</c> bring up
/// the same single-view UI it runs on iOS (design doc §5 / §13 Phase 1).
///
/// <para>
/// <c>LaunchMode.SingleTask</c> is the Android analog of the iOS single-instance
/// guarantee (design doc §6) — a launch while already running re-uses this
/// Activity rather than stacking a second copy on top of the live pipeline. The
/// pipeline itself is owned in application scope by <see cref="AndroidCompositionRoot"/>
/// and kept alive by the foreground <see cref="MonitoringService"/>, so a config
/// change that recreates this Activity rebinds to the running pipeline instead of
/// restarting it (design doc §5.8).
/// </para>
/// </summary>
[Activity(
	Label = "Meltdown Monitor",
	Theme = "@style/MyTheme.NoActionBar",
	MainLauncher = true,
	LaunchMode = LaunchMode.SingleTask,
	ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity<MobileApp>
{
	private const int PermissionRequestCode = 4711;

	protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
	{
		// Install the Android factory so Avalonia's OnFrameworkInitializationCompleted
		// builds a fully composed view model instead of the design-time stub, and
		// kick off the live pipeline once the view models exist (design doc §5 / §13).
		MobileApp.RootViewModelFactory = AndroidCompositionRoot.BuildRootViewModel;
		MobileApp.Started = () => _ = AndroidCompositionRoot.BuildAndStartPipelineAsync();

		// FluentTheme (set in the shared App.axaml) falls back to the platform
		// font (Roboto) on Android, so no extra font package is pulled in here.
		return base.CustomizeAppBuilder(builder);
	}

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);

		// Sequence the runtime asks behind the in-app disclaimer in a later pass
		// (design doc §5.2); for now request the monitoring permissions up front so
		// a fresh install can connect. Already-granted permissions are a no-op.
		RequestMonitoringPermissions();
	}

	private void RequestMonitoringPermissions()
	{
		var needed = new List<string>();

		if (OperatingSystem.IsAndroidVersionAtLeast(31))
		{
			needed.Add("android.permission.BLUETOOTH_SCAN");
			needed.Add("android.permission.BLUETOOTH_CONNECT");
		}
		else
		{
			// BLE scanning needs fine location on API ≤ 30.
			needed.Add("android.permission.ACCESS_FINE_LOCATION");
		}

		if (OperatingSystem.IsAndroidVersionAtLeast(33))
		{
			needed.Add("android.permission.POST_NOTIFICATIONS");
		}

		var toRequest = needed
			.Where(p => CheckSelfPermission(p) != Permission.Granted)
			.ToArray();

		if (toRequest.Length > 0)
		{
			RequestPermissions(toRequest, PermissionRequestCode);
		}
	}
}
