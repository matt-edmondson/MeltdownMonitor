using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Regulation;

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

	private readonly RegulationFieldAnimator _animator = new();
	private readonly Stopwatch _clock = new();
	private DispatcherTimer? _timer;
	private TimeSpan _lastFrame;

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

		// Window of tolerance: a soft lavender zone marking the regulated centre.
		context.DrawEllipse(Brush(Lavender, 0.08 * confidence), null, centre, halfWidth * 0.32, lobeHeight * 0.7);

		DrawShutdownZone(context, centreV, halfWidth, lobeHeight, confidence);

		var ghost = LemniscateGeometry.Polyline(centreV, halfWidth, lobeHeight,
			Math.Clamp(LobeSegments, LemniscateGeometry.MinSegments, LemniscateGeometry.MaxSegments));
		DrawTrace(context, ghost, centreV, halfWidth, reading, confidence);
		DrawAxisHistograms(context, centre, w, h, halfWidth, lobeHeight, confidence);

		// Warning threshold dashed lines on the field: vertical markers at ±WarningBoundaryIndex
		// (0.6) that the moving marker/trail crosses as arousal rises or falls.
		{
			double warnOff = RegulationFieldCalculator.WarningBoundaryIndex * halfWidth;
			double topY = centre.Y - (lobeHeight * 0.92);
			double botY = centre.Y + (lobeHeight * 0.92);
			DrawDashedVertical(context, centre.X + warnOff, topY, botY, Brush(Peach, 0.28 * confidence), 1, 4, 3);
			DrawDashedVertical(context, centre.X - warnOff, topY, botY, Brush(Sky, 0.28 * confidence), 1, 4, 3);
		}

		DrawTrail(context, centreV, halfWidth, confidence);
		DrawRecoveryTarget(context, centreV, halfWidth, lobeHeight, confidence);
		DrawMarker(context, centreV, halfWidth, confidence);

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

	private void DrawTrace(DrawingContext context, IReadOnlyList<Vector2> ghost, Vector2 centre, float halfWidth, RegulationReading r, double confidence)
	{
		if (ghost.Count < 2)
		{
			return;
		}

		// Ghost baseline (symmetric resting frame).
		var ghostPen = new Pen(Brush(Overlay1, 0.28 * confidence), 1.5);
		for (int i = 0; i < ghost.Count; i++)
		{
			context.DrawLine(ghostPen, P(ghost[i]), P(ghost[(i + 1) % ghost.Count]));
		}

		// Live two-tone trace: the warm (right) lobe swells with positive index,
		// the cool (left) lobe with negative; stroke thins as variability collapses.
		float clampedIndex = Math.Clamp((float)r.Index, -1f, 1f);
		float warmSwell = 1f + (MathF.Max(0f, clampedIndex) * 1.4f);
		float coolSwell = 1f + (MathF.Max(0f, -clampedIndex) * 1.4f);
		float quality = (float)Math.Clamp(r.VariabilityQuality, 0.0, 1.0);
		double baseThick = (3.0 + (4.0 * quality)) * Math.Clamp(LobeThickness, 0.5, 3.0);

		for (int i = 0; i < ghost.Count; i++)
		{
			Vector2 a = ghost[i];
			Vector2 b = ghost[(i + 1) % ghost.Count];
			float midX = (a.X + b.X) * 0.5f;
			bool warm = midX >= centre.X;
			double depth = Math.Min(1.0, Math.Abs(midX - centre.X) / halfWidth);

			Color c = warm
				? Lerp(Peach, Maroon, depth)
				: Lerp(Sky, Sapphire, depth);

			// Animated variability jitter on the outer half of each lobe: the
			// healthier the variability, the more the line shifts sideways.
			Vector2 n = Normal(a, b) * (float)_animator.JitterOffset(i, quality, depth);

			double thick = baseThick * (warm ? warmSwell : coolSwell);
			context.DrawLine(new Pen(Brush(c, confidence), thick), P(a + n), P(b + n));
		}
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

		// X axis (arousal index), below the field, bars growing downward.
		// Axis range is dynamic: at least [-1, 1] but expands for extreme index readings.
		if (xHist.PeakCount > 0)
		{
			double baseY = centre.Y + lobeHeight + 16;
			double maxH = Math.Min(20, (h - 6) - baseY);
			if (maxH > 1)
			{
				double histLeft = centre.X + (xHist.Min * halfWidth);
				double histRight = centre.X + (xHist.Max * halfWidth);
				double totalW = histRight - histLeft;
				int n = xHist.BucketCount;
				double slot = totalW / n;
				double barW = Math.Max(1.0, slot - 1.5);
				context.DrawLine(axisPen, new Point(histLeft, baseY), new Point(histRight, baseY));
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
					context.FillRectangle(Brush(hue, 0.55 * confidence), new Rect(bx - (barW / 2), baseY, barW, bh));
				}

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

		// Y axis (vagal tone), on the left margin, bars growing rightward.
		if (yHist.PeakCount > 0)
		{
			double axisX = 4;
			double maxW = Math.Min(20, (centre.X - halfWidth - 30) - axisX);
			if (maxW > 1)
			{
				int n = yHist.BucketCount;
				double span = lobeHeight * 0.8;
				double topY = centre.Y - span;
				double slot = (2 * span) / n;
				double barH = Math.Max(1.0, slot - 1.5);
				context.DrawLine(axisPen, new Point(axisX, topY), new Point(axisX, centre.Y + span));
				for (int b = 0; b < n; b++)
				{
					int c = yHist.Counts[b];
					if (c == 0)
					{
						continue;
					}

					double by = topY + ((b + 0.5) * slot);
					double bw = maxW * (c / (double)yHist.PeakCount);
					context.FillRectangle(Brush(Lavender, 0.55 * confidence), new Rect(axisX, by - (barH / 2), bw, barH));
				}
			}
		}
	}

	private void DrawTrail(DrawingContext context, Vector2 centre, float halfWidth, double confidence)
	{
		var trail = Trail;
		if (trail is null || trail.Count < 2)
		{
			return;
		}

		// Oldest faint → newest bright, ending just behind the marker.
		for (int i = 0; i < trail.Count - 1; i++)
		{
			double frac = i / (double)(trail.Count - 1);
			Vector2 p = LemniscateGeometry.MarkerPoint((float)trail[i].Reading.Index, centre, halfWidth);
			double radius = 1.5 + (3.0 * frac);
			// Each point keeps the colour of the state it was captured under, so the trail
			// records the journey through states rather than recolouring to the current one.
			Color stateCol = MeltdownMonitor.Mobile.StateColors.ColorFor(trail[i].State);
			// Leading edge (newest, frac->1) brightens with speed and tints by trend so the
			// comet visibly "leans" the way arousal is heading; older segments stay their own colour.
			Color tint = Dynamics.Trend switch
			{
				RegulationTrend.Escalating => Lerp(stateCol, Peach, frac * _animator.DisplayedSpeed),
				RegulationTrend.DeEscalating => Lerp(stateCol, Sky, frac * _animator.DisplayedSpeed),
				_ => stateCol,
			};
			double alpha = (0.5 + (0.3 * _animator.DisplayedSpeed)) * frac * confidence;
			context.DrawEllipse(Brush(tint, alpha), null, P(p), radius, radius);
		}
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

	private void DrawMarker(DrawingContext context, Vector2 centre, float halfWidth, double confidence)
	{
		// Eased position glides between the multi-second samples; the halo
		// pulses at the current HR cadence (RegulationFieldAnimator).
		Vector2 p = LemniscateGeometry.MarkerPoint((float)_animator.MarkerPos, centre, halfWidth);
		var at = P(p);
		double halo = 14 * _animator.HaloPulse;
		context.DrawEllipse(Brush(StateColor, 0.18 * confidence), null, at, halo, halo); // halo
		// Outer collapse halo: Slate, non-pulsing, radius grows with the collapse signal. Layers
		// outside the pulsing state halo so the two read as distinct (different colour + motion).
		double collapse = HypoarousalVisual.Intensity(Hypoarousal);
		if (collapse > 0.0)
		{
			double ring = 14 + (10 * collapse);
			context.DrawEllipse(Brush(Slate, 0.30 * collapse * confidence), null, at, ring, ring);
		}

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
