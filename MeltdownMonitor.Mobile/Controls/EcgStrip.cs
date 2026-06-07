using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MeltdownMonitor.Mobile.Controls;

/// <summary>
/// Live ECG strip: draws the raw microvolt samples across the width (oldest left, newest right),
/// auto-scaled to the window's amplitude, with a dot on each detected R-peak. Backed by Avalonia's
/// <see cref="DrawingContext"/> (Skia), like <see cref="Sparkline"/>. Purely a signal view — not a
/// diagnostic ECG.
/// </summary>
public sealed class EcgStrip : Control
{
	public static readonly StyledProperty<IReadOnlyList<double>?> SamplesProperty =
		AvaloniaProperty.Register<EcgStrip, IReadOnlyList<double>?>(nameof(Samples));

	public static readonly StyledProperty<IReadOnlyList<int>?> RPeakIndicesProperty =
		AvaloniaProperty.Register<EcgStrip, IReadOnlyList<int>?>(nameof(RPeakIndices));

	public static readonly StyledProperty<IBrush> LineBrushProperty =
		AvaloniaProperty.Register<EcgStrip, IBrush>(
			nameof(LineBrush), new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C)));

	public static readonly StyledProperty<IBrush> PeakBrushProperty =
		AvaloniaProperty.Register<EcgStrip, IBrush>(
			nameof(PeakBrush), new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x7B)));

	public static readonly StyledProperty<IBrush> BaselineBrushProperty =
		AvaloniaProperty.Register<EcgStrip, IBrush>(
			nameof(BaselineBrush), new SolidColorBrush(Color.FromRgb(0x33, 0x38, 0x40)));

	public static readonly StyledProperty<double> LineThicknessProperty =
		AvaloniaProperty.Register<EcgStrip, double>(nameof(LineThickness), 1.5);

	static EcgStrip()
	{
		AffectsRender<EcgStrip>(
			SamplesProperty,
			RPeakIndicesProperty,
			LineBrushProperty,
			PeakBrushProperty,
			BaselineBrushProperty,
			LineThicknessProperty);
	}

	public IReadOnlyList<double>? Samples
	{
		get => GetValue(SamplesProperty);
		set => SetValue(SamplesProperty, value);
	}

	public IReadOnlyList<int>? RPeakIndices
	{
		get => GetValue(RPeakIndicesProperty);
		set => SetValue(RPeakIndicesProperty, value);
	}

	public IBrush LineBrush
	{
		get => GetValue(LineBrushProperty);
		set => SetValue(LineBrushProperty, value);
	}

	public IBrush PeakBrush
	{
		get => GetValue(PeakBrushProperty);
		set => SetValue(PeakBrushProperty, value);
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
		var baselinePen = new Pen(BaselineBrush, 1.0);
		double midY = bounds.Height / 2.0;
		context.DrawLine(baselinePen, new Point(0, midY), new Point(bounds.Width, midY));

		IReadOnlyList<double>? samples = Samples;
		if (samples is null || samples.Count < 2 || bounds.Width <= 0 || bounds.Height <= 0)
		{
			return;
		}

		double min = samples[0];
		double max = samples[0];
		for (int i = 1; i < samples.Count; i++)
		{
			if (samples[i] < min) { min = samples[i]; }
			if (samples[i] > max) { max = samples[i]; }
		}

		double range = max - min;
		if (range < 1.0)
		{
			range = 1.0; // avoid divide-by-zero on a flat trace
		}

		// Leave a small vertical margin so the trace never clips the edges.
		double margin = bounds.Height * 0.08;
		double plotHeight = bounds.Height - (2 * margin);

		double XAt(int i) => bounds.Width * i / (samples.Count - 1);
		double YAt(double v) => margin + (plotHeight * (1.0 - ((v - min) / range)));

		var pen = new Pen(LineBrush, LineThickness, lineJoin: PenLineJoin.Round);
		var prev = new Point(XAt(0), YAt(samples[0]));
		for (int i = 1; i < samples.Count; i++)
		{
			var next = new Point(XAt(i), YAt(samples[i]));
			context.DrawLine(pen, prev, next);
			prev = next;
		}

		IReadOnlyList<int>? peaks = RPeakIndices;
		if (peaks is not null)
		{
			foreach (int index in peaks)
			{
				if (index >= 0 && index < samples.Count)
				{
					context.DrawEllipse(PeakBrush, null, new Point(XAt(index), YAt(samples[index])), 3.0, 3.0);
				}
			}
		}
	}
}
