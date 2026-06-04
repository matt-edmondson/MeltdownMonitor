namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// A single live reading of autonomic regulation, derived from an <c>HrvSample</c>
/// relative to the personal baseline.
/// </summary>
/// <param name="Index">
/// Signed arousal-vs-baseline. Positive = sympathetic activation (toward the warm "meltdown"
/// lobe); 0 = at baseline (centre of the window of tolerance); negative = calmer than baseline
/// = rest/recovery (the cool lobe). NOT a shutdown signal. Magnitude is unbounded — severe
/// states can exceed ±1. The display clips to ±1 (marker position, heatmap axis); the raw
/// value is kept so genuinely extreme readings do not pile up in the edge histogram buckets.
/// </param>
/// <param name="VariabilityQuality">
/// RMSSD relative to baseline in [0, 1]: 1 = healthy variability (a fat, lively trace),
/// 0 = collapsed/metronomic. Drives stroke fatness. Saturates at 1 once RMSSD reaches
/// baseline — for the marker's *vertical position* use <see cref="VagalTone"/> instead,
/// which keeps resolution above baseline.
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
	double LfHfBalance)
{
	/// <summary>
	/// [0, 1] low-arousal collapse signal: rises when HR is well below baseline AND variability
	/// is not elevated (distinct from genuine high-vagal rest). 0 when activated, at rest with
	/// healthy variability, or when the baseline is unusable. Display-only — does NOT drive the
	/// detector (see audit A(b)). Provisional heuristic pending validation against real episodes.
	/// </summary>
	public double Hypoarousal { get; init; }

	/// <summary>
	/// Vagal-tone vertical position in [0, 1], baseline-centred so the field's Y axis is
	/// baseline-relative like its X (<see cref="Index"/>): 0.5 = at baseline (the crossover),
	/// → 1 (STEADY) as RMSSD rises above baseline, → 0 (FRAGILE) as it collapses. Derived from
	/// the log-ratio of RMSSD to baseline, so equal proportional moves above and below baseline
	/// travel equal distance and there is real resolution on *both* sides — unlike
	/// <see cref="VariabilityQuality"/>, which pins everything at or above baseline to 1. 0.5
	/// (neutral) when the baseline is unusable. Display-only.
	/// </summary>
	public double VagalTone { get; init; }
}
