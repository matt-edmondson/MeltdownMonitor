using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MeltdownMonitor.Mobile.Controls;

/// <summary>
/// Hand-rolled multi-series time chart for the Metrics tab. Renders a primary series
/// and an optional baseline overlay (dashed) on a shared padded auto-fit Y axis and a
/// time X axis (newest at the right). A <see cref="Stairs"/> mode renders the primary
/// series as a step plot (used for the binary sensor-contact strip). Title is drawn
/// top-left. Mirrors the desktop ImPlot charts; the mapping is <see cref="ChartScale"/>.
/// </summary>
public sealed class MetricChart : Control
{
	public static readonly StyledProperty<string?> TitleProperty =
		AvaloniaProperty.Register<MetricChart, string?>(nameof(Title));

	public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
		AvaloniaProperty.Register<MetricChart, IReadOnlyList<double>?>(nameof(Values));

	public static readonly StyledProperty<IReadOnlyList<double>?> BaselineValuesProperty =
		AvaloniaProperty.Register<MetricChart, IReadOnlyList<double>?>(nameof(BaselineValues));

	public static readonly StyledProperty<IReadOnlyList<double>?> TimestampsProperty =
		AvaloniaProperty.Register<MetricChart, IReadOnlyList<double>?>(nameof(Timestamps));

	public static readonly StyledProperty<double> WindowSecondsProperty =
		AvaloniaProperty.Register<MetricChart, double>(nameof(WindowSeconds), 3600.0);

	public static readonly StyledProperty<IBrush> LineBrushProperty =
		AvaloniaProperty.Register<MetricChart, IBrush>(
			nameof(LineBrush), new SolidColorBrush(Color.FromRgb(0x8A, 0xAD, 0xF4)));

	public static readonly StyledProperty<IBrush> BaselineBrushProperty =
		AvaloniaProperty.Register<MetricChart, IBrush>(
			nameof(BaselineBrush), new SolidColorBrush(Color.FromRgb(0x80, 0x87, 0xA2)));

	public static readonly StyledProperty<bool> StairsProperty =
		AvaloniaProperty.Register<MetricChart, bool>(nameof(Stairs));

	public static readonly StyledProperty<double?> YMinProperty =
		AvaloniaProperty.Register<MetricChart, double?>(nameof(YMin));

	public static readonly StyledProperty<double?> YMaxProperty =
		AvaloniaProperty.Register<MetricChart, double?>(nameof(YMax));

	static MetricChart()
	{
		AffectsRender<MetricChart>(
			TitleProperty,
			ValuesProperty,
			BaselineValuesProperty,
			TimestampsProperty,
			WindowSecondsProperty,
			LineBrushProperty,
			BaselineBrushProperty,
			StairsProperty,
			YMinProperty,
			YMaxProperty);
	}

	public string? Title
	{
		get => GetValue(TitleProperty);
		set => SetValue(TitleProperty, value);
	}

	public IReadOnlyList<double>? Values
	{
		get => GetValue(ValuesProperty);
		set => SetValue(ValuesProperty, value);
	}

	public IReadOnlyList<double>? BaselineValues
	{
		get => GetValue(BaselineValuesProperty);
		set => SetValue(BaselineValuesProperty, value);
	}

	public IReadOnlyList<double>? Timestamps
	{
		get => GetValue(TimestampsProperty);
		set => SetValue(TimestampsProperty, value);
	}

	public double WindowSeconds
	{
		get => GetValue(WindowSecondsProperty);
		set => SetValue(WindowSecondsProperty, value);
	}

	public IBrush LineBrush
	{
		get => GetValue(LineBrushProperty);
		set => SetValue(LineBrushProperty, value);
	}

	public IBrush BaselineBrush
	{
		get => GetValue(BaselineBrushProperty);
		set => SetValue(BaselineBrushProperty, value);
	}

	public bool Stairs
	{
		get => GetValue(StairsProperty);
		set => SetValue(StairsProperty, value);
	}

	/// <summary>Forces a fixed Y floor (e.g. 0 for the contact strip); null = auto-fit.</summary>
	public double? YMin
	{
		get => GetValue(YMinProperty);
		set => SetValue(YMinProperty, value);
	}

	/// <summary>Forces a fixed Y ceiling (e.g. 1 for the contact strip); null = auto-fit.</summary>
	public double? YMax
	{
		get => GetValue(YMaxProperty);
		set => SetValue(YMaxProperty, value);
	}

	private static readonly IBrush TitleBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98));
	private const double TitleH = 16.0;

	public override void Render(DrawingContext context)
	{
		double w = Bounds.Width, h = Bounds.Height;
		if (w <= 2 || h <= 2)
		{
			return;
		}

		if (!string.IsNullOrEmpty(Title))
		{
			var ft = new FormattedText(Title!, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
				Typeface.Default, 11, TitleBrush);
			context.DrawText(ft, new Point(2, 0));
		}

		double plotTop = string.IsNullOrEmpty(Title) ? 0 : TitleH;
		double plotH = h - plotTop;
		if (plotH <= 2)
		{
			return;
		}

		var values = Values;
		var baseline = BaselineValues;
		var (autoMin, autoMax) = ChartScale.FitRange(new[] { values, baseline }, padFraction: 0.12);
		double min = YMin ?? autoMin;
		double max = YMax ?? autoMax;
		double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
		var ts = Timestamps;

		if (baseline is { Count: >= 2 })
		{
			Stroke(context, baseline, ts, now, w, plotTop, plotH, min, max,
				new Pen(BaselineBrush, 1.5) { DashStyle = new DashStyle(new[] { 4.0, 4.0 }, 0) }, stairs: false);
		}

		if (values is { Count: >= 2 })
		{
			Stroke(context, values, ts, now, w, plotTop, plotH, min, max, new Pen(LineBrush, 2.0), Stairs);
		}
	}

	private void Stroke(DrawingContext context, IReadOnlyList<double> series, IReadOnlyList<double>? ts,
		double now, double w, double top, double plotH, double min, double max, IPen pen, bool stairs)
	{
		bool timed = ts is not null && ts.Count == series.Count;

		double X(int i) => timed
			? ChartScale.TimeX(ts![i], now, WindowSeconds, w)
			: w * i / Math.Max(1, series.Count - 1);

		double Y(int i) => top + ChartScale.Y(series[i], min, max, plotH);

		var prev = new Point(X(0), Y(0));
		for (int i = 1; i < series.Count; i++)
		{
			var next = new Point(X(i), Y(i));
			if (stairs)
			{
				var corner = new Point(next.X, prev.Y);
				context.DrawLine(pen, prev, corner);
				context.DrawLine(pen, corner, next);
			}
			else
			{
				context.DrawLine(pen, prev, next);
			}

			prev = next;
		}
	}
}
