namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// A single live reading of autonomic regulation, derived from an <c>HrvSample</c>
/// relative to the personal baseline.
/// </summary>
/// <param name="Index">
/// Signed arousal-vs-baseline in [-1, 1]. Positive = sympathetic activation (toward the
/// warm "meltdown" lobe); 0 = at baseline (centre of the window of tolerance); negative =
/// calmer than baseline = rest/recovery (the cool lobe). NOT a shutdown signal.
/// </param>
/// <param name="VariabilityQuality">
/// RMSSD relative to baseline in [0, 1]: 1 = healthy variability (a fat, lively trace),
/// 0 = collapsed/metronomic. Drives stroke fatness.
/// </param>
/// <param name="Confidence">
/// [0, 1]: 0 while the baseline is unusable/cold, ramping to 1 once warm. Dims the field.
/// </param>
/// <param name="LobeRoundness">
/// [0, 1]: cosmetic lemniscate lobe fatness derived from the Poincaré SD1/SD2 ratio
/// (un-baselined — see the calculator for the clamp band). 0.5 = neutral default when
/// extended metrics are absent.
/// </param>
/// <param name="LfHfBalance">
/// Signed LF/HF-vs-its-baseline in [-1, 1]: positive = sympathetic dominance, negative =
/// parasympathetic. 0 = neutral/unknown (no extended metrics or no LF/HF baseline yet).
/// </param>
public readonly record struct RegulationReading(
	double Index,
	double VariabilityQuality,
	double Confidence,
	double LobeRoundness,
	double LfHfBalance);
