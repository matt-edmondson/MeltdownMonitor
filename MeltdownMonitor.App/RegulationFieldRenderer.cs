using Hexa.NET.ImGui;
using MeltdownMonitor.Core.Regulation;
using System.Numerics;

namespace MeltdownMonitor.App;

/// <summary>
/// Draws the Regulation Field — the signature figure-8 (lemniscate) "window of tolerance"
/// instrument — into the current ImGui window using its draw list. A needle marker slides
/// from the cool REST lobe (left) through the centre to the warm MELTDOWN lobe (right) as
/// arousal rises above baseline; a comet trail shows the recent trajectory; stroke fatness
/// tracks variability quality; the whole field dims while the baseline is still calibrating.
///
/// This is the ImGui port of <c>MeltdownMonitor.Mobile.Controls.RegulationField</c>, reusing
/// the same Core <see cref="LemniscateGeometry"/> maths and Catppuccin Macchiato palette.
/// </summary>
public static class RegulationFieldRenderer
{
	private const int LobeSegments = 96;

	// Catppuccin Macchiato — matches the Mobile renderer's palette.
	private static readonly Vector4 Base = Rgb(0x24, 0x27, 0x3a);
	private static readonly Vector4 Text = Rgb(0xca, 0xd3, 0xf5);
	private static readonly Vector4 Subtext0 = Rgb(0xa5, 0xad, 0xcb);
	private static readonly Vector4 Overlay1 = Rgb(0x80, 0x87, 0xa2);
	private static readonly Vector4 Lavender = Rgb(0xb7, 0xbd, 0xf8);
	private static readonly Vector4 Sky = Rgb(0x91, 0xd7, 0xe3);
	private static readonly Vector4 Sapphire = Rgb(0x7d, 0xc4, 0xe4);
	private static readonly Vector4 Peach = Rgb(0xf5, 0xa9, 0x7f);
	private static readonly Vector4 Maroon = Rgb(0xee, 0x99, 0xa0);

	/// <summary>
	/// Reserves a <paramref name="size"/> region at the current cursor and draws the field into it.
	/// </summary>
	/// <param name="trail">Recent readings, oldest first, drawn as a fading comet trail.</param>
	/// <param name="stateColor">The detector-state accent for the marker and trail.</param>
	public static void Draw(Vector2 size, RegulationReading reading, IReadOnlyList<RegulationReading> trail, Vector4 stateColor)
	{
		if (size.X <= 4 || size.Y <= 4)
		{
			return;
		}

		Vector2 origin = ImGui.GetCursorScreenPos();
		ImGui.Dummy(size);

		var draw = ImGui.GetWindowDrawList();
		double confidence = Math.Clamp(reading.Confidence, 0.0, 1.0);

		Vector2 centre = origin + (size * 0.5f);
		float halfWidth = MathF.Min(size.X * 0.40f, 240f);
		float lobeHeight = MathF.Min(size.Y * 0.34f, halfWidth * 0.62f);

		// Window of tolerance: a soft lavender zone marking the regulated centre.
		draw.AddEllipseFilled(centre, new Vector2(halfWidth * 0.32f, lobeHeight * 0.7f), Col(Lavender, 0.08 * confidence));

		var ghost = LemniscateGeometry.Polyline(centre, halfWidth, lobeHeight, LobeSegments);
		DrawTrace(draw, ghost, centre, halfWidth, reading, confidence);
		DrawTrail(draw, trail, centre, halfWidth, stateColor, confidence);
		DrawMarker(draw, centre, halfWidth, reading, stateColor, confidence);

		// Crossover node at the centre of the figure-8.
		draw.AddCircleFilled(centre, 6f, Col(Lavender, confidence));
		draw.AddCircleFilled(centre, 2.5f, Col(Text, confidence));

		DrawLabels(draw, centre, halfWidth, lobeHeight);

		if (confidence < 0.999)
		{
			Centred(draw, $"Calibrating baseline… {confidence * 100:F0}%",
				new Vector2(centre.X, centre.Y + lobeHeight + 14f), Col(Subtext0, 1.0));
		}
	}

	private static void DrawTrace(ImDrawListPtr draw, IReadOnlyList<Vector2> ghost, Vector2 centre, float halfWidth, RegulationReading r, double confidence)
	{
		if (ghost.Count < 2)
		{
			return;
		}

		// Ghost baseline (symmetric resting frame).
		uint ghostCol = Col(Overlay1, 0.28 * confidence);
		for (int i = 0; i < ghost.Count; i++)
		{
			draw.AddLine(ghost[i], ghost[(i + 1) % ghost.Count], ghostCol, 1.5f);
		}

		// Live two-tone trace: the warm (right) lobe swells with positive index, the cool
		// (left) lobe with negative; stroke thins as variability collapses.
		float warmSwell = 1f + (MathF.Max(0f, (float)r.Index) * 1.4f);
		float coolSwell = 1f + (MathF.Max(0f, -(float)r.Index) * 1.4f);
		float quality = (float)Math.Clamp(r.VariabilityQuality, 0.0, 1.0);
		float baseThick = 3f + (4f * quality);

		for (int i = 0; i < ghost.Count; i++)
		{
			Vector2 a = ghost[i];
			Vector2 b = ghost[(i + 1) % ghost.Count];
			float midX = (a.X + b.X) * 0.5f;
			bool warm = midX >= centre.X;
			float depth = MathF.Min(1f, MathF.Abs(midX - centre.X) / halfWidth);

			Vector4 c = warm ? Lerp(Peach, Maroon, depth) : Lerp(Sky, Sapphire, depth);
			float thick = baseThick * (warm ? warmSwell : coolSwell);
			draw.AddLine(a, b, Col(c, confidence), thick);
		}
	}

	private static void DrawTrail(ImDrawListPtr draw, IReadOnlyList<RegulationReading> trail, Vector2 centre, float halfWidth, Vector4 stateColor, double confidence)
	{
		if (trail.Count < 2)
		{
			return;
		}

		// Oldest faint → newest bright, ending just behind the marker.
		for (int i = 0; i < trail.Count - 1; i++)
		{
			float frac = i / (float)(trail.Count - 1);
			Vector2 p = LemniscateGeometry.MarkerPoint((float)trail[i].Index, centre, halfWidth);
			float radius = 1.5f + (3f * frac);
			draw.AddCircleFilled(p, radius, Col(stateColor, 0.5 * frac * confidence));
		}
	}

	private static void DrawMarker(ImDrawListPtr draw, Vector2 centre, float halfWidth, RegulationReading r, Vector4 stateColor, double confidence)
	{
		Vector2 p = LemniscateGeometry.MarkerPoint((float)r.Index, centre, halfWidth);
		draw.AddCircleFilled(p, 14f, Col(stateColor, 0.18 * confidence)); // halo
		draw.AddCircleFilled(p, 6f, Col(stateColor, confidence));         // core
		draw.AddCircleFilled(p, 2.5f, Col(Base, confidence));            // pupil
	}

	private static void DrawLabels(ImDrawListPtr draw, Vector2 centre, float halfWidth, float lobeHeight)
	{
		Centred(draw, "REST", new Vector2(centre.X - halfWidth, centre.Y + 4f), Col(Sapphire, 1.0));
		Centred(draw, "MELTDOWN", new Vector2(centre.X + halfWidth, centre.Y + 4f), Col(Peach, 1.0));
		Centred(draw, "WINDOW OF TOLERANCE", new Vector2(centre.X, centre.Y - lobeHeight - 18f), Col(Subtext0, 1.0));
	}

	private static void Centred(ImDrawListPtr draw, string text, Vector2 at, uint col)
	{
		Vector2 textSize = ImGui.CalcTextSize(text);
		draw.AddText(new Vector2(at.X - (textSize.X * 0.5f), at.Y), col, text);
	}

	private static uint Col(Vector4 c, double alpha) =>
		ImGui.GetColorU32(new Vector4(c.X, c.Y, c.Z, (float)Math.Clamp(alpha, 0.0, 1.0)));

	private static Vector4 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);

	private static Vector4 Lerp(Vector4 a, Vector4 b, float t)
	{
		t = Math.Clamp(t, 0f, 1f);
		return new Vector4(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t), 1f);
	}
}
