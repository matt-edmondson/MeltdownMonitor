namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Pure helpers for the Regulation Field's live RR texture — the beat-to-beat signal that
/// jitters the lemniscate trace. Shared by both heads' renderers (ported verbatim from the
/// desktop RegulationFieldView) and kept in Core so they are unit-testable.
/// </summary>
public static class RrTexture
{
	/// <summary>Below this many clean beats the trace draws smooth (flat) instead of textured.</summary>
	public const int MinRrForJitter = 8;

	/// <summary>Beat-to-beat difference (ms) that maps to full deflection.</summary>
	public const float RrDevScaleMs = 30f;

	/// <summary>
	/// Normalised beat-to-beat differences in [-1, 1]. An (almost) flat result when variability
	/// has collapsed; jagged when it is healthy. Empty when too few clean beats have arrived.
	/// </summary>
	public static float[] BuildRrDeviations(IReadOnlyList<double> rr)
	{
		if (rr.Count < MinRrForJitter)
		{
			return [];
		}

		var dev = new float[rr.Count];
		for (int i = 1; i < rr.Count; i++)
		{
			dev[i] = Math.Clamp((float)(rr[i] - rr[i - 1]) / RrDevScaleMs, -1f, 1f);
		}

		return dev;
	}
}
