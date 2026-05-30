namespace MeltdownMonitor.App;

/// <summary>Which corner of the screen work area the overlay locks to.</summary>
public enum OverlayCorner
{
	TopLeft,
	TopRight,
	BottomLeft,
	BottomRight,
}

/// <summary>
/// Overlay-mode configuration, persisted as part of <see cref="AppSettings"/>. When enabled,
/// the whole status window becomes a borderless, translucent, always-on-top overlay locked to
/// a chosen corner with a configurable offset and size.
/// </summary>
public sealed class OverlaySettings
{
	/// <summary>When true the status window is rendered as a transparent, always-on-top overlay.</summary>
	public bool Enabled { get; set; }

	/// <summary>In overlay mode: false shows the compact metrics HUD, true shows the full tabbed UI.</summary>
	public bool Expanded { get; set; }

	/// <summary>
	/// In overlay mode, ignore the mouse so clicks pass through to whatever is behind the
	/// overlay. Toggle it back off from the tray menu (the in-overlay controls are unclickable
	/// while this is on).
	/// </summary>
	public bool ClickThrough { get; set; }

	/// <summary>Whole-window opacity in overlay mode, 0.2 (faint) – 1.0 (opaque).</summary>
	public float Opacity { get; set; } = 0.85f;

	/// <summary>Show the Regulation Field figure-8 at the top of the compact HUD.</summary>
	public bool ShowRegulationField { get; set; } = true;

	/// <summary>The screen corner the overlay locks to.</summary>
	public OverlayCorner Corner { get; set; } = OverlayCorner.TopRight;

	/// <summary>Horizontal inset (px) from the locked corner.</summary>
	public int OffsetX { get; set; } = 24;

	/// <summary>Vertical inset (px) from the locked corner.</summary>
	public int OffsetY { get; set; } = 24;

	/// <summary>Overlay window width in pixels (resizable from the grip).</summary>
	public int Width { get; set; } = 460;

	/// <summary>Overlay window height in pixels (resizable from the grip).</summary>
	public int Height { get; set; } = 500;

	/// <summary>The metrics shown in the compact HUD, in the order they're listed.</summary>
	public List<OverlayMetric> Metrics { get; set; } =
	[
		OverlayMetric.State,
		OverlayMetric.HeartRate,
		OverlayMetric.Rmssd,
		OverlayMetric.RmssdVsBaseline,
		OverlayMetric.LfHfRatio,
	];
}
