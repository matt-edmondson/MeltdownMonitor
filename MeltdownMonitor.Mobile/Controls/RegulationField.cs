using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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
/// the desktop view is specified against. There is no per-frame animation loop on
/// mobile: the control re-renders when a new <see cref="Reading"/> arrives
/// (~every few seconds), so the desktop's breathing/jitter flourishes are
/// intentionally omitted for v1.
/// </summary>
public sealed class RegulationField : Control
{
	private const int LobeSegments = 96;

	public static readonly StyledProperty<RegulationReading> ReadingProperty =
		AvaloniaProperty.Register<RegulationField, RegulationReading>(
			nameof(Reading), new RegulationReading(0.0, 1.0, 0.0));

	public static readonly StyledProperty<IReadOnlyList<RegulationReading>?> TrailProperty =
		AvaloniaProperty.Register<RegulationField, IReadOnlyList<RegulationReading>?>(nameof(Trail));

	public static readonly StyledProperty<Color> StateColorProperty =
		AvaloniaProperty.Register<RegulationField, Color>(
			nameof(StateColor), Color.FromRgb(0x29, 0x80, 0xD8));

	// Catppuccin Macchiato — the field's distinctive palette, single-sourced here
	// to match the desktop renderer's MacchiatoPalette.
	private static readonly Color Base = Color.FromRgb(0x24, 0x27, 0x3a);
	private static readonly Color Text = Color.FromRgb(0xca, 0xd3, 0xf5);
	private static readonly Color Subtext0 = Color.FromRgb(0xa5, 0xad, 0xcb);
	private static readonly Color Overlay1 = Color.FromRgb(0x80, 0x87, 0xa2);
	private static readonly Color Lavender = Color.FromRgb(0xb7, 0xbd, 0xf8);
	private static readonly Color Sky = Color.FromRgb(0x91, 0xd7, 0xe3);
	private static readonly Color Sapphire = Color.FromRgb(0x7d, 0xc4, 0xe4);
	private static readonly Color Peach = Color.FromRgb(0xf5, 0xa9, 0x7f);
	private static readonly Color Maroon = Color.FromRgb(0xee, 0x99, 0xa0);

	static RegulationField() =>
		AffectsRender<RegulationField>(ReadingProperty, TrailProperty, StateColorProperty);

	/// <summary>Latest arousal-vs-baseline reading; drives the marker position,
	/// stroke fatness and overall confidence dimming.</summary>
	public RegulationReading Reading
	{
		get => GetValue(ReadingProperty);
		set => SetValue(ReadingProperty, value);
	}

	/// <summary>Recent readings, oldest first, drawn as a fading comet trail
	/// along the major axis.</summary>
	public IReadOnlyList<RegulationReading>? Trail
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

		var ghost = LemniscateGeometry.Polyline(centreV, halfWidth, lobeHeight, LobeSegments);
		DrawTrace(context, ghost, centreV, halfWidth, reading, confidence);
		DrawTrail(context, centreV, halfWidth, confidence);
		DrawMarker(context, centreV, halfWidth, reading, confidence);

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
		float warmSwell = 1f + (MathF.Max(0f, (float)r.Index) * 1.4f);
		float coolSwell = 1f + (MathF.Max(0f, -(float)r.Index) * 1.4f);
		float quality = (float)Math.Clamp(r.VariabilityQuality, 0.0, 1.0);
		double baseThick = 3.0 + (4.0 * quality);

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

			double thick = baseThick * (warm ? warmSwell : coolSwell);
			context.DrawLine(new Pen(Brush(c, confidence), thick), P(a), P(b));
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
			Vector2 p = LemniscateGeometry.MarkerPoint((float)trail[i].Index, centre, halfWidth);
			double radius = 1.5 + (3.0 * frac);
			context.DrawEllipse(Brush(StateColor, 0.5 * frac * confidence), null, P(p), radius, radius);
		}
	}

	private void DrawMarker(DrawingContext context, Vector2 centre, float halfWidth, RegulationReading r, double confidence)
	{
		Vector2 p = LemniscateGeometry.MarkerPoint((float)r.Index, centre, halfWidth);
		var at = P(p);
		context.DrawEllipse(Brush(StateColor, 0.18 * confidence), null, at, 14, 14); // halo
		context.DrawEllipse(Brush(StateColor, confidence), null, at, 6, 6);          // core
		context.DrawEllipse(Brush(Base, confidence), null, at, 2.5, 2.5);            // pupil
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

	private static IBrush Brush(Color c, double opacity) =>
		new SolidColorBrush(c, Math.Clamp(opacity, 0.0, 1.0));

	private static Color Lerp(Color a, Color b, double t)
	{
		t = Math.Clamp(t, 0.0, 1.0);
		static byte Mix(byte x, byte y, double f) => (byte)Math.Round(x + ((y - x) * f));
		return Color.FromRgb(Mix(a.R, b.R, t), Mix(a.G, b.G, t), Mix(a.B, b.B, t));
	}
}
