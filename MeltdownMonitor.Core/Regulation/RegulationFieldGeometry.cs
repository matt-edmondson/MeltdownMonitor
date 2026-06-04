namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Pure pixel-space helpers shared by both heads' Regulation Field renderers, kept in
/// Core so they are unit-testable. The marker's vertical position encodes vagal tone:
/// FRAGILE (0) lifts to the top extent, STEADY (1) drops to the bottom, 0.5 rests on
/// the crossover. Ported verbatim from the desktop RegulationFieldView.VagalToneOffsetY.
/// </summary>
public static class RegulationFieldGeometry
{
	/// <summary>Vertical offset (pixels) from the crossover for a tone in [0, 1].
	/// <paramref name="markerYClamp"/> is the half-travel from the crossover to each extent;
	/// tone 0 → -clamp (top/FRAGILE), tone 1 → +clamp (bottom/STEADY), 0.5 → 0 (crossover).
	/// Out-of-range tone is clamped to the extents.</summary>
	public static float VagalToneOffsetY(double vagalTone, float markerYClamp)
		=> Math.Clamp(((float)vagalTone - 0.5f) * 2f * markerYClamp, -markerYClamp, markerYClamp);
}
