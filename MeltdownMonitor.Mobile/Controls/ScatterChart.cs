using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MeltdownMonitor.Mobile.Controls;

/// <summary>Pure helpers for the Poincaré scatter (testable without a surface).</summary>
public static class ScatterSeries
{
	/// <summary>Consecutive RR pairs (RR[i], RR[i+1]). Empty when fewer than two samples.</summary>
	public static (double[] Xs, double[] Ys) ConsecutivePairs(IReadOnlyList<double> rr)
	{
		if (rr.Count < 2)
		{
			return ([], []);
		}

		int n = rr.Count - 1;
		var xs = new double[n];
		var ys = new double[n];
		for (int i = 0; i < n; i++)
		{
			xs[i] = rr[i];
			ys[i] = rr[i + 1];
		}

		return (xs, ys);
	}
}

/// <summary>
/// Poincaré plot: RR[i] (x) vs RR[i+1] (y), square equal axes with a faint identity
/// line so the cloud reads at 45°. Mirrors the desktop ImPlot scatter.
/// </summary>
public sealed class ScatterChart : Control
{
	public static readonly StyledProperty<string?> TitleProperty =
		AvaloniaProperty.Register<ScatterChart, string?>(nameof(Title));

	public static readonly StyledProperty<IReadOnlyList<double>?> RrIntervalsProperty =
		AvaloniaProperty.Register<ScatterChart, IReadOnlyList<double>?>(nameof(RrIntervals));

	static ScatterChart()
	{
		AffectsRender<ScatterChart>(TitleProperty, RrIntervalsProperty);
	}

	public string? Title
	{
		get => GetValue(TitleProperty);
		set => SetValue(TitleProperty, value);
	}

	public IReadOnlyList<double>? RrIntervals
	{
		get => GetValue(RrIntervalsProperty);
		set => SetValue(RrIntervalsProperty, value);
	}

	private static readonly IBrush TitleBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98));
	private static readonly IBrush PointBrush = new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0xFF), 0.85);
	private static readonly IPen IdentityPen = new Pen(new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C), 0.40), 1);

	public override void Render(DrawingContext context)
	{
		double w = Bounds.Width;
		double h = Bounds.Height;
		if (w <= 2 || h <= 2)
		{
			return;
		}

		if (!string.IsNullOrEmpty(Title))
		{
			var ft = new FormattedText(Title!, CultureInfo.CurrentCulture,
				FlowDirection.LeftToRight, Typeface.Default, 11, TitleBrush);
			context.DrawText(ft, new Point(2, 0));
		}

		double top = string.IsNullOrEmpty(Title) ? 0 : 16;
		double side = Math.Min(w, h - top);
		if (side <= 2)
		{
			return;
		}

		double offX = (w - side) / 2;

		var rr = RrIntervals;
		var (xs, ys) = ScatterSeries.ConsecutivePairs(rr ?? []);
		if (xs.Length < 1)
		{
			return;
		}

		double min = double.PositiveInfinity;
		double max = double.NegativeInfinity;
		foreach (double v in rr!)
		{
			if (v < min)
			{
				min = v;
			}

			if (v > max)
			{
				max = v;
			}
		}

		double range = max - min;
		if (range <= 0)
		{
			min -= 50;
			max += 50;
			range = max - min;
		}

		Point Map(double x, double y) => new(
			offX + ((x - min) / range * side),
			top + (side - ((y - min) / range * side)));

		context.DrawLine(IdentityPen, Map(min, min), Map(max, max));
		for (int i = 0; i < xs.Length; i++)
		{
			context.DrawEllipse(PointBrush, null, Map(xs[i], ys[i]), 2.0, 2.0);
		}
	}
}
