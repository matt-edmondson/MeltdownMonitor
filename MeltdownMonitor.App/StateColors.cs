using MeltdownMonitor.Core.Detection;
using System.Numerics;

namespace MeltdownMonitor.App;

/// <summary>Shared colour scheme for detector states, used by the status header and overlay.</summary>
internal static class StateColors
{
	/// <summary>The RGBA colour representing a detector state.</summary>
	public static Vector4 For(DetectorState state) => state switch
	{
		DetectorState.Idle     => new Vector4(0.55f, 0.55f, 0.55f, 1f),
		DetectorState.Watching => new Vector4(0.30f, 0.75f, 0.45f, 1f),
		DetectorState.Warning  => new Vector4(0.95f, 0.75f, 0.20f, 1f),
		DetectorState.Alerting => new Vector4(0.95f, 0.30f, 0.25f, 1f),
		DetectorState.Cooldown => new Vector4(0.45f, 0.55f, 0.85f, 1f),
		_                      => new Vector4(0.5f, 0.5f, 0.5f, 1f),
	};
}
