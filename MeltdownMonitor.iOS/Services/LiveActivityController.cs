using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.iOS.Services;

/// <summary>
/// iOS implementation of <see cref="ILiveActivityController"/> (design doc
/// Phase 8). ActivityKit has no managed binding, so the actual
/// <c>Activity&lt;…&gt;.request/update/end</c> calls live in a small Swift
/// shim (<c>LiveActivity/LiveActivityBridge.swift</c>) that exports plain C
/// entry points via <c>@_cdecl</c>.
///
/// Those entry points belong to a native Xcode-managed target that the .NET
/// build does not compile (see <c>docs/live-activity.md</c>). A static
/// <c>[DllImport("__Internal")]</c> would force the app to fail at *link* time
/// while the bridge is absent, so instead the symbols are resolved lazily with
/// <c>dlsym</c>: the binary always links, the controller no-ops when the bridge
/// isn't present, and it lights up automatically once the Swift file is added
/// to the app target.
/// </summary>
[SupportedOSPlatform("ios16.2")]
public sealed class LiveActivityController : ILiveActivityController
{
	// dlfcn.h: #define RTLD_DEFAULT ((void *) -2) — search every loaded image.
	private static readonly IntPtr RtldDefault = new(-2);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void ContentFn(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string label,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string colorHex,
		int heartRate,
		double rmssdRatio,
		[MarshalAs(UnmanagedType.I1)] bool isPaused);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void VoidFn();

	private readonly ContentFn? _start;
	private readonly ContentFn? _update;
	private readonly VoidFn? _end;

	public LiveActivityController()
	{
		_start = Resolve<ContentFn>("mm_live_activity_start");
		_update = Resolve<ContentFn>("mm_live_activity_update");
		_end = Resolve<VoidFn>("mm_live_activity_end");
	}

	/// <summary>True when the native ActivityKit bridge is linked into this build.</summary>
	private bool BridgePresent => _start is not null && _update is not null && _end is not null;

	public bool IsActive { get; private set; }

	public Task StartAsync(LiveActivityContent content)
	{
		if (BridgePresent && Invoke(() => _start!(
			content.StateLabel, content.ColorHex, content.HeartRate, content.RmssdRatio, content.IsPaused)))
		{
			IsActive = true;
		}

		return Task.CompletedTask;
	}

	public Task UpdateAsync(LiveActivityContent content)
	{
		if (!IsActive)
		{
			return StartAsync(content);
		}

		Invoke(() => _update!(
			content.StateLabel, content.ColorHex, content.HeartRate, content.RmssdRatio, content.IsPaused));
		return Task.CompletedTask;
	}

	public Task EndAsync()
	{
		if (IsActive)
		{
			Invoke(() => _end!());
			IsActive = false;
		}

		return Task.CompletedTask;
	}

	/// <summary>
	/// Runs a native call, swallowing transient ActivityKit failures (e.g. the
	/// user disabled Live Activities in Settings) so a missed update never
	/// crashes the app or stalls the pipeline. Returns true when it ran.
	/// </summary>
	private static bool Invoke(Action call)
	{
		try
		{
			call();
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static TDelegate? Resolve<TDelegate>(string symbol)
		where TDelegate : Delegate
	{
		IntPtr ptr = dlsym(RtldDefault, symbol);
		return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<TDelegate>(ptr);
	}

	[DllImport("/usr/lib/libSystem.dylib", EntryPoint = "dlsym")]
	private static extern IntPtr dlsym(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string symbol);
}
