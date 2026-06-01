using System.Numerics;
using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.App.Regulation;

/// <summary>Catppuccin Macchiato palette (linear RGBA 0..1) used across the UI.</summary>
internal static class MacchiatoPalette
{
	public static readonly Vector4 Base = Hex(0x24, 0x27, 0x3a);
	public static readonly Vector4 Mantle = Hex(0x1e, 0x20, 0x30);
	public static readonly Vector4 Text = Hex(0xca, 0xd3, 0xf5);
	public static readonly Vector4 Subtext0 = Hex(0xa5, 0xad, 0xcb);
	public static readonly Vector4 Overlay1 = Hex(0x80, 0x87, 0xa2);
	public static readonly Vector4 Lavender = Hex(0xb7, 0xbd, 0xf8);

	/// <summary>Collapse / dorsal-vagal "shutdown" hue — a dim, desaturated slate-indigo that reads
	/// cold/withdrawn, deliberately distinct from the soft <see cref="Lavender"/> used for the
	/// window-of-tolerance and crossover. First-cut shade; live-tune against a real sensor.</summary>
	public static readonly Vector4 Slate = Hex(0x5d, 0x6a, 0x9e);
	public static readonly Vector4 Sky = Hex(0x91, 0xd7, 0xe3);
	public static readonly Vector4 Sapphire = Hex(0x7d, 0xc4, 0xe4);
	public static readonly Vector4 Green = Hex(0xa6, 0xda, 0x95);
	public static readonly Vector4 Peach = Hex(0xf5, 0xa9, 0x7f);
	public static readonly Vector4 Maroon = Hex(0xee, 0x99, 0xa0);
	public static readonly Vector4 Red = Hex(0xed, 0x87, 0x96);

	/// <summary>Detector-state accent, per the branding spec's state colour mapping.</summary>
	public static Vector4 State(DetectorState state) => state switch
	{
		DetectorState.Idle => Overlay1,
		DetectorState.Watching => Green,
		DetectorState.Warning => Peach,
		DetectorState.Alerting => Red,
		DetectorState.Cooldown => Sapphire,
		_ => Overlay1,
	};

	public static Vector4 Lerp(Vector4 a, Vector4 b, float t) => Vector4.Lerp(a, b, Math.Clamp(t, 0f, 1f));

	public static Vector4 WithAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);

	private static Vector4 Hex(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);
}
