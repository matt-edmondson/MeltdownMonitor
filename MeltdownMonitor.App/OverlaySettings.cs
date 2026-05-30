namespace MeltdownMonitor.App;

/// <summary>Which corner of the window the heads-up overlay pins to.</summary>
public enum OverlayCorner
{
	TopLeft,
	TopRight,
	BottomLeft,
	BottomRight,
}

/// <summary>Heads-up metrics overlay configuration, persisted as part of <see cref="AppSettings"/>.</summary>
public sealed class OverlaySettings
{
	/// <summary>Whether the transparent overlay is drawn on top of the status window.</summary>
	public bool Enabled { get; set; }

	/// <summary>The corner the overlay pins to.</summary>
	public OverlayCorner Corner { get; set; } = OverlayCorner.TopRight;

	/// <summary>Background opacity, 0 (fully transparent) – 1 (opaque).</summary>
	public float BackgroundAlpha { get; set; } = 0.35f;

	/// <summary>
	/// When true the overlay ignores the mouse so clicks pass through to the charts beneath.
	/// The right-click metric menu is unavailable while this is on.
	/// </summary>
	public bool ClickThrough { get; set; }

	/// <summary>The metrics shown, in the order they're listed.</summary>
	public List<OverlayMetric> Metrics { get; set; } =
	[
		OverlayMetric.State,
		OverlayMetric.HeartRate,
		OverlayMetric.Rmssd,
		OverlayMetric.RmssdVsBaseline,
		OverlayMetric.LfHfRatio,
	];
}
