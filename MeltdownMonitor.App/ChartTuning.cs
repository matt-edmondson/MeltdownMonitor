namespace MeltdownMonitor.App;

/// <summary>Status-window chart layout preferences (applied live).</summary>
public record ChartTuning
{
	/// <summary>Height of each chart in pixels.</summary>
	public float PlotHeight { get; init; } = 256f;

	/// <summary>Target width of each Overview grid cell in pixels.</summary>
	public float OverviewChartWidth { get; init; } = 700f;

	/// <summary>Maximum width:height ratio before a chart stops widening.</summary>
	public float MaxPlotAspect { get; init; } = 4.0f;

	/// <summary>Maximum size of the square Poincaré scatter in pixels.</summary>
	public float PoincareMaxSide { get; init; } = 512f;
}
