using ktsu.AppDataStorage;
using MeltdownMonitor.Core.Baseline;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.App;

public class AppSettings : AppData<AppSettings>
{
	public DetectionThresholds Thresholds { get; set; } = new();

	/// <summary>HRV baseline seeding, responsiveness, and guardrail tuning.</summary>
	public BaselineTuning BaselineTuning { get; set; } = new();

	/// <summary>Status-window chart layout tuning.</summary>
	public ChartTuning ChartTuning { get; set; } = new();

	/// <summary>Advanced HRV computation window tuning.</summary>
	public HrvTuning HrvTuning { get; set; } = new();

	/// <summary>Transparent heads-up metrics overlay configuration.</summary>
	public OverlaySettings Overlay { get; set; } = new();

	/// <summary>Pre-written calm suggestion shown in the alert toast.</summary>
	public string AlertSuggestion { get; set; } =
		"Step away. Five minutes. Find something quiet.";

	/// <summary>
	/// Which heart-rate sensor to connect to. Auto connects to whichever HRS device is found first.
	/// </summary>
	public HeartRateDeviceType DeviceType { get; set; } = HeartRateDeviceType.Auto;

	/// <summary>
	/// When true, stream the Polar strap accelerometer (PMD) and use the resulting movement level to
	/// defer alerts and freeze the baseline during exertion — exercise mimics the dysregulation
	/// signature, so this suppresses that false positive. No effect on non-Polar sensors. Default off.
	/// </summary>
	public bool EnableMotionCorroboration { get; set; }

	/// <summary>
	/// Which stream supplies beat-to-beat intervals. Default <see cref="IntervalSource.HeartRateService"/>
	/// (standard HRS RR, every device). The Polar PMD options (<see cref="IntervalSource.PolarPpi"/> for
	/// Verity Sense, <see cref="IntervalSource.PolarEcg"/> for the H10) take over once their stream is
	/// live and fall back to HRS on a device that doesn't offer them. Applies on next start.
	/// </summary>
	public IntervalSource PreferredIntervalSource { get; set; } = IntervalSource.HeartRateService;

	/// <summary>
	/// When true, the status window shows a Debug tab with live diagnostics — the ECG-vs-HRS RR A/B,
	/// per-stream artifact rates, the full HRV/baseline/movement/ECG dump, and connection details.
	/// Off by default; purely diagnostic, it changes no detection behaviour.
	/// </summary>
	public bool EnableDebugMode { get; set; }

	public bool EnableChime { get; set; } = true;
	public string? ChimeWavPath { get; set; }

	public bool EnableToast { get; set; } = true;

	/// <summary>
	/// DSN of the self-hosted GlitchTip project that receives crash reports. Null or blank
	/// disables crash reporting; the <c>MELTDOWN_CRASH_REPORTING_DSN</c> environment variable
	/// is used as a fallback. Opt-in by design, since the app handles sensitive physiological data.
	/// </summary>
	public string? CrashReportingDsn { get; set; }

	/// <summary>When set, monitoring is paused until this time.</summary>
	public DateTimeOffset? PausedUntil { get; set; }

	public string DatabasePath { get; set; } =
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"MeltdownMonitor",
			"meltdown.db");

	/// <summary>Minimum gap between HRV sample emissions (0.5–30s). Lower = smoother graphs.</summary>
	public double HrvEmitIntervalSeconds { get; set; } = 5.0;

	/// <summary>How much history the status-window sparklines display (1–360 min).</summary>
	public int SparklineWindowMinutes { get; set; } = 60;

	/// <summary>Number of recent readings drawn as the Regulation Field comet trail
	/// (12–2160; clamped at the consumer). Default 48 ≈ 4 min, max ≈ 3 h at the 5 s emit cadence.</summary>
	public int RegulationTrailLength { get; set; } = 48;

	/// <summary>How many recent readings the Regulation Field dwell heatmap accumulates over
	/// (60–518400; clamped at the consumer). Independent of — and usually much longer than — the
	/// comet trail: the comet shows where you're heading, the heatmap where you tend to dwell.
	/// Default 720 ≈ 1 h, max ≈ 30 days at the 5 s emit cadence.</summary>
	public int RegulationHeatmapLength { get; set; } = 720;

	/// <summary>Overall opacity of the Regulation Field dwell heatmap (0–1; clamped at the
	/// consumer). 0 hides it; default 0.35 keeps it a faint underlay beneath the comet and marker.</summary>
	public double HeatmapOpacity { get; set; } = 0.35;

	/// <summary>Opacity of the crosshair marking the dwell heatmap's peak (busiest) bucket — where
	/// regulation has settled most over the heatmap window (0–1; clamped at the consumer). 0 hides
	/// it; default 0.7 keeps it a clear pointer over the faint underlay.</summary>
	public double HeatmapPeakOpacity { get; set; } = 0.7;

	/// <summary>Opacity of the dashed box outlining the dwell heatmap's high-concentration region —
	/// the block of busy buckets around the peak crosshair (0–1; clamped at the consumer). 0 hides
	/// it; default 0.55 keeps it a soft frame around where regulation clusters.</summary>
	public double HeatmapRegionOpacity { get; set; } = 0.55;

	/// <summary>Fraction of the busiest bucket's count a bucket must reach to join the dashed
	/// high-concentration region (0–1; clamped at the consumer). Lower widens the box toward every
	/// occupied bucket; higher tightens it around the peak. Default 0.5 wraps the buckets holding at
	/// least half the peak's dwell.</summary>
	public double HeatmapRegionThreshold { get; set; } = 0.5;

	/// <summary>Multiplier on the Regulation Field's live-trace variability jitter
	/// (0–3; clamped at the consumer). 1.0 is the tuned default, 0 flattens the trace,
	/// higher exaggerates the beat-to-beat undulation.</summary>
	public double JitterExaggeration { get; set; } = 1.0;

	/// <summary>Multiplier on the Regulation Field's live-trace lobe stroke thickness
	/// (0.5–3; clamped at the consumer). 1.0 is the tuned default.</summary>
	public double LobeThickness { get; set; } = 1.0;

	/// <summary>Number of points sampled along the Regulation Field's figure-8 outline — its
	/// render resolution (24–256; clamped at the consumer). Default 96 preserves the original
	/// fixed look; lower = faceted, higher = smoother.</summary>
	public int LobeSegments { get; set; } = LemniscateGeometry.DefaultSegments;

	/// <summary>Overall opacity of the Regulation Field's live-trace lobes (0–1; clamped at the
	/// consumer). The lobes are drawn with additive blending, so their densely overlapping
	/// segments and round joins bloom toward white; this knob pulls the stroke alpha down to
	/// compensate for that saturation. Default 0.6.</summary>
	public double LobeOpacity { get; set; } = 0.6;

	/// <summary>Overall opacity of the Regulation Field's comet trail (0–1; clamped at the
	/// consumer). The trail is drawn with additive blending, so overlapping sub-segments and the
	/// head bloom; this scales the per-segment alpha to compensate. Default 0.7.</summary>
	public double TrailOpacity { get; set; } = 0.7;

	/// <summary>Overall opacity of the Regulation Field's axis histograms (0–1; clamped at the
	/// consumer). The bars are drawn with additive blending; this scales their alpha to compensate
	/// for the bloom. Default 0.6.</summary>
	public double HistogramOpacity { get; set; } = 0.6;

	/// <summary>Bucket resolution along the arousal-index (X) axis — the number of bars in the
	/// arousal histogram and columns in the dwell heatmap (6–64; clamped at the consumer).
	/// Higher = finer detail, lower = chunkier. Default 24.</summary>
	public int FieldIndexBuckets { get; set; } = 24;

	/// <summary>Bucket resolution along the vagal-tone (Y) axis — the number of bars in the
	/// vagal-tone histogram and rows in the dwell heatmap (6–64; clamped at the consumer).
	/// Higher = finer detail, lower = chunkier. Default 16.</summary>
	public int FieldVagalBuckets { get; set; } = 16;

	/// <summary>Loop rate of the Regulation Field recovery arrows — how fast the inward-pulling
	/// train slides toward the centre (0.1–3.0; clamped at the consumer). 0.7 is the tuned
	/// default; lower drifts in slowly, higher pulses in faster.</summary>
	public double RecoveryArrowSpeed { get; set; } = 0.7;

	/// <summary>Number of recovery arrows in the Regulation Field's inward-pulling train
	/// (1–6; clamped at the consumer). Default 3.</summary>
	public int RecoveryArrowCount { get; set; } = 3;

	// ── Per-element blend modes ──────────────────────────────────────────────
	// Each Regulation Field glow layer can be drawn either additively (overlaps bloom toward
	// white — the signature glow) or with plain alpha compositing (overlaps composite over,
	// no bloom). True = additive (the tuned default look); false = alpha-over.

	/// <summary>Blend the LF/HF balance halo additively (true, glow) or with alpha (false). Default additive.</summary>
	public bool LfHfHaloAdditive { get; set; } = true;

	/// <summary>Blend the live-trace lobes additively (true, glow) or with alpha (false). Default additive.</summary>
	public bool LobesAdditive { get; set; } = true;

	/// <summary>Blend the comet trail additively (true, glow) or with alpha (false). Default additive.</summary>
	public bool TrailAdditive { get; set; } = true;

	/// <summary>Blend the dwell-heatmap cells additively (true, glow) or with alpha (false). Default additive.</summary>
	public bool HeatmapAdditive { get; set; } = true;

	/// <summary>Blend the marker halos additively (true, glow) or with alpha (false). Default additive.</summary>
	public bool MarkerHaloAdditive { get; set; } = true;

	/// <summary>Blend the axis histogram bars additively (true, glow) or with alpha (false). Default additive.</summary>
	public bool HistogramAdditive { get; set; } = true;
}
