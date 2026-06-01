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
	/// (12–240; clamped at the consumer). Default 48 ≈ 4 min at the 5 s emit cadence.</summary>
	public int RegulationTrailLength { get; set; } = 48;
}
