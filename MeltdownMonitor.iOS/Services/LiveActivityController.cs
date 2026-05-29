using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.iOS.Services;

/// <summary>
/// iOS implementation of <see cref="ILiveActivityController"/> (design doc
/// Phase 8). ActivityKit has no managed binding, so the actual
/// <c>Activity&lt;…&gt;.request/update/end</c> calls live in a small Swift
/// shim (<c>LiveActivity/LiveActivityBridge.swift</c>) that exports plain C
/// entry points via <c>@_cdecl</c>; this class P/Invokes them.
///
/// The bridge is part of a native Xcode-managed target that the .NET build
/// does not compile (see <c>docs/live-activity.md</c>). Until that target is
/// linked into the app binary the <c>__Internal</c> symbols are absent, so
/// every call is guarded: the first missing-symbol failure disables the
/// controller permanently and the app keeps running without a Live Activity.
/// </summary>
[SupportedOSPlatform("ios16.2")]
public sealed class LiveActivityController : ILiveActivityController
{
	private bool _disabled;

	public bool IsActive { get; private set; }

	public Task StartAsync(LiveActivityContent content)
	{
		if (Guarded(() => NativeBridge.Start(
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

		Guarded(() => NativeBridge.Update(
			content.StateLabel, content.ColorHex, content.HeartRate, content.RmssdRatio, content.IsPaused));
		return Task.CompletedTask;
	}

	public Task EndAsync()
	{
		if (IsActive)
		{
			Guarded(NativeBridge.End);
			IsActive = false;
		}

		return Task.CompletedTask;
	}

	/// <summary>
	/// Runs a native call, swallowing the failure modes that mean the
	/// ActivityKit bridge isn't present (symbol missing, library not linked)
	/// and disabling further attempts. Returns true when the call ran.
	/// </summary>
	private bool Guarded(Action call)
	{
		if (_disabled)
		{
			return false;
		}

		try
		{
			call();
			return true;
		}
		catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
		{
			// Native widget target not wired into this build — degrade to no-op.
			_disabled = true;
			return false;
		}
		catch
		{
			// A transient ActivityKit failure (e.g. user disabled Live Activities
			// in Settings) should not crash the app or keep retrying noisily.
			return false;
		}
	}

	private static class NativeBridge
	{
		[DllImport("__Internal", EntryPoint = "mm_live_activity_start")]
		public static extern void Start(
			[MarshalAs(UnmanagedType.LPUTF8Str)] string label,
			[MarshalAs(UnmanagedType.LPUTF8Str)] string colorHex,
			int heartRate,
			double rmssdRatio,
			[MarshalAs(UnmanagedType.I1)] bool isPaused);

		[DllImport("__Internal", EntryPoint = "mm_live_activity_update")]
		public static extern void Update(
			[MarshalAs(UnmanagedType.LPUTF8Str)] string label,
			[MarshalAs(UnmanagedType.LPUTF8Str)] string colorHex,
			int heartRate,
			double rmssdRatio,
			[MarshalAs(UnmanagedType.I1)] bool isPaused);

		[DllImport("__Internal", EntryPoint = "mm_live_activity_end")]
		public static extern void End();
	}
}
