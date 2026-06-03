using ktsu.AppDataStorage;
using MeltdownMonitor.Core.Baseline;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;

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
	/// Which Polar sensor to connect to. Auto connects to whichever HRS device is found first.
	/// </summary>
	public PolarDeviceType DeviceType { get; set; } = PolarDeviceType.Auto;

	public bool EnableChime { get; set; } = true;
	public string? ChimeWavPath { get; set; }

	public bool EnableToast { get; set; } = true;

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
	/// (60–17280; clamped at the consumer). Independent of — and usually much longer than — the
	/// comet trail: the comet shows where you're heading, the heatmap where you tend to dwell.
	/// Default 720 ≈ 1 h, max ≈ 24 h at the 5 s emit cadence.</summary>
	public int RegulationHeatmapLength { get; set; } = 720;

	/// <summary>Overall opacity of the Regulation Field dwell heatmap (0–1; clamped at the
	/// consumer). 0 hides it; default 0.35 keeps it a faint underlay beneath the comet and marker.</summary>
	public double HeatmapOpacity { get; set; } = 0.35;

	/// <summary>Multiplier on the Regulation Field's live-trace variability jitter
	/// (0–3; clamped at the consumer). 1.0 is the tuned default, 0 flattens the trace,
	/// higher exaggerates the beat-to-beat undulation.</summary>
	public double JitterExaggeration { get; set; } = 1.0;

	/// <summary>Multiplier on the Regulation Field's live-trace lobe stroke thickness
	/// (0.5–3; clamped at the consumer). 1.0 is the tuned default.</summary>
	public double LobeThickness { get; set; } = 1.0;

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
}
