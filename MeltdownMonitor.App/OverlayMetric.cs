using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.App;

/// <summary>A live metric that can be shown on the heads-up overlay.</summary>
public enum OverlayMetric
{
	State,
	HeartRate,
	Rmssd,
	RmssdVsBaseline,
	HrVsBaseline,
	Pnn50,
	Sdnn,
	LfHfRatio,
	LfPower,
	HfPower,
	Sd1,
	Sd2,
	Sd1Sd2Ratio,
	BaselineWarmUp,
}

/// <summary>Immutable snapshot of the pipeline state the overlay reads each frame.</summary>
public readonly record struct OverlaySample(DetectorState State, HrvSample? Latest, double WarmUpProgress);

/// <summary>
/// Labels and value formatting for each <see cref="OverlayMetric"/>. Pure (no ImGui or
/// pipeline dependencies) so the formatting can be unit-tested in isolation.
/// </summary>
public static class OverlayMetrics
{
	/// <summary>Placeholder shown when a metric has no value yet (e.g. before warm-up).</summary>
	public const string Unavailable = "—";

	/// <summary>Every metric, in canonical display order.</summary>
	public static IReadOnlyList<OverlayMetric> All { get; } = Enum.GetValues<OverlayMetric>();

	/// <summary>The short label shown beside a metric's value.</summary>
	public static string Label(OverlayMetric metric) => metric switch
	{
		OverlayMetric.State           => "State",
		OverlayMetric.HeartRate       => "HR",
		OverlayMetric.Rmssd           => "RMSSD",
		OverlayMetric.RmssdVsBaseline => "RMSSD Δ",
		OverlayMetric.HrVsBaseline    => "HR Δ",
		OverlayMetric.Pnn50           => "pNN50",
		OverlayMetric.Sdnn            => "SDNN",
		OverlayMetric.LfHfRatio       => "LF/HF",
		OverlayMetric.LfPower         => "LF power",
		OverlayMetric.HfPower         => "HF power",
		OverlayMetric.Sd1             => "SD1",
		OverlayMetric.Sd2             => "SD2",
		OverlayMetric.Sd1Sd2Ratio     => "SD1/SD2",
		OverlayMetric.BaselineWarmUp  => "Warm-up",
		_                             => metric.ToString(),
	};

	/// <summary>Formats the metric's current value, or <see cref="Unavailable"/> when there's no data.</summary>
	public static string Format(OverlayMetric metric, in OverlaySample sample)
	{
		switch (metric)
		{
			case OverlayMetric.State:
				return sample.State.ToString();
			case OverlayMetric.BaselineWarmUp:
				return $"{sample.WarmUpProgress * 100:F0}%";
		}

		if (sample.Latest is not { } latest)
		{
			return Unavailable;
		}

		switch (metric)
		{
			case OverlayMetric.HeartRate:
				return $"{latest.MeanHr:F0} bpm";
			case OverlayMetric.Rmssd:
				return $"{latest.Rmssd:F1} ms";
			case OverlayMetric.RmssdVsBaseline:
				return latest.BaselineRmssd > 0
					? $"{(latest.Rmssd - latest.BaselineRmssd) / latest.BaselineRmssd * 100:+0.0;-0.0;0.0}%"
					: Unavailable;
			case OverlayMetric.HrVsBaseline:
				return latest.BaselineHr > 0
					? $"{(latest.MeanHr - latest.BaselineHr) / latest.BaselineHr * 100:+0.0;-0.0;0.0}%"
					: Unavailable;
			case OverlayMetric.Pnn50:
				return $"{latest.Pnn50:F0}%";
		}

		// Frequency-domain and Poincaré metrics only exist once the extended window fills.
		if (latest.Extended is not { } ext)
		{
			return Unavailable;
		}

		return metric switch
		{
			OverlayMetric.Sdnn        => $"{ext.Sdnn:F1} ms",
			OverlayMetric.LfHfRatio   => $"{ext.LfHfRatio:F2}",
			OverlayMetric.LfPower     => $"{ext.LfPowerMs2:F0} ms²",
			OverlayMetric.HfPower     => $"{ext.HfPowerMs2:F0} ms²",
			OverlayMetric.Sd1         => $"{ext.SD1:F1} ms",
			OverlayMetric.Sd2         => $"{ext.SD2:F1} ms",
			OverlayMetric.Sd1Sd2Ratio => $"{ext.SD1SD2Ratio:F2}",
			_                         => Unavailable,
		};
	}
}
