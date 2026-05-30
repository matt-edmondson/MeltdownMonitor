using MeltdownMonitor.App.Regulation;
using MeltdownMonitor.Core.Detection;
using System.Numerics;

namespace MeltdownMonitor.App;

/// <summary>
/// Shared detector-state colour scheme, used by the status header and the metrics
/// overlay. Delegates to <see cref="MacchiatoPalette"/> so every surface (header,
/// overlay, Regulation Field) draws from the one Catppuccin Macchiato source.
/// </summary>
internal static class StateColors
{
	/// <summary>The RGBA colour representing a detector state.</summary>
	public static Vector4 For(DetectorState state) => MacchiatoPalette.State(state);
}
