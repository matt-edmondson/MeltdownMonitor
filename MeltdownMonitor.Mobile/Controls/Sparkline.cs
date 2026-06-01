using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MeltdownMonitor.Mobile.Controls;

/// <summary>
/// Lightweight live sparkline. Renders a primary series (RMSSD) and an
/// optional baseline series across the same Y-axis, auto-scaled to the
/// observed maximum. Backed by Avalonia's <see cref="DrawingContext"/>,
/// which targets Skia under the hood — fine for the 60 s × ~12 sample
/// window the design doc calls for.
/// </summary>
public sealed class Sparkline : Control
{
	public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
		AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

	public static readonly StyledProperty<IReadOnlyList<double>?> BaselineValuesProperty =
		AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(BaselineValues));

	public static readonly StyledProperty<IBrush> LineBrushProperty =
		AvaloniaProperty.Register<Sparkline, IBrush>(
			nameof(LineBrush), new SolidColorBrush(Color.FromRgb(0x29, 0x80, 0xD8)));

	public static readonly StyledProperty<IBrush> BaselineBrushProperty =
		AvaloniaProperty.Register<Sparkline, IBrush>(
			nameof(BaselineBrush), new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)));

	public static readonly StyledProperty<double> LineThicknessProperty =
		AvaloniaProperty.Register<Sparkline, double>(nameof(LineThickness), 2.0);

	public static readonly StyledProperty<IReadOnlyList<double>?> TimestampsProperty =
		AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Timestamps));

	public static readonly StyledProperty<double> WindowSecondsProperty =
		AvaloniaProperty.Register<Sparkline, double>(nameof(WindowSeconds), 60.0);

	static Sparkline()
	{
		AffectsRender<Sparkline>(
			ValuesProperty,
			BaselineValuesProperty,
			TimestampsProperty,
			WindowSecondsProperty,
			LineBrushProperty,
			BaselineBrushProperty,
			LineThicknessProperty);
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

	public double LineThickness
	{
		get => GetValue(LineThicknessProperty);
		set => SetValue(LineThicknessProperty, value);
	}

	/// <summary>Unix epoch seconds, one per <see cref="Values"/> / <see cref="BaselineValues"/>
	/// point. When present (and length-matched) the series is spaced by real time within
	/// <see cref="WindowSeconds"/>; otherwise points fall back to even index spacing.</summary>
	public IReadOnlyList<double>? Timestamps
	{
		get => GetValue(TimestampsProperty);
		set => SetValue(TimestampsProperty, value);
	}

	/// <summary>Width of the time window (seconds) shown across the control. Default 60.</summary>
	public double WindowSeconds
	{
		get => GetValue(WindowSecondsProperty);
		set => SetValue(WindowSecondsProperty, value);
	}

	public override void Render(DrawingContext context)
	{
		var bounds = new Rect(Bounds.Size);
		if (bounds.Width <= 0 || bounds.Height <= 0)
		{
			return;
		}

		var values = Values;
		var baseline = BaselineValues;
		var timestamps = Timestamps;
		double window = Math.Max(1.0, WindowSeconds);
		double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

		double max = 0;
		if (values is not null)
		{
			foreach (double v in values)
			{
				if (v > max) max = v;
			}
		}

		if (baseline is not null)
		{
			foreach (double v in baseline)
			{
				if (v > max) max = v;
			}
		}

		if (max <= 0)
		{
			max = 100;
		}

		// Headroom so peaks don't kiss the top edge.
		max *= 1.15;

		// NowViewModel appends and trims Values, BaselineValues, and Timestamps together,
		// so in steady state all three are equal-length and share one time-relative x-scale.
		// If a caller ever desyncs them, DrawSeries safely falls back to index spacing per series.
		if (baseline is not null && baseline.Count >= 2)
		{
			DrawSeries(context, baseline, timestamps, now, window, bounds, max, BaselineBrush, LineThickness, dashed: true);
		}

		if (values is not null && values.Count >= 2)
		{
			DrawSeries(context, values, timestamps, now, window, bounds, max, LineBrush, LineThickness, dashed: false);
		}
	}

	private static void DrawSeries(
		DrawingContext context,
		IReadOnlyList<double> series,
		IReadOnlyList<double>? timestamps,
		double now,
		double window,
		Rect bounds,
		double max,
		IBrush brush,
		double thickness,
		bool dashed)
	{
		var pen = dashed
			? new Pen(brush, thickness) { DashStyle = new DashStyle(new[] { 4.0, 4.0 }, 0) }
			: new Pen(brush, thickness);

		// Time-relative x when timestamps line up with the series; otherwise fall back to
		// even index spacing so the control still renders if a caller omits timestamps.
		bool timed = timestamps is not null && timestamps.Count == series.Count;

		double XAt(int i)
		{
			if (timed)
			{
				double age = now - timestamps![i];                 // seconds before now
				double frac = 1.0 - Math.Clamp(age / window, 0.0, 1.0);
				return frac * bounds.Width;                          // newest at the right edge
			}

			return bounds.Width * i / Math.Max(1, series.Count - 1);
		}

		var prev = new Point(XAt(0), ToY(series[0], max, bounds.Height));
		for (int i = 1; i < series.Count; i++)
		{
			var next = new Point(XAt(i), ToY(series[i], max, bounds.Height));
			context.DrawLine(pen, prev, next);
			prev = next;
		}
	}

	private static double ToY(double value, double max, double height)
	{
		double clamped = Math.Clamp(value, 0, max);
		return height - (clamped / max * height);
	}
}
