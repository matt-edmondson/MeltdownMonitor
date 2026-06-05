using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;

namespace MeltdownMonitor.Android;

/// <summary>
/// Single-Activity Avalonia host for the Android head (design doc §5 / §13
/// Phase 1). In Avalonia 12 the app-builder wiring lives in the
/// <see cref="MainApplication"/> (an <c>Application</c> subclass), so this
/// activity is just the launcher surface — <see cref="AvaloniaMainActivity"/>
/// hosts the shared single-view UI the app runs on iOS.
///
/// <para>
/// <c>LaunchMode.SingleTask</c> is the Android analog of the iOS single-instance
/// guarantee — a launch while already running re-uses this Activity rather than
/// stacking a second copy. The pipeline is application-scoped on
/// <see cref="AndroidCompositionRoot"/> and kept alive by the foreground
/// <see cref="MonitoringService"/>, so a config change that recreates this
/// Activity rebinds to the running pipeline instead of restarting it (§5.8).
/// </para>
/// </summary>
[Activity(
	Label = "Meltdown Monitor",
	Theme = "@style/MyTheme.NoActionBar",
	MainLauncher = true,
	LaunchMode = LaunchMode.SingleTask,
	ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
	private const int PermissionRequestCode = 4711;

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
