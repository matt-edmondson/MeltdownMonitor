using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Regulation;
using SkiaSharp;

namespace MeltdownMonitor.Mobile.Controls;

/// <summary>
/// The Regulation Field — the signature live instrument (design doc §6 / the
/// Regulation Field plan). A figure-8 (lemniscate) "window of tolerance" with a
/// needle marker that slides from the cool REST lobe (left) through the centre
/// to the warm MELTDOWN lobe (right) as arousal rises above baseline. A comet
/// trail shows the recent trajectory; stroke fatness tracks variability quality;
/// the whole field dims while the baseline is still calibrating.
///
/// Renders through Avalonia's <see cref="DrawingContext"/> (Skia under the hood),
/// reusing the pure, unit-tested <see cref="LemniscateGeometry"/> and
/// <see cref="RegulationFieldCalculator"/> from Core verbatim — the same maths
/// the desktop view is specified against. While attached to the visual tree a
/// render-tick timer drives the desktop's pulse/jitter flourishes through a
/// pure <see cref="RegulationFieldAnimator"/>: the marker eases between the
/// multi-second samples, its halo pulses at the current HR cadence, and the
/// trace carries variability jitter. The timer stops when the control detaches
/// (the Now tab tears down while backgrounded) so it costs nothing off-screen.
/// </summary>
public sealed class RegulationField : Control
{
	// ~30 fps: enough for a smooth pulse/jitter without churning the battery
	// the way a 60 fps loop would on a phone that stays on this screen.
	private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);

	// Vagal-tone half-travel as a fraction of lobe height: the marker (and its trail / the
	// Y-axis histogram) ride from FRAGILE at the top to STEADY at the bottom across ±this.
	// Matches the desktop RegulationFieldView.MarkerYSpan.
	private const float MarkerYSpan = 0.92f;

	private readonly RegulationFieldAnimator _animator = new();
	private readonly Stopwatch _clock = new();
	private DispatcherTimer? _timer;
	private TimeSpan _lastFrame;

	// Free-running smooth scroll over the absolute beat timeline for the live RR texture —
	// the same mechanism the desktop view uses to decouple the texture flow from the
	// irregular, batched arrival of BLE beats.
	private RrTexturePlayhead _playhead;

	public static readonly StyledProperty<RegulationReading> ReadingProperty =
		AvaloniaProperty.Register<RegulationField, RegulationReading>(
			nameof(Reading), new RegulationReading(0.0, 1.0, 0.0, 0.5, 0.0));

	public static readonly StyledProperty<IReadOnlyList<RegulationTrailPoint>?> TrailProperty =
		AvaloniaProperty.Register<RegulationField, IReadOnlyList<RegulationTrailPoint>?>(nameof(Trail));

	public static readonly StyledProperty<Color> StateColorProperty =
		AvaloniaProperty.Register<RegulationField, Color>(
			nameof(StateColor), Color.FromRgb(0x29, 0x80, 0xD8));

	public static readonly StyledProperty<double> HeartRateProperty =
		AvaloniaProperty.Register<RegulationField, double>(nameof(HeartRate));

	public static readonly StyledProperty<RegulationDynamics> DynamicsProperty =
		AvaloniaProperty.Register<RegulationField, RegulationDynamics>(
			nameof(Dynamics), RegulationDynamics.Steady);

	public static readonly StyledProperty<RecoveryProgress> RecoveryProperty =
		AvaloniaProperty.Register<RegulationField, RecoveryProgress>(
			nameof(Recovery), RecoveryProgress.Inactive);

	public static readonly StyledProperty<double> JitterExaggerationProperty =
		AvaloniaProperty.Register<RegulationField, double>(nameof(JitterExaggeration), 1.0);

	public static readonly StyledProperty<double> LobeThicknessProperty =
		AvaloniaProperty.Register<RegulationField, double>(nameof(LobeThickness), 1.0);

	public static readonly StyledProperty<int> IndexBucketsProperty =
		AvaloniaProperty.Register<RegulationField, int>(nameof(IndexBuckets), 24);

	public static readonly StyledProperty<int> VagalBucketsProperty =
		AvaloniaProperty.Register<RegulationField, int>(nameof(VagalBuckets), 16);

	public static readonly StyledProperty<int> LobeSegmentsProperty =
		AvaloniaProperty.Register<RegulationField, int>(
			nameof(LobeSegments), LemniscateGeometry.DefaultSegments);

	// Catppuccin Macchiato — the field's distinctive palette, single-sourced here
	// to match the desktop renderer's MacchiatoPalette.
	private static readonly Color Base = Color.FromRgb(0x24, 0x27, 0x3a);
	private static readonly Color Text = Color.FromRgb(0xca, 0xd3, 0xf5);
	private static readonly Color Subtext0 = Color.FromRgb(0xa5, 0xad, 0xcb);
	private static readonly Color Overlay1 = Color.FromRgb(0x80, 0x87, 0xa2);
	private static readonly Color Lavender = Color.FromRgb(0xb7, 0xbd, 0xf8);
	// Collapse / shutdown hue — dim slate-indigo, distinct from Lavender (window-of-tolerance + crossover).
	private static readonly Color Slate = Color.FromRgb(0x5d, 0x6a, 0x9e);
	private static readonly Color Sky = Color.FromRgb(0x91, 0xd7, 0xe3);
	private static readonly Color Sapphire = Color.FromRgb(0x7d, 0xc4, 0xe4);
	private static readonly Color Peach = Color.FromRgb(0xf5, 0xa9, 0x7f);
	private static readonly Color Maroon = Color.FromRgb(0xee, 0x99, 0xa0);
	private static readonly Color Green = Color.FromRgb(0xa6, 0xda, 0x95);
	// Dwell-heatmap magma ramp + crosshair shadow (match the desktop MacchiatoPalette).
	private static readonly Color Mantle = Color.FromRgb(0x1e, 0x20, 0x30);
	private static readonly Color Mauve = Color.FromRgb(0xc6, 0xa0, 0xf6);
	private static readonly Color Red = Color.FromRgb(0xed, 0x87, 0x96);
	private static readonly Color Yellow = Color.FromRgb(0xee, 0xd4, 0x9f);

	public static readonly StyledProperty<IReadOnlyList<RegulationTrailPoint>?> DwellTrailProperty =
		AvaloniaProperty.Register<RegulationField, IReadOnlyList<RegulationTrailPoint>?>(nameof(DwellTrail));

	public static readonly StyledProperty<double> HeatmapOpacityProperty =
		AvaloniaProperty.Register<RegulationField, double>(nameof(HeatmapOpacity), 0.35);

	public static readonly StyledProperty<double> HeatmapPeakOpacityProperty =
		AvaloniaProperty.Register<RegulationField, double>(nameof(HeatmapPeakOpacity), 0.70);

	public static readonly StyledProperty<double> HeatmapRegionOpacityProperty =
		AvaloniaProperty.Register<RegulationField, double>(nameof(HeatmapRegionOpacity), 0.55);

	public static readonly StyledProperty<double> HeatmapRegionThresholdProperty =
		AvaloniaProperty.Register<RegulationField, double>(nameof(HeatmapRegionThreshold), 0.50);

	/// <summary>The longer dwell window the density heatmap accumulates over (oldest first) —
	/// distinct from the shorter comet <see cref="Trail"/>. Bound from NowViewModel.DwellTrail.</summary>
	public IReadOnlyList<RegulationTrailPoint>? DwellTrail
	{
		get => GetValue(DwellTrailProperty);
		set => SetValue(DwellTrailProperty, value);
	}

	/// <summary>Overall opacity of the dwell heatmap (0 hides it). Default 35%.</summary>
	public double HeatmapOpacity
	{
		get => GetValue(HeatmapOpacityProperty);
		set => SetValue(HeatmapOpacityProperty, value);
	}

	/// <summary>Opacity of the crosshair pinning the peak-dwell bucket (0 hides it). Default 70%.</summary>
	public double HeatmapPeakOpacity
	{
		get => GetValue(HeatmapPeakOpacityProperty);
		set => SetValue(HeatmapPeakOpacityProperty, value);
	}

	/// <summary>Opacity of the dashed box framing the high-density region (0 hides it). Default 55%.</summary>
	public double HeatmapRegionOpacity
	{
		get => GetValue(HeatmapRegionOpacityProperty);
		set => SetValue(HeatmapRegionOpacityProperty, value);
	}

	/// <summary>Share of the peak bucket a cell must reach to sit inside the dashed region. Default 50%.</summary>
	public double HeatmapRegionThreshold
	{
		get => GetValue(HeatmapRegionThresholdProperty);
		set => SetValue(HeatmapRegionThresholdProperty, value);
	}

	public static readonly StyledProperty<double> TrailOpacityProperty =
		AvaloniaProperty.Register<RegulationField, double>(nameof(TrailOpacity), 0.70);

	public static readonly StyledProperty<double> HistogramOpacityProperty =
		AvaloniaProperty.Register<RegulationField, double>(nameof(HistogramOpacity), 0.60);

	/// <summary>Opacity of the comet trail. It draws additively and blooms where the tail
	/// overlaps itself and the marker, so lower this if it saturates; default 70%.</summary>
	public double TrailOpacity
	{
		get => GetValue(TrailOpacityProperty);
		set => SetValue(TrailOpacityProperty, value);
	}

	/// <summary>Opacity of the axis histograms (the arousal and vagal-tone bars). They draw
	/// additively, so lower this if they saturate; default 60%.</summary>
	public double HistogramOpacity
	{
		get => GetValue(HistogramOpacityProperty);
		set => SetValue(HistogramOpacityProperty, value);
	}

	public static readonly StyledProperty<double> LobeOpacityProperty =
		AvaloniaProperty.Register<RegulationField, double>(nameof(LobeOpacity), 0.60);

	/// <summary>Opacity of the live-trace lobes. They draw additively and bloom where strokes
	/// overlap, so lower this if they saturate; default 60% (the desktop's tuned default).</summary>
	public double LobeOpacity
	{
		get => GetValue(LobeOpacityProperty);
		set => SetValue(LobeOpacityProperty, value);
	}

	public static readonly StyledProperty<bool> UseLfHfCorroborationProperty =
		AvaloniaProperty.Register<RegulationField, bool>(nameof(UseLfHfCorroboration), true);

	/// <summary>Mirrors DetectionThresholds.UseLfHfCorroboration — gates the LF/HF balance halo,
	/// the soft additive glow biased toward the dominant autonomic pole.</summary>
	public bool UseLfHfCorroboration
	{
		get => GetValue(UseLfHfCorroborationProperty);
		set => SetValue(UseLfHfCorroborationProperty, value);
	}

	public static readonly StyledProperty<IReadOnlyList<double>?> RrProperty =
		AvaloniaProperty.Register<RegulationField, IReadOnlyList<double>?>(nameof(Rr));

	public static readonly StyledProperty<long> RrBeatsAppendedProperty =
		AvaloniaProperty.Register<RegulationField, long>(nameof(RrBeatsAppended));

	/// <summary>Recent non-artifact RR intervals (ms), oldest first — the real beat-to-beat
	/// signal texturing the live trace. Smooth/flat when fewer than RrTexture.MinRrForJitter.</summary>
	public IReadOnlyList<double>? Rr
	{
		get => GetValue(RrProperty);
		set => SetValue(RrProperty, value);
	}

	/// <summary>Total non-artifact beats ever appended — the absolute timeline the RR texture
	/// playhead scrolls along (newest buffer sample = RrBeatsAppended - 1).</summary>
	public long RrBeatsAppended
	{
		get => GetValue(RrBeatsAppendedProperty);
		set => SetValue(RrBeatsAppendedProperty, value);
	}

	public static readonly StyledProperty<double> HypoarousalProperty =
		AvaloniaProperty.Register<RegulationField, double>(nameof(Hypoarousal));

	public static readonly StyledProperty<RegulationDynamics> HypoarousalDynamicsProperty =
		AvaloniaProperty.Register<RegulationField, RegulationDynamics>(
			nameof(HypoarousalDynamics), RegulationDynamics.Steady);

	/// <summary>[0,1] low-arousal collapse signal driving the shutdown zone + marker halo.</summary>
	public double Hypoarousal
	{
		get => GetValue(HypoarousalProperty);
		set => SetValue(HypoarousalProperty, value);
	}

	/// <summary>Velocity/trend of the collapse signal; selects the hypoarousal-aware arrow.</summary>
	public RegulationDynamics HypoarousalDynamics
	{
		get => GetValue(HypoarousalDynamicsProperty);
		set => SetValue(HypoarousalDynamicsProperty, value);
	}

	static RegulationField() =>
		AffectsRender<RegulationField>(ReadingProperty, TrailProperty, StateColorProperty, DynamicsProperty, RecoveryProperty, LobeThicknessProperty, LobeSegmentsProperty, IndexBucketsProperty, VagalBucketsProperty, HypoarousalProperty, HypoarousalDynamicsProperty);

	/// <summary>Latest arousal-vs-baseline reading; drives the marker position,
	/// stroke fatness and overall confidence dimming.</summary>
	public RegulationReading Reading
	{
		get => GetValue(ReadingProperty);
		set => SetValue(ReadingProperty, value);
	}

	/// <summary>Recent trail points, oldest first, drawn as a fading comet trail along the
	/// major axis. Each carries the detector state it was captured under so the segment keeps
	/// its original colour rather than recolouring to the current state.</summary>
	public IReadOnlyList<RegulationTrailPoint>? Trail
	{
		get => GetValue(TrailProperty);
		set => SetValue(TrailProperty, value);
	}

	/// <summary>The confirmed detector-state accent — the marker and trail take
	/// this colour so the field agrees with the rest of the Now screen.</summary>
	public Color StateColor
	{
		get => GetValue(StateColorProperty);
		set => SetValue(StateColorProperty, value);
	}

	/// <summary>Current heart rate (bpm); sets the pulse cadence of the
	/// marker halo. Zero/absent falls back to a gentle resting pulse.</summary>
	public double HeartRate
	{
		get => GetValue(HeartRateProperty);
		set => SetValue(HeartRateProperty, value);
	}

	/// <summary>Latest escalation/de-escalation velocity + trend; drives the marker's
	/// direction arrow and tints the trail's leading edge.</summary>
	public RegulationDynamics Dynamics
	{
		get => GetValue(DynamicsProperty);
		set => SetValue(DynamicsProperty, value);
	}

	/// <summary>How close the body is to clearing the current episode; draws the recovery
	/// gate and a progress arc during Warning/Alerting. Inactive otherwise.</summary>
	public RecoveryProgress Recovery
	{
		get => GetValue(RecoveryProperty);
		set => SetValue(RecoveryProperty, value);
	}

	/// <summary>User-configurable multiplier on the live trace's variability jitter
	/// (clamped 0–3). 1.0 is the tuned default; fed to the animator each frame.</summary>
	public double JitterExaggeration
	{
		get => GetValue(JitterExaggerationProperty);
		set => SetValue(JitterExaggerationProperty, value);
	}

	/// <summary>User-configurable multiplier on the live trace's lobe stroke thickness
	/// (clamped 0.5–3). 1.0 is the tuned default.</summary>
	public double LobeThickness
	{
		get => GetValue(LobeThicknessProperty);
		set => SetValue(LobeThicknessProperty, value);
	}

	/// <summary>Bucket resolution of the arousal-index (X) axis histogram (clamped 6–64 at draw).</summary>
	public int IndexBuckets
	{
		get => GetValue(IndexBucketsProperty);
		set => SetValue(IndexBucketsProperty, value);
	}

	/// <summary>Bucket resolution of the vagal-tone (Y) axis histogram (clamped 6–64 at draw).</summary>
	public int VagalBuckets
	{
		get => GetValue(VagalBucketsProperty);
		set => SetValue(VagalBucketsProperty, value);
	}

	/// <summary>Number of points sampled along the figure-8 outline — its render resolution
	/// (clamped 24–256 at draw). Higher = smoother curve; lower = more faceted.</summary>
	public int LobeSegments
	{
		get => GetValue(LobeSegmentsProperty);
		set => SetValue(LobeSegmentsProperty, value);
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
		var now = _clock.Elapsed;
		double dt = (now - _lastFrame).TotalSeconds;
		_lastFrame = now;

		_animator.JitterExaggeration = Math.Clamp(JitterExaggeration, 0.0, 3.0);
		_animator.Step(dt, Reading.Index, HeartRate, Dynamics.NormalizedSpeed);

		// Scroll the RR texture along the absolute beat timeline at the real beat rate,
		// driven purely by frame time so it stays fluid despite batched BLE arrivals.
		_playhead.Advance(dt, Math.Max(40.0, HeartRate) / 60.0, RrBeatsAppended - 1);

		InvalidateVisual();
	}

	public override void Render(DrawingContext context)
	{
		double w = Bounds.Width;
		double h = Bounds.Height;
		if (w <= 2 || h <= 2)
		{
			return;
		}

		var reading = Reading;
		double confidence = Math.Clamp(reading.Confidence, 0.0, 1.0);

		var centre = new Point(w * 0.5, h * 0.5);
		float halfWidth = (float)Math.Min(w * 0.40, 240.0);
		float lobeHeight = (float)Math.Min(h * 0.34, halfWidth * 0.62f);
		var centreV = new Vector2((float)centre.X, (float)centre.Y);

		DrawLfHfHalo(context, centre, halfWidth, reading, confidence);

		// Window of tolerance: a soft lavender zone marking the regulated centre.
		context.DrawEllipse(Brush(Lavender, 0.08 * confidence), null, centre, halfWidth * 0.32, lobeHeight * 0.7);

		DrawShutdownZone(context, centreV, halfWidth, lobeHeight, confidence);

		DrawDensityHeatmap(context, centreV, halfWidth, lobeHeight * MarkerYSpan, confidence);

		var ghost = LemniscateGeometry.Polyline(centreV, halfWidth, lobeHeight,
			Math.Clamp(LobeSegments, LemniscateGeometry.MinSegments, LemniscateGeometry.MaxSegments));
		DrawTrace(context, ghost, centreV, halfWidth, reading, confidence);
		DrawAxisHistograms(context, centre, w, h, halfWidth, lobeHeight, confidence);

		// Vagal axis + warning threshold lines. The vertical legend brackets the marker's
		// vagal-tone travel (FRAGILE at the top extent, STEADY at the bottom) so the Y motion
		// reads; the dashed verticals at ±WarningBoundaryIndex are the lines the marker/trail
		// visibly cross on the way in and out of the warning zone. Mirrors the desktop DrawVagalAxis.
		{
			float markerYClamp = lobeHeight * MarkerYSpan;
			double topY = centre.Y + RegulationFieldGeometry.VagalToneOffsetY(0.0, markerYClamp);
			double botY = centre.Y + RegulationFieldGeometry.VagalToneOffsetY(1.0, markerYClamp);
			context.DrawLine(new Pen(Brush(Overlay1, 0.22 * confidence), 1),
				new Point(centre.X, topY), new Point(centre.X, botY));
			DrawText(context, "FRAGILE", new Point(centre.X, topY - 16), Subtext0, 10, centred: true);
			DrawText(context, "STEADY", new Point(centre.X, botY + 2), Subtext0, 10, centred: true);

			double warnOff = RegulationFieldCalculator.WarningBoundaryIndex * halfWidth;
			DrawDashedVertical(context, centre.X + warnOff, topY, botY, Brush(Peach, 0.28 * confidence), 1, 4, 3);
			DrawDashedVertical(context, centre.X - warnOff, topY, botY, Brush(Sky, 0.28 * confidence), 1, 4, 3);
		}

		DrawTrail(context, centreV, halfWidth, lobeHeight, confidence);
		DrawRecoveryTarget(context, centreV, halfWidth, lobeHeight, confidence);
		DrawMarker(context, centreV, halfWidth, lobeHeight, confidence);

		// Crossover node at the centre of the figure-8.
		context.DrawEllipse(Brush(Lavender, confidence), null, centre, 6, 6);
		context.DrawEllipse(Brush(Text, confidence), null, centre, 2.5, 2.5);

		DrawLabels(context, centre, halfWidth, lobeHeight);

		if (confidence < 0.999)
		{
			DrawText(context, $"Calibrating baseline… {confidence * 100:F0}%",
				new Point(centre.X, centre.Y + lobeHeight + 14), Subtext0, 12, centred: true);
		}
	}

	// Soft, asymmetric glow biased toward the dominant autonomic pole. Gated on the LF/HF
	// corroboration setting, and only once a real LF/HF balance exists — LF/HF is laggy/noisy,
	// so it is a low-commitment lean cue, not a gate. Three concentric discs fake a soft radial
	// falloff; drawn additively (SKBlendMode.Plus via AdditiveSkiaLayer) so the overlapping discs
	// accumulate toward the centre into a real glow, mirroring the desktop renderer.
	private void DrawLfHfHalo(DrawingContext context, Point centre, float halfWidth, RegulationReading r, double confidence)
	{
		if (!UseLfHfCorroboration)
		{
			return;
		}

		float bal = (float)r.LfHfBalance;
		if (Math.Abs(bal) < 0.02f)
		{
			return;
		}

		Color hue = bal >= 0 ? Peach : Sky;
		var glowCentre = new SKPoint((float)(centre.X + (bal * halfWidth * 0.6f)), (float)centre.Y);
		float baseAlpha = (float)(Math.Min(1f, Math.Abs(bal)) * 0.10f * confidence);

		context.Custom(new AdditiveSkiaLayer(new Rect(Bounds.Size), (canvas, paint) =>
		{
			paint.Style = SKPaintStyle.Fill;
			for (int i = 3; i >= 1; i--)
			{
				paint.Color = Sk(hue, baseAlpha / i);
				canvas.DrawCircle(glowCentre, halfWidth * 0.30f * i, paint);
			}
		}));
	}

	/// <summary>Avalonia colour → SKColor with a [0,1] alpha, for the additive Skia layers.</summary>
	private static SKColor Sk(Color c, double alpha) =>
		new(c.R, c.G, c.B, (byte)Math.Clamp(alpha * 255.0, 0.0, 255.0));

	// Dwell heatmap: a grid of buckets showing where the field has spent its time over the
	// (configurable, usually long) dwell window — the 2D joint of the two axis histograms.
	// Cells lay out through the same X = arousal index, Y = vagal tone mapping as the marker.
	// The magma cells draw additively (they glow rather than sit as flat tiles); the dashed
	// high-density region box and the peak crosshair stay alpha-over as crisp chrome. Ported
	// from the desktop RegulationFieldView.DrawDensityHeatmap against the same Core bucketing.
	private void DrawDensityHeatmap(DrawingContext context, Vector2 centre, float halfWidth, float markerYClamp, double confidence)
	{
		double opacity = Math.Clamp(HeatmapOpacity, 0.0, 1.0);
		double peakOpacity = Math.Clamp(HeatmapPeakOpacity, 0.0, 1.0);
		double regionOpacity = Math.Clamp(HeatmapRegionOpacity, 0.0, 1.0);
		if (opacity <= 0.0 && peakOpacity <= 0.0 && regionOpacity <= 0.0)
		{
			return;
		}

		// Need a little dwell before a density reads as anything but noise.
		var trail = DwellTrail;
		if (trail is null || trail.Count < 4)
		{
			return;
		}

		// Grid resolution is the same per-axis bucket count that drives the axis histograms,
		// so the heatmap stays a true 2D joint of them.
		int xb = Math.Clamp(IndexBuckets, 6, 64);
		int yb = Math.Clamp(VagalBuckets, 6, 64);
		var density = RegulationFieldHistogram.FieldDensity(trail, xb, yb);
		if (density.PeakCount <= 0)
		{
			return;
		}

		float cellW = (halfWidth * 2f) / xb;
		float cellH = (markerYClamp * 2f) / yb;
		float left0 = centre.X - halfWidth;
		float top0 = centre.Y - markerYClamp;
		float peak = density.PeakCount;

		if (opacity > 0.0)
		{
			context.Custom(new AdditiveSkiaLayer(new Rect(Bounds.Size), (canvas, paint) =>
			{
				paint.Style = SKPaintStyle.Fill;
				for (int y = 0; y < yb; y++)
				{
					float top = top0 + (y * cellH);
					for (int x = 0; x < xb; x++)
					{
						int c = density.Count(x, y);
						if (c == 0)
						{
							continue;
						}

						// Gamma-lift the normalised dwell so mid-traffic cells read distinctly.
						float t = MathF.Pow(c / peak, 0.6f);
						float left = left0 + (x * cellW);
						paint.Color = Sk(HeatColor(t), opacity * confidence);
						canvas.DrawRect(left, top, cellW, cellH, paint);
					}
				}
			}));
		}

		// Dashed box around the high-concentration region — drawn beneath the crosshair so
		// the pointer still reads on top. Alpha-over so the dashes stay crisp.
		if (regionOpacity > 0.0
			&& density.HighDensityBounds(Math.Clamp(HeatmapRegionThreshold, 0.0, 1.0)) is { } b)
		{
			var topLeft = new Point(left0 + (b.MinX * cellW), top0 + (b.MinY * cellH));
			var bottomRight = new Point(left0 + ((b.MaxX + 1) * cellW), top0 + ((b.MaxY + 1) * cellH));
			DrawHeatmapDensityRegion(context, topLeft, bottomRight, regionOpacity * confidence);
		}

		// Crosshair over the busiest bucket's centre, so the peak-dwell spot is unmistakable
		// even when the heatmap is faint or hidden.
		if (peakOpacity > 0.0 && density.PeakX >= 0)
		{
			var peakCentre = new Point(
				left0 + ((density.PeakX + 0.5f) * cellW),
				top0 + ((density.PeakY + 0.5f) * cellH));
			DrawHeatmapPeakCrosshair(context, peakCentre, cellW, cellH, peakOpacity * confidence);
		}
	}

	// Dwell-heatmap gradient: a magma-style ramp built from Catppuccin Macchiato hues — dark
	// field background (low dwell) → Mauve → Red → Peach → Yellow (peak dwell). Stops are
	// positioned (not evenly spaced) to keep the purple band wide and the bright top tight.
	private static readonly (float Pos, Color Color)[] HeatStops =
	[
		(0.00f, Base),
		(0.22f, Mauve),
		(0.48f, Red),
		(0.74f, Peach),
		(1.00f, Yellow),
	];

	private static Color HeatColor(float t)
	{
		t = Math.Clamp(t, 0f, 1f);
		for (int i = 1; i < HeatStops.Length; i++)
		{
			if (t <= HeatStops[i].Pos)
			{
				(float aPos, Color aCol) = HeatStops[i - 1];
				(float bPos, Color bCol) = HeatStops[i];
				float span = bPos - aPos;
				return Lerp(aCol, bCol, span > 0f ? (t - aPos) / span : 0f);
			}
		}

		return HeatStops[^1].Color;
	}

	// A dashed rectangle framing the dwell heatmap's high-concentration region: a darker
	// shadow underlay then a bright Sky dash on top, so it stays legible over both hot magma
	// cells and the dark canvas.
	private static void DrawHeatmapDensityRegion(DrawingContext context, Point topLeft, Point bottomRight, double alpha)
	{
		if (alpha <= 0.0)
		{
			return;
		}

		var tr = new Point(bottomRight.X, topLeft.Y);
		var bl = new Point(topLeft.X, bottomRight.Y);
		DrawDashedShadowedLine(context, topLeft, tr, alpha);
		DrawDashedShadowedLine(context, tr, bottomRight, alpha);
		DrawDashedShadowedLine(context, bottomRight, bl, alpha);
		DrawDashedShadowedLine(context, bl, topLeft, alpha);
	}

	// One dashed edge: walk from a to b laying down dash-length segments separated by gaps,
	// each a Mantle shadow underlay then a bright Sky line on top.
	private static void DrawDashedShadowedLine(DrawingContext context, Point a, Point b, double alpha)
	{
		double dx = b.X - a.X;
		double dy = b.Y - a.Y;
		double length = Math.Sqrt((dx * dx) + (dy * dy));
		if (length <= 0.0)
		{
			return;
		}

		double ux = dx / length;
		double uy = dy / length;
		const double thick = 1.5;
		const double dash = 6.0;
		const double gap = 4.0;
		var shadowPen = new Pen(Brush(Mantle, alpha * 0.6), thick + 2);
		var linePen = new Pen(Brush(Sky, alpha), thick);
		for (double pos = 0.0; pos < length; pos += dash + gap)
		{
			double end = Math.Min(pos + dash, length);
			var segStart = new Point(a.X + (ux * pos), a.Y + (uy * pos));
			var segEnd = new Point(a.X + (ux * end), a.Y + (uy * end));
			context.DrawLine(shadowPen, segStart, segEnd);
			context.DrawLine(linePen, segStart, segEnd);
		}
	}

	// A crosshair pinning the peak-dwell bucket: four arms reaching out from a centre ring,
	// with a gap so the bucket itself stays visible. Shadow underlay then bright Sky on top.
	private static void DrawHeatmapPeakCrosshair(DrawingContext context, Point centre, double cellW, double cellH, double alpha)
	{
		if (alpha <= 0.0)
		{
			return;
		}

		double span = Math.Max(cellW, cellH);
		double gap = span * 0.55;
		double reach = span * 1.3;
		const double thick = 1.5;
		var shadowPen = new Pen(Brush(Mantle, alpha * 0.6), thick + 2);
		var linePen = new Pen(Brush(Sky, alpha), thick);

		void Stroke(Point from, Point to)
		{
			context.DrawLine(shadowPen, from, to);
			context.DrawLine(linePen, from, to);
		}

		Stroke(new Point(centre.X - gap, centre.Y), new Point(centre.X - gap - reach, centre.Y));
		Stroke(new Point(centre.X + gap, centre.Y), new Point(centre.X + gap + reach, centre.Y));
		Stroke(new Point(centre.X, centre.Y - gap), new Point(centre.X, centre.Y - gap - reach));
		Stroke(new Point(centre.X, centre.Y + gap), new Point(centre.X, centre.Y + gap + reach));

		context.DrawEllipse(null, shadowPen, centre, gap, gap);
		context.DrawEllipse(null, linePen, centre, gap, gap);
	}

	// Upper-cool quadrant (cool/left side, low variability) = collapse territory. Fill it with Slate
	// at an opacity that tracks the collapse signal, so approach is visible before the latch.
	private void DrawShutdownZone(DrawingContext context, Vector2 centre, float halfWidth, float lobeHeight, double confidence)
	{
		double intensity = HypoarousalVisual.Intensity(Hypoarousal);
		if (intensity <= 0.0)
		{
			return;
		}

		double top = centre.Y - (lobeHeight * 0.95);
		var rect = new Rect(centre.X - halfWidth, top, halfWidth, centre.Y - top);
		context.FillRectangle(Brush(Slate, 0.22 * intensity * confidence), rect);

		// Intensity-gated SHUTDOWN label at the top of the collapse zone — names the zone
		// it sits in for parity with the always-on REST/MELTDOWN pole labels.
		if (intensity > 0.01)
		{
			DrawText(context, "SHUTDOWN",
				new Point(centre.X - (halfWidth * 0.5), top + 2),
				Slate, 10, centred: true);
		}
	}

	// Catmull-Rom sub-steps per lobe segment for the live-trace centreline (desktop RibbonSub).
	private const int TraceSub = 4;

	// Peak trace deflection (px) from the real RR signal at 1× exaggeration (desktop MaxJitterPx).
	private const float MaxJitterPx = 18f;

	private void DrawTrace(DrawingContext context, IReadOnlyList<Vector2> ghost, Vector2 centre, float halfWidth, RegulationReading r, double confidence)
	{
		if (ghost.Count < 2)
		{
			return;
		}

		// Ghost baseline (symmetric resting frame) — crisp alpha-over chrome.
		var ghostPen = new Pen(Brush(Overlay1, 0.28 * confidence), 1.5);
		for (int i = 0; i < ghost.Count; i++)
		{
			context.DrawLine(ghostPen, P(ghost[i]), P(ghost[(i + 1) % ghost.Count]));
		}

		// Live two-tone trace, textured with the REAL beat-to-beat signal: jagged when HRV is
		// healthy, flat when it collapses; tapers to nothing at the crossover. The lobe is mapped
		// ONCE onto a window of the RR signal (no tiling → no spatial repeat), rotated so the
		// oldest↔newest seam lands on a crossover (depth≈0, hidden). The window's leading edge is
		// the smooth playhead (absolute beat-index units), so the texture flows continuously
		// rather than snapping to batched BLE beat arrivals. Ported from the desktop DrawLemniscate.
		float clampedIndex = Math.Clamp((float)r.Index, -1f, 1f);
		float warmSwell = 1f + (MathF.Max(0f, clampedIndex) * 1.4f);
		float coolSwell = 1f + (MathF.Max(0f, -clampedIndex) * 1.4f);
		float quality = (float)Math.Clamp(r.VariabilityQuality, 0.0, 1.0);
		float baseThick = (float)((3.0 + (4.0 * quality)) * Math.Clamp(LobeThickness, 0.5, 3.0));
		double lobeAlpha = confidence * Math.Clamp(LobeOpacity, 0.0, 1.0);

		float[] dev = RrTexture.BuildRrDeviations(Rr ?? []);
		int n = ghost.Count;
		int devLen = dev.Length;
		int quarter = n / 4;
		double span = Math.Min(devLen - 1, n - 1);
		// Absolute beat index → buffer index: the newest buffer sample (devLen-1) is absolute (RrBeatsAppended-1).
		double bufOffset = devLen - RrBeatsAppended;
		double playhead = _playhead.Position;

		// Jitter each VERTEX once (along the smoothed vertex normal) so adjacent segments share
		// an endpoint and the trace stays continuous.
		var pts = new Vector2[n];
		for (int i = 0; i < n; i++)
		{
			Vector2 v = ghost[i];
			float depth = MathF.Min(1f, MathF.Abs(v.X - centre.X) / halfWidth);
			float jitter = 0f;
			if (devLen > 1)
			{
				int seg = (((i - quarter) % n) + n) % n;
				double posAbs = playhead - span + (seg / (double)(n - 1) * span);
				double posBuf = Math.Clamp(posAbs + bufOffset, 0.0, devLen - 1);
				int i0 = (int)Math.Floor(posBuf);
				int i1 = Math.Min(i0 + 1, devLen - 1);
				float d = dev[i0] + ((dev[i1] - dev[i0]) * (float)(posBuf - i0));
				jitter = d * MaxJitterPx * (float)Math.Clamp(JitterExaggeration, 0.0, 3.0) * depth;
			}

			Vector2 normal = Normal(ghost[(i - 1 + n) % n], ghost[(i + 1) % n]);
			pts[i] = v + (normal * jitter);
		}

		// Smooth flowing undulations rather than faceted spikes: a closed Catmull-Rom centreline
		// through the jittered vertices, each sub-point carrying its own colour (warm/cool by
		// side, deepening with depth) and width (lobe swell). Pre-computed here; the additive
		// layer below just strokes the segments with SKBlendMode.Plus so the lobes glow.
		int m = n * TraceSub;
		var spline = new SKPoint[m];
		var cols = new SKColor[m];
		var widths = new float[m];
		int w = 0;
		for (int i = 0; i < n; i++)
		{
			Vector2 p0 = pts[(i - 1 + n) % n];
			Vector2 p1 = pts[i];
			Vector2 p2 = pts[(i + 1) % n];
			Vector2 p3 = pts[(i + 2) % n];
			for (int s = 1; s <= TraceSub; s++)
			{
				Vector2 cur = CatmullRom(p0, p1, p2, p3, s / (float)TraceSub);
				bool warm = cur.X >= centre.X;
				float depth = MathF.Min(1f, MathF.Abs(cur.X - centre.X) / halfWidth);
				Color c = warm ? Lerp(Peach, Maroon, depth) : Lerp(Sky, Sapphire, depth);
				spline[w] = new SKPoint(cur.X, cur.Y);
				cols[w] = Sk(c, lobeAlpha);
				widths[w] = baseThick * (warm ? warmSwell : coolSwell);
				w++;
			}
		}

		context.Custom(new AdditiveSkiaLayer(new Rect(Bounds.Size), (canvas, paint) =>
		{
			paint.Style = SKPaintStyle.Stroke;
			paint.StrokeCap = SKStrokeCap.Butt;
			for (int i = 0; i < m; i++)
			{
				int j = (i + 1) % m;
				paint.Color = cols[i];
				paint.StrokeWidth = widths[i];
				canvas.DrawLine(spline[i], spline[j], paint);
			}
		}));
	}

	// Uniform Catmull-Rom interpolation between p1 and p2 (p0/p3 are the neighbouring points).
	private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
	{
		float t2 = t * t;
		float t3 = t2 * t;
		return 0.5f * ((2f * p1)
			+ ((-p0 + p2) * t)
			+ (((2f * p0) - (5f * p1) + (4f * p2) - p3) * t2)
			+ ((-p0 + (3f * p1) - (3f * p2) + p3) * t3));
	}

	// Axis density histograms: how the samples currently in the trail window are distributed.
	// X (arousal index) is a row of vertical bars below the field, each column aligned with the
	// index it counts — left=cool/REST, right=warm/MELTDOWN. Y (vagal tone) is a column
	// of horizontal bars on the left margin, top=FRAGILE (low tone) to bottom=STEADY (high).
	// Mirrors the desktop RegulationFieldView, computed from the same Core bucketing.
	private void DrawAxisHistograms(DrawingContext context, Point centre, double w, double h, float halfWidth, float lobeHeight, double confidence)
	{
		var trail = Trail;
		if (trail is null || trail.Count < 2)
		{
			return;
		}

		var xHist = RegulationFieldHistogram.IndexAxis(trail, Math.Clamp(IndexBuckets, 6, 64));
		var yHist = RegulationFieldHistogram.VagalToneAxis(trail, Math.Clamp(VagalBuckets, 6, 64));
		var axisBrush = Brush(Overlay1, 0.22 * confidence);
		var axisPen = new Pen(axisBrush, 1);

		// X axis (arousal index), below the field, bars growing downward. The axis spans the
		// field's fixed [-1, 1] band (centre.X ± halfWidth), so all buckets stay within the field.
		if (xHist.PeakCount > 0)
		{
			double baseY = centre.Y + lobeHeight + 16;
			double maxH = Math.Min(20, (h - 6) - baseY);
			if (maxH > 1)
			{
				double histLeft = centre.X - halfWidth;
				int n = xHist.BucketCount;
				double slot = (halfWidth * 2.0) / n;
				double barW = Math.Max(1.0, slot - 1.5);
				context.DrawLine(axisPen, new Point(histLeft, baseY), new Point(centre.X + halfWidth, baseY));
				// Bars draw additively (scaled by the histogram-opacity knob) so they glow like
				// the desktop's; the thin axis baseline above stays alpha-over as crisp chrome.
				double barAlpha = 0.55 * confidence * Math.Clamp(HistogramOpacity, 0.0, 1.0);
				var bars = new List<(SKRect Rect, SKColor Col)>(n);
				for (int b = 0; b < n; b++)
				{
					int c = xHist.Counts[b];
					if (c == 0)
					{
						continue;
					}

					double bx = histLeft + ((b + 0.5) * slot);
					Color hue = bx >= centre.X ? Peach : Sky;
					double bh = maxH * (c / (double)xHist.PeakCount);
					bars.Add((new SKRect(
						(float)(bx - (barW / 2)), (float)baseY,
						(float)(bx + (barW / 2)), (float)(baseY + bh)), Sk(hue, barAlpha)));
				}

				context.Custom(new AdditiveSkiaLayer(new Rect(Bounds.Size), (canvas, paint) =>
				{
					paint.Style = SKPaintStyle.Fill;
					foreach ((SKRect rect, SKColor col) in bars)
					{
						paint.Color = col;
						canvas.DrawRect(rect, paint);
					}
				}));

				// Recovery arrows: cascade-pulsing left-pointing triangles when an alert is active.
				// Gated by Recovery.IsActive (true during Warning/Alerting).
				if (Recovery.IsActive)
				{
					double warnX = centre.X + (RegulationFieldCalculator.WarningBoundaryIndex * halfWidth);
					double zoneW = warnX - centre.X;
					double spacing = zoneW / 4.0;
					double arrowW = 7;
					double arrowH = 5;
					double arrowY = baseY + (maxH * 0.5);
					Color stateCol = StateColor;
					for (int i = 0; i < 3; i++)
					{
						double phase = (_animator.AnimTime * 3.5) - (i * 1.3);
						double alpha = Math.Clamp(0.25 + 0.65 * Math.Sin(phase), 0.1, 1.0);
						double ax = warnX - ((i + 1) * spacing);
						var geom = new StreamGeometry();
						using (var ctx = geom.Open())
						{
							ctx.BeginFigure(new Point(ax - (arrowW / 2), arrowY), true);
							ctx.LineTo(new Point(ax + (arrowW / 2), arrowY - arrowH));
							ctx.LineTo(new Point(ax + (arrowW / 2), arrowY + arrowH));
							ctx.EndFigure(true);
						}

						context.DrawGeometry(Brush(stateCol, alpha * confidence), null, geom);
					}
				}
			}
		}

		// Y axis (vagal tone), on the left margin, bars growing rightward. Endpoints follow the
		// marker's vagal-tone travel (FRAGILE at tone 0 on top, STEADY at tone 1 below), so each
		// bar sits at the exact height the marker rides for the tone its bucket counts.
		if (yHist.PeakCount > 0)
		{
			double axisX = 4;
			double maxW = Math.Min(20, (centre.X - halfWidth - 30) - axisX);
			if (maxW > 1)
			{
				int n = yHist.BucketCount;
				float markerYClamp = lobeHeight * MarkerYSpan;
				double topY = centre.Y + RegulationFieldGeometry.VagalToneOffsetY(0.0, markerYClamp);
				double span = markerYClamp;
				double slot = (2 * span) / n;
				double barH = Math.Max(1.0, slot - 1.5);
				context.DrawLine(axisPen, new Point(axisX, topY), new Point(axisX, centre.Y + span));
				double barAlpha = 0.55 * confidence * Math.Clamp(HistogramOpacity, 0.0, 1.0);
				SKColor barCol = Sk(Lavender, barAlpha);
				var bars = new List<SKRect>(n);
				for (int b = 0; b < n; b++)
				{
					int c = yHist.Counts[b];
					if (c == 0)
					{
						continue;
					}

					double by = topY + ((b + 0.5) * slot);
					double bw = maxW * (c / (double)yHist.PeakCount);
					bars.Add(new SKRect(
						(float)axisX, (float)(by - (barH / 2)),
						(float)(axisX + bw), (float)(by + (barH / 2))));
				}

				context.Custom(new AdditiveSkiaLayer(new Rect(Bounds.Size), (canvas, paint) =>
				{
					paint.Style = SKPaintStyle.Fill;
					paint.Color = barCol;
					foreach (SKRect rect in bars)
					{
						canvas.DrawRect(rect, paint);
					}
				}));
			}
		}
	}

	private void DrawTrail(DrawingContext context, Vector2 centre, float halfWidth, float lobeHeight, double confidence)
	{
		var trail = Trail;
		if (trail is null || trail.Count < 2)
		{
			return;
		}

		float markerYClamp = lobeHeight * MarkerYSpan;

		// Map every trail reading to its 2D field position: X = arousal index, Y = vagal tone
		// (FRAGILE up / STEADY down) — the same mapping as the live marker, so the comet records
		// the marker's true path. The head is the eased marker position, so the last segment
		// lands exactly on the marker.
		int count = trail.Count;
		var pts = new Vector2[count];
		for (int i = 0; i < count; i++)
		{
			Vector2 p = LemniscateGeometry.MarkerPoint((float)trail[i].Reading.Index, centre, halfWidth);
			p.Y += RegulationFieldGeometry.VagalToneOffsetY(trail[i].Reading.VagalTone, markerYClamp);
			pts[i] = p;
		}

		Vector2 head = LemniscateGeometry.MarkerPoint((float)_animator.MarkerPos, centre, halfWidth);
		head.Y += RegulationFieldGeometry.VagalToneOffsetY(Reading.VagalTone, markerYClamp);
		pts[^1] = head;

		// One smooth comet tail through the points (Catmull-Rom): oldest faint → newest bright,
		// thickening toward the head. Each segment keeps the colour of the state it was captured
		// under; the leading edge brightens with speed and tints by trend so the comet visibly
		// "leans" the way arousal is heading. Pre-computed here; the additive layer just strokes
		// the sub-segments so overlaps bloom rather than darken (mirrors the desktop comet).
		const int sub = 8;
		double speed = _animator.DisplayedSpeed;
		double trailOpacity = Math.Clamp(TrailOpacity, 0.0, 1.0);
		var trend = Dynamics.Trend;
		int segCount = (count - 1) * sub;
		var linePts = new SKPoint[segCount + 1];
		var cols = new SKColor[segCount];
		var widths = new float[segCount];
		int w = 0;
		linePts[0] = new SKPoint(pts[0].X, pts[0].Y);
		for (int i = 0; i < count - 1; i++)
		{
			Vector2 p0 = pts[Math.Max(0, i - 1)];
			Vector2 p1 = pts[i];
			Vector2 p2 = pts[i + 1];
			Vector2 p3 = pts[Math.Min(count - 1, i + 2)];

			Color segBase = MeltdownMonitor.Mobile.StateColors.ColorFor(trail[i].State);
			for (int s = 1; s <= sub; s++)
			{
				float t = s / (float)sub;
				Vector2 cur = CatmullRom(p0, p1, p2, p3, t);
				double frac = (i + t) / (count - 1);
				Color segCol = trend switch
				{
					RegulationTrend.Escalating => Lerp(segBase, Peach, frac * speed),
					RegulationTrend.DeEscalating => Lerp(segBase, Sky, frac * speed),
					_ => segBase,
				};
				double segAlpha = (0.55 + (0.3 * speed)) * frac * confidence * trailOpacity;
				cols[w] = Sk(segCol, segAlpha);
				widths[w] = (float)(1.0 + (2.5 * frac));
				w++;
				linePts[w] = new SKPoint(cur.X, cur.Y);
			}
		}

		context.Custom(new AdditiveSkiaLayer(new Rect(Bounds.Size), (canvas, paint) =>
		{
			paint.Style = SKPaintStyle.Stroke;
			paint.StrokeCap = SKStrokeCap.Butt;
			for (int i = 0; i < segCount; i++)
			{
				paint.Color = cols[i];
				paint.StrokeWidth = widths[i];
				canvas.DrawLine(linePts[i], linePts[i + 1], paint);
			}
		}));
	}

	// During an active episode, mark the warm-side warning boundary the marker must fall back
	// below and sweep a progress arc showing how close the body is to recovery (metrics back in
	// band, then held). Mirrors the desktop RegulationFieldView's recovery gate.
	private void DrawRecoveryTarget(DrawingContext context, Vector2 centre, float halfWidth, float lobeHeight, double confidence)
	{
		var recovery = Recovery;
		if (!recovery.IsActive)
		{
			return;
		}

		Vector2 g = LemniscateGeometry.MarkerPoint((float)RegulationFieldCalculator.WarningBoundaryIndex, centre, halfWidth);
		var gate = P(g);
		var goal = Brush(Green, confidence);

		const double ring = 11;
		context.DrawEllipse(null, new Pen(Brush(Green, 0.45 * confidence), 1.5), gate, ring, ring);
		context.DrawEllipse(goal, null, gate, 2.5, 2.5);

		// Two-stage recovery progress sweeping clockwise from 12 o'clock around the gate.
		DrawArc(context, gate, ring + 3, recovery.Overall, new Pen(goal, 2.5));

		if (recovery.Overall > 0.005)
		{
			DrawText(context, $"{recovery.Overall * 100:F0}%", new Point(gate.X, gate.Y + ring + 4), Green, 11, centred: true);
		}

		DrawText(context, "RECOVER", new Point(gate.X, gate.Y - (lobeHeight * 0.5) - 24), Green, 10, centred: true);
	}

	// A circular progress arc, clockwise from 12 o'clock, drawn as connected segments — matches
	// the manual point-and-DrawLine idiom the lemniscate trace uses.
	private static void DrawArc(DrawingContext context, Point centre, double radius, double fraction, IPen pen)
	{
		fraction = Math.Clamp(fraction, 0.0, 1.0);
		if (fraction <= 0.005)
		{
			return;
		}

		const int maxSegments = 48;
		int segments = Math.Max(1, (int)Math.Ceiling(maxSegments * fraction));
		double a0 = -Math.PI / 2;
		double sweep = fraction * 2 * Math.PI;
		var prev = new Point(centre.X + (radius * Math.Cos(a0)), centre.Y + (radius * Math.Sin(a0)));
		for (int i = 1; i <= segments; i++)
		{
			double a = a0 + (sweep * i / segments);
			var p = new Point(centre.X + (radius * Math.Cos(a)), centre.Y + (radius * Math.Sin(a)));
			context.DrawLine(pen, prev, p);
			prev = p;
		}
	}

	private void DrawMarker(DrawingContext context, Vector2 centre, float halfWidth, float lobeHeight, double confidence)
	{
		// Eased position glides between the multi-second samples; the halo
		// pulses at the current HR cadence (RegulationFieldAnimator).
		Vector2 p = LemniscateGeometry.MarkerPoint((float)_animator.MarkerPos, centre, halfWidth);
		// Y encodes vagal tone: grounded/low (STEADY) when HRV is healthy, lifted toward
		// FRAGILE as it collapses — the same vertical mapping as the desktop marker.
		p.Y += RegulationFieldGeometry.VagalToneOffsetY(Reading.VagalTone, lobeHeight * MarkerYSpan);
		var at = P(p);

		// The two surrounding halos glow additively (overlap with the trail head and each other
		// blooms toward white); the solid core and pupil below stay alpha-over so they read as
		// crisp, opaque points — the same split the desktop marker uses.
		var skAt = new SKPoint(p.X, p.Y);
		float halo = (float)(14 * _animator.HaloPulse);
		SKColor pulseCol = Sk(StateColor, 0.18 * confidence);
		double collapse = HypoarousalVisual.Intensity(Hypoarousal);
		float collapseRing = (float)(14 + (10 * collapse));
		SKColor collapseCol = Sk(Slate, 0.30 * collapse * confidence);
		context.Custom(new AdditiveSkiaLayer(new Rect(Bounds.Size), (canvas, paint) =>
		{
			paint.Style = SKPaintStyle.Fill;
			paint.Color = pulseCol;
			canvas.DrawCircle(skAt, halo, paint);
			// Outer collapse halo: Slate, non-pulsing, radius grows with the collapse signal.
			// Layered outside the pulsing state halo so the two read as distinct.
			if (collapse > 0.0)
			{
				paint.Color = collapseCol;
				canvas.DrawCircle(skAt, collapseRing, paint);
			}
		}));

		context.DrawEllipse(Brush(StateColor, confidence), null, at, 6, 6);              // core
		context.DrawEllipse(Brush(Base, confidence), null, at, 2.5, 2.5);                // pupil
		DrawVelocityArrow(context, at, confidence);
	}

	private void DrawVelocityArrow(DrawingContext context, Point markerAt, double confidence)
	{
		if (confidence < 0.999)
		{
			return;
		}

		double scalar = Hypoarousal;

		// A rising collapse signal overrides the index arrow with a Slate WARNING toward the cool/left
		// (shutdown) side — never let a slide into collapse read as a calming de-escalation.
		if (HypoarousalVisual.ShowCollapseArrow(scalar, HypoarousalDynamics))
		{
			// dir=-1 → toward cool/left/REST (shutdown side)
			DrawArrow(context, markerAt, -1.0, Slate, confidence, HypoarousalDynamics.NormalizedSpeed);
			return;
		}

		var dyn = Dynamics;
		double speed = _animator.DisplayedSpeed;

		// Suppress the index arrow when it would contradict the shutdown zone (de-escalating
		// while collapse signal is present would read as calming — it isn't).
		if (HypoarousalVisual.SuppressIndexArrow(scalar, dyn))
		{
			return;
		}

		// Index-only floor — evaluated AFTER the collapse branch so a rising collapse can show
		// even when the index trend is Steady.
		if (dyn.Trend == RegulationTrend.Steady || speed < 0.02)
		{
			return;
		}

		double dir = dyn.Trend == RegulationTrend.Escalating ? 1.0 : -1.0;
		Color hue = dyn.Trend == RegulationTrend.Escalating ? Peach : Sky;
		DrawArrow(context, markerAt, dir, hue, confidence, speed);
	}

	private static void DrawArrow(DrawingContext context, Point markerAt, double dir, Color hue, double confidence, double speed)
	{
		double alpha = confidence * (0.35 + (0.65 * speed));

		double gap = 12.0;
		double len = 10.0 + (speed * 46.0);
		var start = new Point(markerAt.X + (dir * gap), markerAt.Y);
		var tip = new Point(start.X + (dir * len), start.Y);
		context.DrawLine(new Pen(Brush(hue, alpha), 3), start, tip);

		const double head = 7.0;
		var geo = new StreamGeometry();
		using (var g = geo.Open())
		{
			g.BeginFigure(tip, isFilled: true);
			g.LineTo(new Point(tip.X - (dir * head), tip.Y - (head * 0.7)));
			g.LineTo(new Point(tip.X - (dir * head), tip.Y + (head * 0.7)));
			g.EndFigure(isClosed: true);
		}

		context.DrawGeometry(Brush(hue, alpha), null, geo);
	}

	private static void DrawDashedVertical(DrawingContext context, double x, double yTop, double yBot,
		IBrush brush, double thick, double dash, double gap)
	{
		var pen = new Pen(brush, thick);
		double step = dash + gap;
		for (double y = yTop; y < yBot; y += step)
		{
			context.DrawLine(pen, new Point(x, y), new Point(x, Math.Min(y + dash, yBot)));
		}
	}

	private static void DrawLabels(DrawingContext context, Point centre, float halfWidth, float lobeHeight)
	{
		DrawText(context, "REST", new Point(centre.X - halfWidth, centre.Y + 4), Sapphire, 11, centred: true);
		DrawText(context, "MELTDOWN", new Point(centre.X + halfWidth, centre.Y + 4), Peach, 11, centred: true);
		DrawText(context, "WINDOW OF TOLERANCE", new Point(centre.X, centre.Y - lobeHeight - 18), Subtext0, 10, centred: true);
	}

	private static void DrawText(DrawingContext context, string text, Point at, Color color, double size, bool centred = false)
	{
		var ft = new FormattedText(
			text,
			CultureInfo.CurrentCulture,
			FlowDirection.LeftToRight,
			Typeface.Default,
			size,
			new SolidColorBrush(color));

		var origin = centred ? new Point(at.X - (ft.Width / 2), at.Y) : at;
		context.DrawText(ft, origin);
	}

	private static Point P(Vector2 v) => new(v.X, v.Y);

	/// <summary>Unit normal to the segment a→b, used to push the jittered trace
	/// sideways off the ghost baseline.</summary>
	private static Vector2 Normal(Vector2 a, Vector2 b)
	{
		Vector2 d = b - a;
		float len = d.Length();
		return len < 1e-4f ? Vector2.Zero : new Vector2(-d.Y, d.X) / len;
	}

	private static IBrush Brush(Color c, double opacity) =>
		new SolidColorBrush(c, Math.Clamp(opacity, 0.0, 1.0));

	private static Color Lerp(Color a, Color b, double t)
	{
		t = Math.Clamp(t, 0.0, 1.0);
		static byte Mix(byte x, byte y, double f) => (byte)Math.Round(x + ((y - x) * f));
		return Color.FromRgb(Mix(a.R, b.R, t), Mix(a.G, b.G, t), Mix(a.B, b.B, t));
	}
}
