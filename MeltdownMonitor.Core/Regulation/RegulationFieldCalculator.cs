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

	// A combined deviation equal to 1.0 (both metrics at their Warning thresholds)
	// maps to this index magnitude, leaving head-room toward the saturating ±1.
	private const double WarningIndex = 0.6;

	public static RegulationReading Compute(
		HrvSample sample,
		DetectionThresholds thresholds,
		double warmUpProgress,
		bool baselineWarm)
	{
		double confidence = baselineWarm ? 1.0 : Math.Clamp(warmUpProgress, 0.0, 1.0);

		if (sample.BaselineRmssd <= 0 || sample.BaselineHr <= 0)
		{
			// Baseline not usable yet — neutral position, no confidence.
			return new RegulationReading(0.0, 1.0, 0.0);
		}

		double rmssdDrop = (sample.BaselineRmssd - sample.Rmssd) / sample.BaselineRmssd; // + when stressed
		double hrRise = (sample.MeanHr - sample.BaselineHr) / sample.BaselineHr;         // + when stressed

		// Express each deviation in units of its Warning threshold so the two
		// differently-scaled fractions can be combined on a common axis.
		double warnR = Math.Max(thresholds.RmssdWarningDropFraction, 1e-6);
		double warnH = Math.Max(thresholds.HrWarningRiseFraction, 1e-6);

		double activation = (RmssdWeight * Math.Max(0.0, rmssdDrop) / warnR)
						  + (HrWeight * Math.Max(0.0, hrRise) / warnH);
		double rest = (RmssdWeight * Math.Max(0.0, -rmssdDrop) / warnR)
					+ (HrWeight * Math.Max(0.0, -hrRise) / warnH);

		double index = Math.Clamp((activation - rest) * WarningIndex, -1.0, 1.0);

		double quality = Math.Clamp(sample.Rmssd / sample.BaselineRmssd, 0.0, 1.0);

		return new RegulationReading(index, quality, confidence);
	}
}
