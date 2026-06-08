using System.Linq;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Motion;

namespace MeltdownMonitor.Core.Baseline;

public class BaselineHrvTracker
{
	private readonly object _lock = new();

	// Tuning — defaults match the original constants; the owner (Pipeline) overrides
	// these from user settings. The EWMA uses a fixed per-sample alpha (≈0.005 at a 5s
	// cadence ≈ 15-minute memory); the LF/HF alpha is slower as extended metrics arrive ~30s.
	/// <summary>Per-sample EWMA weight for the RMSSD/HR baseline.</summary>
	public double RmssdHrAlpha { get; set; } = 0.005;
	/// <summary>Per-sample EWMA weight for the LF/HF baseline.</summary>
	public double LfHfAlpha { get; set; } = 0.03;
	/// <summary>Cold-start warm-up duration (minutes) before the detector arms.</summary>
	public double WarmUpMinutes { get; set; } = 10.0;
	/// <summary>Recent window (minutes) whose median seeds the live baseline at startup.</summary>
	public double WarmStartWindowMinutes { get; set; } = 60.0;
	/// <summary>Minimum recent clean samples required to warm-start (skip the live warm-up).</summary>
	public int MinWarmStartSamples { get; set; } = 12;
	/// <summary>Guardrail: the live baseline may not drift more than this fraction from the anchor.</summary>
	public double MaxAnchorDrift { get; set; } = 0.40;
	/// <summary>When true, skip baseline updates at/above <see cref="MovementFreezeLevel"/>.</summary>
	public bool FreezeOnMovement { get; set; } = true;
	/// <summary>Movement level at/above which the baseline freezes (exercise HRV must not re-normalise it).</summary>
	public MovementLevel MovementFreezeLevel { get; set; } = MovementLevel.Moderate;
	/// <summary>When true, skip baseline updates while the Apple Watch contradicts the strap
	/// (<see cref="WatchCorroboration.Conflicted"/>) — a suspect strap reading must not re-normalise the
	/// baseline. Off by default; the owner (Pipeline) sets it from the detection thresholds.</summary>
	public bool FreezeOnWatchConflict { get; set; }

	private double _baselineRmssd;
	private double _baselineHr;
	private double _baselineLfHfRatio;
	private DateTimeOffset _firstSampleTime = DateTimeOffset.MinValue;
	private bool _isWarm;
	private double _anchorRmssd;
	private double _anchorHr;
	private double _anchorLfHfRatio;

	// Warm-up-window buffer for a no-history cold start: the live EWMA anchors on the first sample,
	// which is wrong if the user launches already dysregulated. We instead seed from the median of
	// the whole warm-up window once it completes (audit B).
	private readonly List<double> _warmUpRmssd = [];
	private readonly List<double> _warmUpHr = [];
	private bool _coldCalibrated;

	public double BaselineRmssd => _baselineRmssd;
	public double BaselineHr => _baselineHr;

	/// <summary>
	/// Baseline LF/HF ratio (EWMA). Zero until the first extended sample arrives.
	/// </summary>
	public double BaselineLfHfRatio => _baselineLfHfRatio;
	public bool IsWarm => _isWarm;

	/// <summary>
	/// True when the baseline was warm-started from this session's own warm-up window with no
	/// personal history anchor — i.e. self-calibrated cold. A provenance fact (it does not fade):
	/// if the whole warm-up was symptomatic, no self-calibration could correct it, so the UI should
	/// flag that readings may be measured against a possibly-activated baseline (audit B).
	/// </summary>
	public bool IsColdCalibrated => _coldCalibrated;

	/// <summary>0..1 progress toward the warm-up threshold, for UI display.</summary>
	public double WarmUpProgress
	{
		get
		{
			if (_isWarm)
			{
				return 1.0;
			}

			if (_firstSampleTime == DateTimeOffset.MinValue)
			{
				return 0.0;
			}

			double elapsed = (DateTimeOffset.UtcNow - _firstSampleTime).TotalMinutes;
			return Math.Clamp(elapsed / WarmUpMinutes, 0.0, 1.0);
		}
	}

	/// <param name="sample">The latest HRV sample.</param>
	/// <param name="contact">
	/// The sensor's skin / electrode contact state. When <see cref="SensorContactStatus.NotDetected"/>
	/// the update is skipped: RR data from an off-body sensor is unreliable and would otherwise drag
	/// the baseline toward garbage. The default (<see cref="SensorContactStatus.NotSupported"/>) and
	/// <see cref="SensorContactStatus.Detected"/> both proceed — sensors that don't report contact
	/// are never gated.
	/// </param>
	/// <param name="movement">
	/// Current movement level from a motion source. At/above <see cref="MovementFreezeLevel"/> the
	/// update is skipped: exercise legitimately lowers HRV and raises HR, and folding that into the
	/// baseline would desensitise the detector. The default (<see cref="MovementLevel.Unknown"/>)
	/// never freezes, so a build with no accelerometer is unaffected.
	/// </param>
	/// <param name="watch">
	/// Current Apple Watch corroboration verdict. When <see cref="WatchCorroboration.Conflicted"/> and
	/// <see cref="FreezeOnWatchConflict"/> is set, the update is skipped: the strap reading the wrist
	/// contradicts is suspect and must not re-normalise the baseline. The default
	/// (<see cref="WatchCorroboration.Unknown"/>) never freezes, so a no-watch build is unaffected.
	/// </param>
	public void Update(
		HrvSample sample,
		SensorContactStatus contact = SensorContactStatus.NotSupported,
		MovementLevel movement = MovementLevel.Unknown,
		WatchCorroboration watch = WatchCorroboration.Unknown)
	{
		// Do not update during dysregulated states — prevents baseline from
		// chasing a sustained episode and blinding the detector.
		if (sample.State is DetectorState.Warning or DetectorState.Alerting)
		{
			return;
		}

		// Do not update while the sensor is off-body — the same reasoning the
		// detector uses (RR data is untrustworthy), applied to the baseline so a
		// dropout can't quietly re-normalise it.
		if (contact == SensorContactStatus.NotDetected)
		{
			return;
		}

		// Do not update while moving — exercise HRV is real but unrepresentative of the resting
		// baseline, so folding it in would blind the detector to a later genuine episode.
		if (FreezeOnMovement && movement != MovementLevel.Unknown && movement >= MovementFreezeLevel)
		{
			return;
		}

		// Do not update while the wrist contradicts the strap (opt-in) — the suspect RR that drove the
		// conflict would otherwise quietly re-normalise the baseline, same reasoning as the movement freeze.
		if (FreezeOnWatchConflict && watch == WatchCorroboration.Conflicted)
		{
			return;
		}

		// Lock guards against a concurrent re-seed (SeedFromHistory) from another thread.
		lock (_lock)
		{
			if (_firstSampleTime == DateTimeOffset.MinValue)
			{
				_firstSampleTime = sample.Timestamp;
				// First-sample-anchor only the metrics still cold. A warm-start may have already
				// seeded HR (WarmStartHrBaseline) while leaving RMSSD to calibrate live — that HR
				// seed must survive the first live beat rather than be overwritten by it.
				if (_baselineRmssd <= 0)
				{
					_baselineRmssd = sample.Rmssd;
				}

				if (_baselineHr <= 0)
				{
					_baselineHr = sample.MeanHr;
				}
			}
			else
			{
				_baselineRmssd = ((1.0 - RmssdHrAlpha) * _baselineRmssd) + (RmssdHrAlpha * sample.Rmssd);
				_baselineHr = ((1.0 - RmssdHrAlpha) * _baselineHr) + (RmssdHrAlpha * sample.MeanHr);
			}

			// Update LF/HF baseline when extended metrics are present
			if (sample.Extended is { LfHfRatio: > 0 } extended)
			{
				if (_baselineLfHfRatio == 0)
				{
					_baselineLfHfRatio = extended.LfHfRatio;
				}
				else
				{
					_baselineLfHfRatio = ((1.0 - LfHfAlpha) * _baselineLfHfRatio)
										+ (LfHfAlpha * extended.LfHfRatio);
				}
			}

			ClampToAnchor();

			// Buffer the warm-up window so a no-history cold start can seed from its robust median
			// rather than the (possibly symptomatic) first sample.
			if (!_isWarm)
			{
				_warmUpRmssd.Add(sample.Rmssd);
				_warmUpHr.Add(sample.MeanHr);

				if ((sample.Timestamp - _firstSampleTime).TotalMinutes >= WarmUpMinutes)
				{
					SeedColdBaselineFromWarmUp();
					_isWarm = true;
				}
			}
		}
	}

	// At the end of a cold warm-up, replace the first-sample-anchored EWMA baseline with the median of
	// the whole warm-up window. The median is robust to a symptomatic first sample and to transient
	// spikes — the "calibrate during a symptom" guard (audit B). RMSSD and HR are decided independently
	// because a HealthKit warm-start (WarmStartHrBaseline) can anchor HR while leaving RMSSD to
	// calibrate cold here. A metric that already has a personal history anchor is left to the EWMA +
	// ClampToAnchor path, which guards its drift.
	private void SeedColdBaselineFromWarmUp()
	{
		if (_anchorRmssd <= 0)
		{
			double rmssdSeed = Median([.. _warmUpRmssd.Where(v => v > 0)]);
			if (rmssdSeed > 0)
			{
				_baselineRmssd = rmssdSeed;
			}

			// The parasympathetic baseline was self-calibrated from this session with no history
			// anchor — a provenance fact the UI surfaces so a possibly-activated baseline isn't shown
			// as confident calm (audit B). HR provenance (HealthKit vs live) does not change this.
			_coldCalibrated = true;
		}

		if (_anchorHr <= 0)
		{
			double hrSeed = Median([.. _warmUpHr.Where(v => v > 0)]);
			if (hrSeed > 0)
			{
				_baselineHr = hrSeed;
			}
		}

		_warmUpRmssd.Clear();
		_warmUpHr.Clear();
	}

	/// <summary>
	/// Seeds the HR baseline (and its drift anchor) from a heart-rate history pull, without
	/// fabricating an RMSSD baseline. HealthKit HR is averaged seconds-to-minutes apart: a
	/// legitimate resting-HR estimate, but it carries no beat-to-beat detail, so the parasympathetic
	/// RMSSD baseline is left to warm up from real live beats (audit B). The seed doubles as a drift
	/// anchor so the live HR warm-up can't run away from the known resting rate. Only fills HR state
	/// that is still cold, so a real beat-to-beat history seed (<see cref="SeedFromHistory"/>) always
	/// wins; a no-op once the tracker is already warm or when no positive HR is supplied.
	/// </summary>
	public void WarmStartHrBaseline(IReadOnlyList<double> heartRates)
	{
		lock (_lock)
		{
			if (_isWarm)
			{
				return;
			}

			double seed = Median([.. heartRates.Where(v => v > 0)]);
			if (seed <= 0)
			{
				return;
			}

			if (_baselineHr <= 0)
			{
				_baselineHr = seed;
			}

			if (_anchorHr <= 0)
			{
				_anchorHr = seed;
			}
		}
	}

	/// <summary>
	/// Seeds the baseline from persisted history: a robust (median) long-term anchor
	/// over the whole supplied window, and a warm-start of the live EWMA from the most
	/// recent hour. Clean samples only (no Warning/Alerting states, positive values).
	/// Safe to call once before live samples flow; a no-op when no usable history exists.
	/// </summary>
	public void SeedFromHistory(IReadOnlyList<HrvSample> history)
	{
		// Lock guards against concurrent live Update() calls when re-seeding mid-run.
		lock (_lock)
		{
			List<HrvSample> clean = [.. history.Where(IsClean)];

			_anchorRmssd = Median([.. clean.Where(s => s.Rmssd > 0).Select(s => s.Rmssd)]);
			_anchorHr = Median([.. clean.Where(s => s.MeanHr > 0).Select(s => s.MeanHr)]);
			_anchorLfHfRatio = Median([.. clean.Where(s => s.Extended is { LfHfRatio: > 0 })
				.Select(s => s.Extended!.LfHfRatio)]);

			DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-WarmStartWindowMinutes);
			List<HrvSample> recent = [.. clean.Where(s => s.Timestamp >= cutoff)];
			List<double> recentRmssd = [.. recent.Where(s => s.Rmssd > 0).Select(s => s.Rmssd)];
			List<double> recentHr = [.. recent.Where(s => s.MeanHr > 0).Select(s => s.MeanHr)];

			if (recentRmssd.Count < MinWarmStartSamples || recentHr.Count < MinWarmStartSamples)
			{
				// Not enough recent data to trust a warm start; anchor (if any) still guards
				// the live warm-up that follows.
				return;
			}

			_baselineRmssd = Median(recentRmssd);
			_baselineHr = Median(recentHr);

			List<double> recentLfHf = [.. recent.Where(s => s.Extended is { LfHfRatio: > 0 })
				.Select(s => s.Extended!.LfHfRatio)];
			if (recentLfHf.Count > 0)
			{
				_baselineLfHfRatio = Median(recentLfHf);
			}

			_firstSampleTime = recent.Max(s => s.Timestamp);
			_isWarm = true;
		}
	}

	private static bool IsClean(HrvSample sample) =>
		sample.State is not (DetectorState.Warning or DetectorState.Alerting);

	private static double Median(IReadOnlyList<double> values)
	{
		if (values.Count == 0)
		{
			return 0;
		}

		double[] sorted = [.. values.OrderBy(v => v)];
		int mid = sorted.Length / 2;
		return (sorted.Length % 2 == 0)
			? (sorted[mid - 1] + sorted[mid]) / 2.0
			: sorted[mid];
	}

	// Keep the live EWMA within +/-MaxAnchorDrift of the personalised anchor so a long
	// sub-threshold rough patch cannot silently re-normalise the baseline. No-op until
	// an anchor has been seeded.
	private void ClampToAnchor()
	{
		if (_anchorRmssd > 0)
		{
			_baselineRmssd = Math.Clamp(_baselineRmssd,
				_anchorRmssd * (1.0 - MaxAnchorDrift), _anchorRmssd * (1.0 + MaxAnchorDrift));
		}

		if (_anchorHr > 0)
		{
			_baselineHr = Math.Clamp(_baselineHr,
				_anchorHr * (1.0 - MaxAnchorDrift), _anchorHr * (1.0 + MaxAnchorDrift));
		}

		if (_anchorLfHfRatio > 0)
		{
			_baselineLfHfRatio = Math.Clamp(_baselineLfHfRatio,
				_anchorLfHfRatio * (1.0 - MaxAnchorDrift), _anchorLfHfRatio * (1.0 + MaxAnchorDrift));
		}
	}

	public void Reset()
	{
		// Lock so a reset from the UI thread (e.g. "clear my data") can't race the live Update on the
		// BLE thread mutating the warm-up buffers.
		lock (_lock)
		{
			_baselineRmssd = 0;
			_baselineHr = 0;
			_baselineLfHfRatio = 0;
			_firstSampleTime = DateTimeOffset.MinValue;
			_isWarm = false;
			_anchorRmssd = 0;
			_anchorHr = 0;
			_anchorLfHfRatio = 0;
			_warmUpRmssd.Clear();
			_warmUpHr.Clear();
			_coldCalibrated = false;
		}
	}
}
