using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Mobile.Controls;

/// <summary>
/// "Stacked beats" ECG view: re-slices the live trace into one-beat-wide cardiac cycles, each aligned
/// on its R-peak at centre, and overlays them — the live beat brightest, older beats fading with a
/// diminishing alpha — so beat-to-beat variability reads as the spread of the stack. A render timer
/// eases the vertical scale and a horizontal anchor so a new beat settles softly to centre rather than
/// snapping into place. Backed by Avalonia's <see cref="DrawingContext"/> (Skia). Purely a signal view
/// — not a diagnostic ECG.
/// </summary>
public sealed class EcgStrip : Control
{
	// ~30 fps render loop, tied to the visual-tree lifecycle so it stops when the tab is off-screen.
	private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);

	// Exponential-ease rates (per second): how fast the vertical scale and the centre anchor settle.
	private const double ScaleEaseRate = 4.0;
	private const double AnchorEaseRate = 7.0;

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
	private bool _scaleInitialised;

	// Eased horizontal offset (seconds) applied to the whole stack. Bumped when a new beat arrives, then
	// eased back to 0 — the "ease to centre" that keeps the latest beat from jumping into position.
	private double _anchorSeconds;
	private int _lastLiveLength = -1;

	public static readonly StyledProperty<EcgBeatOverlay?> OverlayProperty =
		AvaloniaProperty.Register<EcgStrip, EcgBeatOverlay?>(nameof(Overlay));

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

		// Detect a new beat: the live slice resets to a short length the instant a fresh R-peak fires.
		int liveLength = overlay.Live?.Samples.Count ?? 0;
		if (_lastLiveLength >= 0 && liveLength < _lastLiveLength)
		{
			// Nudge the whole stack a fraction of a beat off-centre so it eases back in — no hard snap.
			_anchorSeconds = overlay.HalfWindowSeconds * 0.22;
		}

		_lastLiveLength = liveLength;

		// Ease the vertical scale toward the window's amplitude and the anchor back toward centre.
		double targetMin = overlay.MinMicroVolts;
		double targetMax = overlay.MaxMicroVolts;
		if (!_scaleInitialised)
		{
			_easedMin = targetMin;
			_easedMax = targetMax;
			_scaleInitialised = true;
		}
		else
		{
			double scaleK = 1.0 - Math.Exp(-dt * ScaleEaseRate);
			_easedMin += (targetMin - _easedMin) * scaleK;
			_easedMax += (targetMax - _easedMax) * scaleK;
		}

		_anchorSeconds *= Math.Exp(-dt * AnchorEaseRate);

		InvalidateVisual();
	}

	public override void Render(DrawingContext context)
	{
		var bounds = new Rect(Bounds.Size);
		double midY = bounds.Height / 2.0;
		context.DrawLine(new Pen(BaselineBrush, 1.0), new Point(0, midY), new Point(bounds.Width, midY));

		EcgBeatOverlay? overlay = Overlay;
		if (overlay is null || !overlay.HasBeats || bounds.Width <= 0 || bounds.Height <= 0
			|| overlay.HalfWindowSeconds <= 0)
		{
			return;
		}

		double min = _scaleInitialised ? _easedMin : overlay.MinMicroVolts;
		double max = _scaleInitialised ? _easedMax : overlay.MaxMicroVolts;
		double range = max - min;
		if (range < 1.0)
		{
			range = 1.0; // avoid divide-by-zero on a flat trace
		}

		double margin = bounds.Height * 0.08;
		double plotHeight = bounds.Height - (2 * margin);
		double centreX = bounds.Width / 2.0;
		// Half the window fills (most of) half the width, leaving a small horizontal margin.
		double pxPerSecond = centreX * 0.94 / overlay.HalfWindowSeconds;

		double XAt(double offsetSeconds) => centreX + ((offsetSeconds + _anchorSeconds) * pxPerSecond);
		double YAt(double v) => margin + (plotHeight * (1.0 - ((v - min) / range)));

		Color lineColor = (LineBrush as ISolidColorBrush)?.Color ?? Color.FromRgb(0x3D, 0xD6, 0x8C);

		using (context.PushClip(bounds))
		{
			// Faded stack, oldest first so newer beats paint on top.
			foreach (EcgOverlayBeat beat in overlay.Beats)
			{
				double alpha = Math.Max(MinBeatAlpha, NewestBeatAlpha * Math.Pow(BeatAlphaFalloff, beat.Age));
				DrawBeat(context, beat, lineColor, alpha, LineThickness * 0.9, XAt, YAt);
			}

			// Live beat: full strength, a touch heavier, on top of everything.
			if (overlay.Live is { } live)
			{
				DrawBeat(context, live, lineColor, 1.0, LineThickness, XAt, YAt);
			}

			// Centre R-peak guide.
			var guidePen = new Pen(PeakBrush, 1.0) { DashStyle = new DashStyle([2, 3], 0) };
			double guideX = XAt(0);
			context.DrawLine(guidePen, new Point(guideX, margin), new Point(guideX, bounds.Height - margin));
		}
	}

	private static void DrawBeat(
		DrawingContext context, EcgOverlayBeat beat, Color color, double alpha, double thickness,
		Func<double, double> xAt, Func<double, double> yAt)
	{
		IReadOnlyList<EcgBeatSample> samples = beat.Samples;
		if (samples.Count < 2)
		{
			return;
		}

		var pen = new Pen(
			new SolidColorBrush(color, alpha), thickness, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);

		var prev = new Point(xAt(samples[0].OffsetSeconds), yAt(samples[0].MicroVolts));
		for (int i = 1; i < samples.Count; i++)
		{
			var next = new Point(xAt(samples[i].OffsetSeconds), yAt(samples[i].MicroVolts));
			context.DrawLine(pen, prev, next);
			prev = next;
		}
	}
}
