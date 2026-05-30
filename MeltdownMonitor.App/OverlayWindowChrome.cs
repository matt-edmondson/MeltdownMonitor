using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MeltdownMonitor.App;

/// <summary>
/// Applies "overlay mode" window styles to the native status-window handle: borderless,
/// always-on-top, and whole-window translucency, with optional click-through. The original
/// styles are cached so normal mode can be restored exactly.
///
/// All calls are best-effort and no-op when the handle is unavailable (e.g. before the
/// window is created). Per-frame calls are cheap: styles are only re-applied when something
/// actually changed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class OverlayWindowChrome
{
	private const int GWL_STYLE = -16;
	private const int GWL_EXSTYLE = -20;

	private const int WS_CAPTION = 0x00C00000;
	private const int WS_THICKFRAME = 0x00040000;
	private const int WS_MINIMIZEBOX = 0x00020000;
	private const int WS_MAXIMIZEBOX = 0x00010000;
	private const int WS_SYSMENU = 0x00080000;

	private const int WS_EX_LAYERED = 0x00080000;
	private const int WS_EX_TRANSPARENT = 0x00000020;

	private const uint LWA_ALPHA = 0x2;

	private static readonly nint HWND_TOPMOST = new(-1);
	private static readonly nint HWND_NOTOPMOST = new(-2);

	private const uint SWP_NOSIZE = 0x0001;
	private const uint SWP_NOMOVE = 0x0002;
	private const uint SWP_NOACTIVATE = 0x0010;
	private const uint SWP_FRAMECHANGED = 0x0020;

	private nint _hwnd;
	private nint _resolvedHandle;
	private bool _applied;
	private int _originalStyle;
	private int _originalExStyle;

	// Last-applied values, so per-frame calls only touch Win32 on a real change.
	private bool _lastClickThrough;
	private byte _lastAlpha;
	private (OverlayCorner Corner, int OffsetX, int OffsetY, int Width, int Height) _lastGeometry;
	private bool _geometryApplied;

	/// <summary>True once overlay styles have been applied (and not yet restored).</summary>
	public bool IsApplied => _applied;

	/// <summary>
	/// Switches the window into overlay mode (borderless, topmost, layered) and keeps its
	/// click-through and opacity in sync. Safe to call every frame; must be called from the
	/// thread that owns the render window so the native handle can be resolved.
	/// </summary>
	public void Apply(bool clickThrough, float opacity)
	{
		nint hwnd = ResolveHandle();
		if (hwnd == 0)
		{
			return;
		}

		byte alpha = (byte)Math.Clamp(opacity * 255f, 51f, 255f);

		if (!_applied || _hwnd != hwnd)
		{
			_hwnd = hwnd;
			_originalStyle = GetWindowLong(hwnd, GWL_STYLE);
			_originalExStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

			int style = _originalStyle & ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
			_ = SetWindowLong(hwnd, GWL_STYLE, style);
			_ = SetWindowLong(hwnd, GWL_EXSTYLE, _originalExStyle | WS_EX_LAYERED);
			_ = SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);

			_applied = true;
			// Force the click-through and alpha branches below to run for the fresh window.
			_lastClickThrough = !clickThrough;
			_lastAlpha = 0;
		}

		if (clickThrough != _lastClickThrough)
		{
			int ex = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_LAYERED;
			ex = clickThrough ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
			_ = SetWindowLong(hwnd, GWL_EXSTYLE, ex);
			_lastClickThrough = clickThrough;
		}

		if (alpha != _lastAlpha)
		{
			_ = SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
			_lastAlpha = alpha;
		}
	}

	/// <summary>Restores the original decorated, non-topmost, opaque window. Safe to call repeatedly.</summary>
	public void Restore()
	{
		if (!_applied || _hwnd == 0)
		{
			return;
		}

		_ = SetWindowLong(_hwnd, GWL_STYLE, _originalStyle);
		_ = SetWindowLong(_hwnd, GWL_EXSTYLE, _originalExStyle);
		_ = SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);

		_applied = false;
		_geometryApplied = false;
		_lastAlpha = 0;
		_lastClickThrough = false;
	}

	/// <summary>
	/// Locks the overlay to the given corner of its monitor's work area at the given offset
	/// and size. Re-applies only when something changed (or on the first call after entering
	/// overlay mode). The explicit resize also forces the renderer to refresh its framebuffer
	/// after the title bar was removed — without it the top of the content can be clipped.
	/// </summary>
	public void ApplyGeometry(OverlayCorner corner, int offsetX, int offsetY, int width, int height)
	{
		if (!_applied || _hwnd == 0)
		{
			return;
		}

		width = Math.Max(200, width);
		height = Math.Max(140, height);

		var geometry = (corner, offsetX, offsetY, width, height);
		if (_geometryApplied && geometry == _lastGeometry)
		{
			return;
		}

		if (!TryGetWorkArea(_hwnd, out RECT work))
		{
			return;
		}

		bool right = corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
		bool bottom = corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight;

		int x = right ? work.Right - width - offsetX : work.Left + offsetX;
		int y = bottom ? work.Bottom - height - offsetY : work.Top + offsetY;

		_ = SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);
		_lastGeometry = geometry;
		_geometryApplied = true;
	}

	private static bool TryGetWorkArea(nint hwnd, out RECT work)
	{
		nint monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
		var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
		if (monitor != 0 && GetMonitorInfo(monitor, ref info))
		{
			work = info.rcWork;
			return true;
		}

		work = default;
		return false;
	}

	// ktsu.ImGui.App doesn't expose its native handle publicly, so we find it ourselves:
	// the render window is created (by GLFW/Silk) on the same thread that calls OnRender, so
	// the first visible top-level window owned by this thread is ours. Cached after the first hit.
	private nint ResolveHandle()
	{
		if (_resolvedHandle != 0)
		{
			return _resolvedHandle;
		}

		nint found = 0;
		bool Callback(nint hWnd, nint lParam)
		{
			if (IsWindowVisible(hWnd) && GetParent(hWnd) == 0)
			{
				found = hWnd;
				return false; // stop enumerating
			}

			return true;
		}

		_ = EnumThreadWindows(GetCurrentThreadId(), Callback, 0);
		_resolvedHandle = found;
		return found;
	}

	private delegate bool EnumThreadWindowsProc(nint hWnd, nint lParam);

	private const uint MONITOR_DEFAULTTONEAREST = 2;

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MONITORINFO
	{
		public int cbSize;
		public RECT rcMonitor;
		public RECT rcWork;
		public uint dwFlags;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern int GetWindowLong(nint hWnd, int nIndex);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

	[DllImport("user32.dll")]
	private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWindowVisible(nint hWnd);

	[DllImport("user32.dll")]
	private static extern nint GetParent(nint hWnd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumThreadWindows(uint dwThreadId, EnumThreadWindowsProc lpfn, nint lParam);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();
}
