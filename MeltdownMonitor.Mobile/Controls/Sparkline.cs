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

	static Sparkline()
	{
		AffectsRender<Sparkline>(
			ValuesProperty,
			BaselineValuesProperty,
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

	public override void Render(DrawingContext context)
	{
		var bounds = new Rect(Bounds.Size);
		if (bounds.Width <= 0 || bounds.Height <= 0)
		{
			return;
		}

		var values = Values;
		var baseline = BaselineValues;

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

		if (baseline is not null && baseline.Count >= 2)
		{
			DrawSeries(context, baseline, bounds, max, BaselineBrush, LineThickness, dashed: true);
		}

		if (values is not null && values.Count >= 2)
		{
			DrawSeries(context, values, bounds, max, LineBrush, LineThickness, dashed: false);
		}
	}

	private static void DrawSeries(
		DrawingContext context,
		IReadOnlyList<double> series,
		Rect bounds,
		double max,
		IBrush brush,
		double thickness,
		bool dashed)
	{
		var pen = dashed
			? new Pen(brush, thickness) { DashStyle = new DashStyle(new[] { 4.0, 4.0 }, 0) }
			: new Pen(brush, thickness);

		double xStep = bounds.Width / Math.Max(1, series.Count - 1);
		var prev = new Point(0, ToY(series[0], max, bounds.Height));

		for (int i = 1; i < series.Count; i++)
		{
			var next = new Point(i * xStep, ToY(series[i], max, bounds.Height));
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
