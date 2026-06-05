using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using AndroidX.Activity.Result.Contract;
using AndroidX.Health.Connect.Client;
using Avalonia.Android;
using MeltdownMonitor.Android.Services;

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
///
/// <para>
/// This activity also owns the Health Connect permission launcher (design doc
/// §5.3 / Phase 4). Health Connect grants come from its own permission screen,
/// reachable only from a live Activity, so while foregrounded the activity
/// installs <see cref="HealthConnectPermissions.Launcher"/> and drives it through
/// the classic <see cref="Activity.StartActivityForResult(Intent, int)"/> /
/// <see cref="OnActivityResult"/> pair — Avalonia's activity is a plain
/// <c>Android.App.Activity</c>, not an AndroidX <c>ComponentActivity</c>, so the
/// modern <c>registerForActivityResult</c> seam is not available and the classic
/// path is the reliable one.
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
	private const int HealthPermissionRequestCode = 4712;

	// Set while a Health Connect permission screen is on screen. The contract
	// instance must survive between CreateIntent and ParseResult, so it is held
	// alongside the completion source that RequestHealthPermissionsAsync awaits.
	private TaskCompletionSource<bool>? _healthPermissionRequest;
	private ActivityResultContract? _healthPermissionContract;

	// Stable delegate reference so OnPause can clear exactly the launcher it set.
	private Func<IReadOnlyCollection<string>, Task<bool>>? _healthLauncher;

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);

		// Sequence the runtime asks behind the in-app disclaimer in a later pass
		// (design doc §5.2); for now request the monitoring permissions up front so
		// a fresh install can connect. Already-granted permissions are a no-op.
		RequestMonitoringPermissions();
	}

	protected override void OnResume()
	{
		base.OnResume();

		// Expose the Health Connect permission launcher only while foregrounded:
		// StartActivityForResult against a backgrounded/destroyed Activity throws,
		// and the only caller (Settings → "Connect Health") needs the UI up anyway.
		_healthLauncher ??= RequestHealthPermissionsAsync;
		HealthConnectPermissions.Launcher = _healthLauncher;
	}

	protected override void OnPause()
	{
		// Clear only the launcher this activity installed, so a freshly-created
		// activity that already re-installed its own is left untouched.
		if (ReferenceEquals(HealthConnectPermissions.Launcher, _healthLauncher))
		{
			HealthConnectPermissions.Launcher = null;
		}

		base.OnPause();
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

	/// <summary>
	/// Launches Health Connect's permission screen for the requested permissions and
	/// completes when it is dismissed (design doc §5.3 / Phase 4). The launch is
	/// marshalled onto the UI thread because the caller (<c>HealthConnectStore</c>)
	/// awaits the grant re-check on a background thread, and
	/// <see cref="Activity.StartActivityForResult(Intent, int)"/> must run on the UI
	/// thread. A request already on screen is not stacked.
	/// </summary>
	private Task<bool> RequestHealthPermissionsAsync(IReadOnlyCollection<string> permissions)
	{
		var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

		RunOnUiThread(() =>
		{
			if (_healthPermissionRequest is { Task.IsCompleted: false })
			{
				// A permission screen is already up; don't launch a second.
				tcs.TrySetResult(false);
				return;
			}

			try
			{
				// The Health Connect contract takes a Kotlin Set<String> of permission
				// strings and returns the granted Set<String>. Build the input as a Java
				// HashSet of Java strings; the binding marshals it to the Kotlin set.
				var contract = PermissionController.CreateRequestPermissionResultContract();
				var input = new Java.Util.HashSet();
				foreach (var permission in permissions)
				{
					input.Add(new Java.Lang.String(permission));
				}

				_healthPermissionContract = contract;
				_healthPermissionRequest = tcs;

				var intent = contract.CreateIntent(this, input);
				StartActivityForResult(intent, HealthPermissionRequestCode);
			}
			catch (Java.Lang.Exception)
			{
				// No Health Connect provider to resolve the intent, or it refused to
				// launch — degrade to "not granted"; the store re-reads the real state.
				_healthPermissionRequest = null;
				_healthPermissionContract = null;
				tcs.TrySetResult(false);
			}
		});

		return tcs.Task;
	}

	protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
	{
		base.OnActivityResult(requestCode, resultCode, data);

		if (requestCode != HealthPermissionRequestCode)
		{
			return;
		}

		var tcs = _healthPermissionRequest;
		var contract = _healthPermissionContract;
		_healthPermissionRequest = null;
		_healthPermissionContract = null;

		if (tcs is null)
		{
			return;
		}

		try
		{
			// Parse the granted Set<String> off the same contract instance. The result is
			// advisory — the store re-reads the authoritative grant — so a coarse "anything
			// granted" answer is enough. A null/uncastable result degrades to false.
			var result = contract?.ParseResult((int)resultCode, data);
			var granted = result?.JavaCast<Java.Util.ICollection>();
			tcs.TrySetResult(granted is { IsEmpty: false });
		}
		catch (Java.Lang.Exception)
		{
			tcs.TrySetResult(false);
		}
		catch (InvalidCastException)
		{
			tcs.TrySetResult(false);
		}
	}
}
