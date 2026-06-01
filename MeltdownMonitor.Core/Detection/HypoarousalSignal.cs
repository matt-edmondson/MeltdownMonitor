namespace MeltdownMonitor.Core.Detection;

/// <summary>
/// The low-arousal collapse signal in [0, 1], shared by the display
/// (<c>RegulationReading.Hypoarousal</c>) and the <see cref="HypoarousalDetector"/> so the two
/// never drift. Rises when HR is meaningfully below baseline AND variability is not elevated —
/// the only HRV-distinguishable hypoarousal pattern: genuine relaxed rest is HR-down + HRV-<i>up</i>,
/// whereas collapse is HR-down + HRV-flat/down. Pure and deterministic.
///
/// Clinical humility (audit A(b)): this cannot detect dorsal-vagal shutdown that <i>preserves</i>
/// HRV — that is indistinguishable from rest by HRV alone. The signature is provisional; validate
/// against felt episodes with the <see cref="DetectionEfficacyAnalyzer"/> before fully trusting it.
/// </summary>
public static class HypoarousalSignal
{
	// HR must fall beyond this fraction below baseline before any signal accrues…
	private const double HrFallBand = 0.10;

	// …and the signal saturates this much further down (i.e. full strength at 0.25 below baseline).
	private const double HrFallSpan = 0.15;

	/// <summary>
	/// The [0, 1] collapse signal from raw metrics relative to baseline. Returns 0 when the baseline
	/// is unusable or any input is non-finite, when HR is at/above baseline (activation or neutral),
	/// or when RMSSD is at/above baseline (genuine vagal rest — high variability, not collapse).
	/// </summary>
	/// <remarks>
	/// The result is the product of two clamped terms: the HR-fall ramp
	/// <c>clamp((hrFall − 0.10) / 0.15, 0, 1)</c> and the low-variability gate <c>(1 − quality)</c>,
	/// where <c>quality = clamp(rmssd / baselineRmssd, 0, 1)</c>. Both must be substantial for the
	/// signal to be large — a deep HR drop alone, or collapsed RMSSD alone, is not enough.
	/// </remarks>
	public static double Compute(double rmssd, double meanHr, double baselineRmssd, double baselineHr)
	{
		if (!double.IsFinite(baselineRmssd) || baselineRmssd <= 0
			|| !double.IsFinite(baselineHr) || baselineHr <= 0
			|| !double.IsFinite(rmssd) || !double.IsFinite(meanHr))
		{
			return 0.0;
		}

		double hrFall = (baselineHr - meanHr) / baselineHr;           // + when HR is below baseline
		double quality = Math.Clamp(rmssd / baselineRmssd, 0.0, 1.0); // 1 = healthy/elevated variability
		return Math.Clamp((hrFall - HrFallBand) / HrFallSpan, 0.0, 1.0) * (1.0 - quality);
	}
}
