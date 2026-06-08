using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Mobile.Controls;

/// <summary>
/// "Stacked beats" ECG view: re-slices the live trace into one-beat-wide cardiac cycles and overlays
/// them — the live beat brightest, older beats fading with a diminishing alpha. Each beat is shifted
/// horizontally by how far its RR sits from the eased reference cadence, so the fading stack shows how
/// early or late each beat arrived relative to the others (a time-domain view of beat-to-beat
/// variability). The reference cadence eases smoothly, holding the centre steady so the timing scatter
/// is readable without jumps. Each beat is stroked as a single geometry (not per-segment lines) so the
/// overlay stays cheap at 60 fps. Backed by Avalonia's <see cref="DrawingContext"/> (Skia). Purely a
/// signal view — not a diagnostic ECG.
/// </summary>
public sealed class EcgStrip : Control
{
	// 60 fps render loop, tied to the visual-tree lifecycle so it stops when the tab is off-screen.
	private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

	private const double DefaultEaseRate = 3.0;

	// Alpha schedule for the fading stack: the newest completed beat starts here and each older beat is
	// multiplied down by the falloff, never dropping below the floor (so the oldest still ghosts in).
	private const double NewestBeatAlpha = 0.70;
	private const double BeatAlphaFalloff = 0.72;
	private const double MinBeatAlpha = 0.06;

	private readonly Stopwatch _clock = new();
	private TimeSpan _lastFrame;
	private DispatcherTimer? _timer;

	private double _easedMin;
	private double _easedMax;
	private double _easedReferenceRr;
	private bool _easeInitialised;

	public static readonly StyledProperty<EcgBeatOverlay?> OverlayProperty =
		AvaloniaProperty.Register<EcgStrip, EcgBeatOverlay?>(nameof(Overlay));

	public static readonly StyledProperty<double> AnchorEaseRateProperty =
		AvaloniaProperty.Register<EcgStrip, double>(nameof(AnchorEaseRate), DefaultEaseRate);

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

	static EcgStrip() =>
		AffectsRender<EcgStrip>(
			OverlayProperty,
			LineBrushProperty,
			PeakBrushProperty,
			BaselineBrushProperty,
			LineThicknessProperty);

	/// <summary>The R-peak-aligned stack of recent beats to overlay.</summary>
	public EcgBeatOverlay? Overlay
	{
		get => GetValue(OverlayProperty);
		set => SetValue(OverlayProperty, value);
	}

	/// <summary>Exponential-ease rate (per second) the view settles at — lower is slower and smoother.
	/// User-tunable via the Settings slider. Governs the vertical scale and the window-width easing.</summary>
	public double AnchorEaseRate
	{
		get => GetValue(AnchorEaseRateProperty);
		set => SetValue(AnchorEaseRateProperty, value);
	}

	/// <summary>Colour of the trace (the live beat at full strength; older beats fade from it).</summary>
	public IBrush LineBrush
	{
		get => GetValue(LineBrushProperty);
		set => SetValue(LineBrushProperty, value);
	}

	/// <summary>Colour of the centre R-peak guide.</summary>
	public IBrush PeakBrush
	{
		get => GetValue(PeakBrushProperty);
		set => SetValue(PeakBrushProperty, value);
	}

	/// <summary>Colour of the horizontal baseline.</summary>
	public IBrush BaselineBrush
	{
		get => GetValue(BaselineBrushProperty);
		set => SetValue(BaselineBrushProperty, value);
	}

	/// <summary>Stroke width of the live beat (older beats are drawn a touch thinner).</summary>
	public double LineThickness
	{
		get => GetValue(LineThicknessProperty);
		set => SetValue(LineThicknessProperty, value);
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		_clock.Restart();
		_lastFrame = _clock.Elapsed;
		_timer = new DispatcherTimer(FrameInterval, DispatcherPriority.Render, OnFrame);
		_timer.Start();
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		_timer?.Stop();
		_timer = null;
		_clock.Stop();
		base.OnDetachedFromVisualTree(e);
	}

	private void OnFrame(object? sender, EventArgs e)
	{
		TimeSpan now = _clock.Elapsed;
		double dt = (now - _lastFrame).TotalSeconds;
		_lastFrame = now;

		EcgBeatOverlay? overlay = Overlay;
		if (overlay is null || !overlay.HasBeats)
		{
			return;
		}

		// Ease the vertical scale (already robust to amplitude spikes) and the reference cadence toward the
		// overlay's values. Easing the reference holds the centre steady so each beat's early/late shift
		// reads against a calm baseline — no jumps; the horizontal spread is the real timing signal.
		if (!_easeInitialised)
		{
			_easedMin = overlay.MinMicroVolts;
			_easedMax = overlay.MaxMicroVolts;
			_easedReferenceRr = overlay.ReferenceRrSeconds;
			_easeInitialised = true;
		}
		else
		{
			double k = 1.0 - Math.Exp(-dt * Math.Max(0.1, AnchorEaseRate));
			_easedMin += (overlay.MinMicroVolts - _easedMin) * k;
			_easedMax += (overlay.MaxMicroVolts - _easedMax) * k;
			_easedReferenceRr += (overlay.ReferenceRrSeconds - _easedReferenceRr) * k;
		}

		InvalidateVisual();
	}

	public override void Render(DrawingContext context)
	{
		var bounds = new Rect(Bounds.Size);
		double midY = bounds.Height / 2.0;
		context.DrawLine(new Pen(BaselineBrush, 1.0), new Point(0, midY), new Point(bounds.Width, midY));

		EcgBeatOverlay? overlay = Overlay;
		if (overlay is null || !overlay.HasBeats || bounds.Width <= 0 || bounds.Height <= 0)
		{
			return;
		}

		double min = _easeInitialised ? _easedMin : overlay.MinMicroVolts;
		double max = _easeInitialised ? _easedMax : overlay.MaxMicroVolts;
		double referenceRr = _easeInitialised ? _easedReferenceRr : overlay.ReferenceRrSeconds;
		double range = max - min;
		if (range < 1.0)
		{
			range = 1.0; // avoid divide-by-zero on a flat trace
		}

		if (referenceRr <= 0)
		{
			return;
		}

		double margin = bounds.Height * 0.08;
		double plotHeight = bounds.Height - (2 * margin);
		double centreX = bounds.Width / 2.0;
		// Half a reference cycle fills (most of) half the width, leaving a small horizontal margin.
		double pxPerSecond = centreX * 0.94 / (referenceRr / 2.0);

		double YAt(double v) => margin + (plotHeight * (1.0 - ((v - min) / range)));

		Color lineColor = (LineBrush as ISolidColorBrush)?.Color ?? Color.FromRgb(0x3D, 0xD6, 0x8C);

		using (context.PushClip(bounds))
		{
			// Faded stack, oldest first so newer beats paint on top. Each beat is shifted by how far its
			// RR sits from the reference cadence — that horizontal offset is the early/late signal.
			foreach (EcgOverlayBeat beat in overlay.Beats)
			{
				double alpha = Math.Max(MinBeatAlpha, NewestBeatAlpha * Math.Pow(BeatAlphaFalloff, beat.Age));
				DrawBeat(context, beat, lineColor, alpha, LineThickness * 0.9, centreX, pxPerSecond, referenceRr, YAt);
			}

			// Live beat: full strength, a touch heavier, on top of everything.
			if (overlay.Live is { } live)
			{
				DrawBeat(context, live, lineColor, 1.0, LineThickness, centreX, pxPerSecond, referenceRr, YAt);
			}

			// Centre guide: the reference cadence (a beat exactly on cadence sits here).
			var guidePen = new Pen(PeakBrush, 1.0) { DashStyle = new DashStyle([2, 3], 0) };
			context.DrawLine(guidePen, new Point(centreX, margin), new Point(centreX, bounds.Height - margin));
		}
	}

	// Strokes one beat as a single polyline geometry — one draw op instead of one per sample pair. The
	// beat is translated horizontally by (its RR − reference cadence), so an early beat sits left of
	// centre and a late beat right of it.
	private static void DrawBeat(
		DrawingContext context, EcgOverlayBeat beat, Color color, double alpha, double thickness,
		double centreX, double pxPerSecond, double referenceRr, Func<double, double> yAt)
	{
		IReadOnlyList<EcgBeatSample> samples = beat.Samples;
		if (samples.Count < 2)
		{
			return;
		}

		double shift = beat.IntervalSeconds > 0 ? beat.IntervalSeconds - referenceRr : 0.0;
		double XAt(double offsetSeconds) => centreX + ((offsetSeconds + shift) * pxPerSecond);

		var geometry = new StreamGeometry();
		using (StreamGeometryContext geo = geometry.Open())
		{
			geo.BeginFigure(new Point(XAt(samples[0].OffsetSeconds), yAt(samples[0].MicroVolts)), isFilled: false);
			for (int i = 1; i < samples.Count; i++)
			{
				geo.LineTo(new Point(XAt(samples[i].OffsetSeconds), yAt(samples[i].MicroVolts)));
			}

			geo.EndFigure(isClosed: false);
		}

		var pen = new Pen(
			new SolidColorBrush(color, alpha), thickness, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
		context.DrawGeometry(null, pen, geometry);
	}
}
