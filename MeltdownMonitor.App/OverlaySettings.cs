namespace MeltdownMonitor.App;

/// <summary>
/// Overlay-mode configuration, persisted as part of <see cref="AppSettings"/>. When enabled,
/// the whole status window becomes a borderless, translucent, always-on-top overlay.
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
