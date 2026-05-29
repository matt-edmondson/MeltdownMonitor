namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// A single live reading of autonomic regulation, derived from an <c>HrvSample</c>
/// relative to the personal baseline.
/// </summary>
/// <param name="Index">
/// Signed arousal-vs-baseline in [-1, 1].
/// Positive = sympathetic activation (toward the warm "meltdown" lobe);
/// 0 = at baseline (centre of the window of tolerance);
/// negative = calmer than baseline = rest/recovery (the cool lobe).
/// This is NOT a shutdown signal — true shutdown detection is not yet possible
/// from RMSSD/HR alone.
/// </param>
/// <param name="VariabilityQuality">
/// RMSSD relative to baseline in [0, 1]: 1 = healthy variability (a fat, lively
/// trace), 0 = collapsed/metronomic (the stress signature). Drives stroke fatness.
/// </param>
/// <param name="Confidence">
/// [0, 1]: 0 while the baseline is unusable/cold, ramping to 1 once warm.
/// The view dims the whole field by this value.
/// </param>
public readonly record struct RegulationReading(double Index, double VariabilityQuality, double Confidence);
