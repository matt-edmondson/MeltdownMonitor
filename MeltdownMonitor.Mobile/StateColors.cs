using Avalonia.Media;
using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.Mobile;

/// <summary>
/// State → colour palette for the mobile UI. Matches the palette named in
/// the iOS design doc §12 Phase 3: Idle grey, Watching blue, Warning amber,
/// Alerting red, Cooldown violet, Paused muted.
/// </summary>
public static class StateColors
{
	public static readonly Color Idle = Color.FromRgb(0x8E, 0x8E, 0x93);
	public static readonly Color Watching = Color.FromRgb(0x29, 0x80, 0xD8);
	public static readonly Color Warning = Color.FromRgb(0xF5, 0xA6, 0x23);
	public static readonly Color Alerting = Color.FromRgb(0xE0, 0x4A, 0x3F);
	public static readonly Color Cooldown = Color.FromRgb(0x8B, 0x5C, 0xF6);
	public static readonly Color Paused = Color.FromRgb(0x55, 0x55, 0x55);

	public static Color ColorFor(DetectorState state, bool isPaused = false) =>
		isPaused
			? Paused
			: state switch
			{
				DetectorState.Idle => Idle,
				DetectorState.Watching => Watching,
				DetectorState.Warning => Warning,
				DetectorState.Alerting => Alerting,
				DetectorState.Cooldown => Cooldown,
				_ => Idle,
			};

	public static IBrush BrushFor(DetectorState state, bool isPaused = false) =>
		new SolidColorBrush(ColorFor(state, isPaused));

	public static string LabelFor(DetectorState state, bool isPaused) =>
		isPaused ? "Paused" : state.ToString();

	/// <summary>
	/// State colour as a <c>#RRGGBB</c> hex string. The Live Activity's SwiftUI
	/// presentation (design doc Phase 8) renders from this so the Lock Screen
	/// palette stays single-sourced here rather than duplicated in Swift.
	/// </summary>
	public static string HexFor(DetectorState state, bool isPaused = false)
	{
		var c = ColorFor(state, isPaused);
		return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
	}
}
