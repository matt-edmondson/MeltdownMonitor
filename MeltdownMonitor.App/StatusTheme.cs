using Hexa.NET.ImGui;
using ktsu.ThemeProvider.ImGui;
using ktsu.ThemeProvider.Themes.Catppuccin;
using MeltdownMonitor.Core.Detection;
using System.Numerics;

namespace MeltdownMonitor.App;

/// <summary>
/// Themes the status window with Catppuccin Macchiato (via ktsu.ThemeProvider) and re-tints
/// the interactive accent colours to reflect the current <see cref="DetectorState"/>: a neutral
/// grey when idle, green while watching, peach on warning, red on alert, blue during cooldown.
/// The Macchiato surface/background/text palette stays constant across states so only the
/// "mood" of the controls shifts — the window never stops looking like Macchiato.
/// </summary>
internal sealed class StatusTheme
{
	// Catppuccin Macchiato accent colours, per the official palette (https://catppuccin.com/palette).
	private static readonly Vector4 Overlay0 = FromHex(0x6E, 0x73, 0x8D); // idle — neutral grey
	private static readonly Vector4 Green = FromHex(0xA6, 0xDA, 0x95);    // watching
	private static readonly Vector4 Peach = FromHex(0xF5, 0xA9, 0x7F);    // warning
	private static readonly Vector4 Red = FromHex(0xED, 0x87, 0x96);      // alerting
	private static readonly Vector4 Blue = FromHex(0x8A, 0xAD, 0xF4);     // cooldown

	// The full Macchiato → ImGui colour mapping, computed once. Re-applied wholesale on every
	// state change so a new tint cleanly replaces the previous one rather than layering on it.
	private readonly IReadOnlyDictionary<ImGuiCol, Vector4> _basePalette =
		new ImGuiPaletteMapper().MapTheme(new Macchiato());

	private bool _applied;
	private DetectorState _appliedState;

	/// <summary>
	/// Applies the Macchiato base palette tinted for <paramref name="state"/>. No-ops when the
	/// state is unchanged, so it is cheap to call once per frame. Mutates the persistent ImGui
	/// style, so the change sticks until the next state transition.
	/// </summary>
	public void Apply(DetectorState state)
	{
		if (_applied && state == _appliedState)
		{
			return;
		}

		Span<Vector4> colors = ImGui.GetStyle().Colors;

		foreach ((ImGuiCol col, Vector4 value) in _basePalette)
		{
			colors[(int)col] = value;
		}

		ApplyAccent(colors, AccentFor(state));

		_applied = true;
		_appliedState = state;
	}

	private static Vector4 AccentFor(DetectorState state) => state switch
	{
		DetectorState.Watching => Green,
		DetectorState.Warning => Peach,
		DetectorState.Alerting => Red,
		DetectorState.Cooldown => Blue,
		_ => Overlay0,
	};

	// Overlay the state accent onto the interactive widget colours, leaving the Macchiato
	// backgrounds, text and borders untouched. Resting controls use a dimmed accent so they
	// read against the dark base; hover/active brighten toward the pure accent.
	private static void ApplyAccent(Span<Vector4> colors, Vector4 accent)
	{
		colors[(int)ImGuiCol.CheckMark] = accent;

		colors[(int)ImGuiCol.SliderGrab] = Dim(accent, 0.85f);
		colors[(int)ImGuiCol.SliderGrabActive] = accent;

		colors[(int)ImGuiCol.Button] = Dim(accent, 0.70f);
		colors[(int)ImGuiCol.ButtonHovered] = Dim(accent, 0.85f);
		colors[(int)ImGuiCol.ButtonActive] = accent;

		colors[(int)ImGuiCol.Header] = Dim(accent, 0.55f);
		colors[(int)ImGuiCol.HeaderHovered] = Dim(accent, 0.75f);
		colors[(int)ImGuiCol.HeaderActive] = Dim(accent, 0.90f);

		colors[(int)ImGuiCol.Tab] = Dim(accent, 0.45f);
		colors[(int)ImGuiCol.TabHovered] = Dim(accent, 0.80f);
		colors[(int)ImGuiCol.TabSelected] = Dim(accent, 0.65f);

		colors[(int)ImGuiCol.SeparatorHovered] = Dim(accent, 0.80f);
		colors[(int)ImGuiCol.SeparatorActive] = accent;

		colors[(int)ImGuiCol.ResizeGrip] = WithAlpha(accent, 0.25f);
		colors[(int)ImGuiCol.ResizeGripHovered] = WithAlpha(accent, 0.60f);
		colors[(int)ImGuiCol.ResizeGripActive] = WithAlpha(accent, 0.90f);

		colors[(int)ImGuiCol.FrameBgHovered] = WithAlpha(accent, 0.22f);
		colors[(int)ImGuiCol.FrameBgActive] = WithAlpha(accent, 0.35f);

		colors[(int)ImGuiCol.TitleBgActive] = Dim(accent, 0.45f);
		colors[(int)ImGuiCol.TextSelectedBg] = WithAlpha(accent, 0.35f);
		colors[(int)ImGuiCol.NavWindowingHighlight] = WithAlpha(accent, 0.70f);
	}

	private static Vector4 Dim(Vector4 c, float factor) => new(c.X * factor, c.Y * factor, c.Z * factor, c.W);

	private static Vector4 WithAlpha(Vector4 c, float alpha) => new(c.X, c.Y, c.Z, alpha);

	private static Vector4 FromHex(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, 1f);
}
