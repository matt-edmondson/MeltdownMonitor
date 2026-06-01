using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Turns an <see cref="HrvSample"/> into a <see cref="RegulationReading"/> relative
/// to the personal baseline. Pure and deterministic so it can be unit-tested and
/// shared with the iOS port.
/// </summary>
public static class RegulationFieldCalculator
{
	// RMSSD is the gold-standard parasympathetic marker, so it carries more weight
	// than HR in the combined arousal index.
	private const double RmssdWeight = 0.6;
	private const double HrWeight = 0.4;

	// Hypoarousal display signal: only HR falling beyond HypoHrBand below baseline counts, and
	// it saturates HypoHrSpan further down. Suppressed by healthy variability (see Compute).
	private const double HypoHrBand = 0.10;
	private const double HypoHrSpan = 0.15;

	/// <summary>
	/// The index magnitude a combined deviation of 1.0 maps to — i.e. both RMSSD-drop and
	/// HR-rise exactly at their Warning thresholds. This is therefore the boundary the
	/// marker must fall back below to clear the Warning condition; the Regulation Field uses
	/// it to draw the recovery target. Constant regardless of the configured thresholds,
	/// because the index normalises by them. Leaves head-room toward the saturating ±1.
	/// </summary>
	public const double WarningBoundaryIndex = 0.6;

	public static RegulationReading Compute(
		HrvSample sample,
		DetectionThresholds thresholds,
		double warmUpProgress,
		bool baselineWarm)
	{
		double confidence = baselineWarm ? 1.0 : Math.Clamp(warmUpProgress, 0.0, 1.0);

		if (!double.IsFinite(sample.BaselineRmssd) || sample.BaselineRmssd <= 0
			|| !double.IsFinite(sample.BaselineHr) || sample.BaselineHr <= 0
			|| !double.IsFinite(sample.Rmssd) || !double.IsFinite(sample.MeanHr))
		{
			// Baseline not usable yet — neutral position, no confidence.
			return new RegulationReading(0.0, 1.0, 0.0, 0.5, 0.0);
		}

		double rmssdDrop = (sample.BaselineRmssd - sample.Rmssd) / sample.BaselineRmssd; // + when stressed
		double hrRise = (sample.MeanHr - sample.BaselineHr) / sample.BaselineHr;         // + when stressed

		// Express each deviation in units of its Warning threshold so the two
		// differently-scaled fractions can be combined on a common axis.
		double warnR = Math.Max(thresholds.RmssdWarningDropFraction, 1e-6);
		double warnH = Math.Max(thresholds.HrWarningRiseFraction, 1e-6);

		// Positive combined = activation toward the warm lobe; negative = calmer than baseline.
		double combined = (RmssdWeight * rmssdDrop / warnR)
						+ (HrWeight * hrRise / warnH);
		double index = Math.Clamp(combined * WarningBoundaryIndex, -1.0, 1.0);

		double quality = Math.Clamp(sample.Rmssd / sample.BaselineRmssd, 0.0, 1.0);

		// Poincaré SD1/SD2 ratio → cosmetic lobe fatness. Un-baselined: healthy ratios sit
		// roughly in [0.2, 0.6]; map that band to [0, 1]. Neutral 0.5 when extended metrics
		// are absent. (SD2, the long-term axis, makes this independent of the RMSSD collapse
		// that VariabilityQuality already shows.)
		double lobeRoundness = 0.5;
		if (sample.Extended is { SD1SD2Ratio: > 0 } poincare)
		{
			lobeRoundness = Math.Clamp((poincare.SD1SD2Ratio - 0.2) / 0.4, 0.0, 1.0);
		}

		// Signed LF/HF relative to its own baseline. 0 when no extended LF/HF or no baseline.
		double lfHfBalance = 0.0;
		if (sample.BaselineLfHfRatio > 0 && sample.Extended is { LfHfRatio: > 0 } freq)
		{
			double rise = (freq.LfHfRatio - sample.BaselineLfHfRatio) / sample.BaselineLfHfRatio;
			lfHfBalance = Math.Clamp(rise, -1.0, 1.0);
		}

		// Hypoarousal: HR well below baseline, gated by *low* variability so genuine vagal rest
		// (high RMSSD) does not read as collapse. `quality` is RMSSD/baseline clamped to [0,1].
		double hrFall = (sample.BaselineHr - sample.MeanHr) / sample.BaselineHr;
		double hypoarousal = Math.Clamp((hrFall - HypoHrBand) / HypoHrSpan, 0.0, 1.0) * (1.0 - quality);

		return new RegulationReading(index, quality, confidence, lobeRoundness, lfHfBalance)
		{
			Hypoarousal = hypoarousal,
		};
	}
}
