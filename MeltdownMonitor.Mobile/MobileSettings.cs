using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Mobile;

/// <summary>
/// Settings surface used by the mobile <see cref="Pipeline"/>. Persistence is
/// deferred to the platform head; on iOS a thin layer over <c>NSUserDefaults</c>
/// is the likely backing store (design doc §13(2)). For Phase 2 the type is a
/// plain POCO so the pipeline can be exercised without an on-disk store.
/// </summary>
public sealed class MobileSettings
{
	public DetectionThresholds Thresholds { get; set; } = new();

	public HeartRateDeviceType DeviceType { get; set; } = HeartRateDeviceType.Auto;

	/// <summary>When set, monitoring is paused until this UTC time.</summary>
	public DateTimeOffset? PausedUntil { get; set; }

	public bool EnableChime { get; set; } = true;

	public bool EnableNotifications { get; set; } = true;

	public string AlertSuggestion { get; set; } =
		"Step away. Five minutes. Find something quiet.";

	/// <summary>
	/// DSN of the self-hosted GlitchTip project that receives crash reports. Null or blank
	/// disables crash reporting; the <c>MELTDOWN_CRASH_REPORTING_DSN</c> environment variable
	/// is used as a fallback. Opt-in by design, since the app handles sensitive physiological data.
	/// </summary>
	public string? CrashReportingDsn { get; set; }

	/// <summary>
	/// When true, dysregulation episodes are written back to HealthKit as
	/// "Mind &amp; Body" wellness annotations (design doc §6.3). Default off —
	/// Apple's wellness rules mean health write-back is strictly opt-in.
	/// </summary>
	public bool WriteEpisodesToHealthKit { get; set; }

	/// <summary>
	/// Master opt-in for the continuous health-store integration: when true the app
	/// both reads recent heart rate to warm the baseline and writes its live streams
	/// (heart rate, HRV, raw beat-to-beat RR) to Apple Health / Health Connect. Default
	/// off — all health-store I/O is strictly opt-in, and turning this off again revokes
	/// the app's reading and writing (the in-app half of "revoke access"). Distinct from
	/// <see cref="WriteEpisodesToHealthKit"/>, which gates only the per-alert episode
	/// annotation; the enable prompt flips both on together.
	/// </summary>
	public bool RecordToHealth { get; set; }

	/// <summary>
	/// True once the user has answered the one-shot "record to Apple Health / Health
	/// Connect" prompt (enabled or dismissed), so it isn't shown again. Persisted like
	/// the rest of the blob.
	/// </summary>
	public bool HealthPromptDismissed { get; set; }

	/// <summary>
	/// CoreBluetooth identifier of the last-connected sensor, persisted so the
	/// app can reconnect without a fresh scan on relaunch (design doc §4.1 /
	/// §6.4). Null until the first successful connection.
	/// </summary>
	public string? PeripheralIdentifier { get; set; }

	/// <summary>
	/// True once the user has acknowledged the first-run disclaimer. Gates the
	/// rest of the app (and any HealthKit ask) per design doc §4.4. Persisted
	/// by the platform head — on iOS that means <c>NSUserDefaults</c>.
	/// </summary>
	public bool IsDisclaimerAccepted { get; set; }

	/// <summary>
	/// When true, a Lock Screen / Dynamic Island Live Activity mirrors the
	/// current state, HR, and RMSSD-vs-baseline ratio (design doc §4.5 /
	/// Phase 8). Opt-in: a persistent on-device status surface is a deliberate
	/// choice, and the activity is the closest mobile analogue to the desktop
	/// tray icon.
	/// </summary>
	public bool EnableLiveActivity { get; set; }

	/// <summary>Number of recent readings drawn as the Regulation Field comet trail
	/// (12–2160; clamped at the consumer). Default 48 ≈ 4 min, max ≈ 3 h at the 5 s emit cadence.</summary>
	public int RegulationTrailLength { get; set; } = 48;

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

	/// <summary>Bucket resolution along the arousal-index (X) axis — the number of bars in the
	/// arousal histogram (6–64; clamped at the consumer). Higher = finer detail. Default 24.</summary>
	public int FieldIndexBuckets { get; set; } = 24;

	/// <summary>Bucket resolution along the vagal-tone (Y) axis — the number of bars in the
	/// vagal-tone histogram (6–64; clamped at the consumer). Higher = finer detail. Default 16.</summary>
	public int FieldVagalBuckets { get; set; } = 16;

	/// <summary>Loop rate of the Regulation Field recovery arrows — how fast the inward-pulling
	/// train slides toward the centre (0.1–3.0; clamped at the consumer). 0.7 is the tuned
	/// default; lower drifts in slowly, higher pulses in faster.</summary>
	public double RecoveryArrowSpeed { get; set; } = 0.7;

	/// <summary>Number of recovery arrows in the Regulation Field's inward-pulling train
	/// (1–6; clamped at the consumer). Default 3.</summary>
	public int RecoveryArrowCount { get; set; } = 3;

	/// <summary>Overall opacity of the Regulation Field's live-trace lobes (0–1; clamped at the
	/// consumer). The lobes are drawn with additive blending so densely overlapping segments bloom
	/// toward white; this knob pulls the stroke alpha down to compensate. Default 0.6.</summary>
	public double LobeOpacity { get; set; } = 0.60;

	/// <summary>Overall opacity of the Regulation Field's comet trail (0–1; clamped at the
	/// consumer). The trail is drawn with additive blending; this scales the per-segment alpha to
	/// compensate for the bloom. Default 0.7.</summary>
	public double TrailOpacity { get; set; } = 0.70;

	/// <summary>Overall opacity of the Regulation Field dwell heatmap (0–1; clamped at the
	/// consumer). 0 hides it; default 0.35 keeps it a faint underlay beneath the comet and marker.</summary>
	public double HeatmapOpacity { get; set; } = 0.35;

	/// <summary>Opacity of the crosshair marking the dwell heatmap's peak (busiest) bucket
	/// (0–1; clamped at the consumer). 0 hides it; default 0.7 keeps it a clear pointer over
	/// the faint underlay.</summary>
	public double HeatmapPeakOpacity { get; set; } = 0.70;

	/// <summary>Opacity of the dashed box outlining the dwell heatmap's high-concentration region
	/// (0–1; clamped at the consumer). 0 hides it; default 0.55 keeps it a soft frame around
	/// where regulation clusters.</summary>
	public double HeatmapRegionOpacity { get; set; } = 0.55;

	/// <summary>Fraction of the busiest bucket's count a bucket must reach to join the
	/// high-concentration region (0–1; clamped at the consumer). Default 0.5 wraps the buckets
	/// holding at least half the peak's dwell.</summary>
	public double HeatmapRegionThreshold { get; set; } = 0.50;

	/// <summary>Overall opacity of the Regulation Field's axis histograms (0–1; clamped at the
	/// consumer). The bars are drawn with additive blending; this scales their alpha to compensate
	/// for the bloom. Default 0.6.</summary>
	public double HistogramOpacity { get; set; } = 0.60;

	/// <summary>How many recent readings the Regulation Field dwell heatmap accumulates over
	/// (60–518400; clamped at the consumer). Default 720 ≈ 1 h, max ≈ 30 days at the 5 s emit cadence.</summary>
	public int RegulationHeatmapLength { get; set; } = 720;

	/// <summary>Minimum gap between HRV sample emissions (0.5–30 s). Lower = smoother graphs.
	/// Default 5.0 s.</summary>
	public double HrvEmitIntervalSeconds { get; set; } = 5.0;

	/// <summary>How much history the sparklines display (1–360 min). Default 60 min.</summary>
	public int SparklineWindowMinutes { get; set; } = 60;

	// ── Per-element blend modes ──────────────────────────────────────────────
	// Each Regulation Field glow layer can be drawn either additively (overlaps bloom toward
	// white — the signature glow) or with plain alpha compositing (no bloom). True = additive
	// (the tuned default look); false = alpha-over.

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
