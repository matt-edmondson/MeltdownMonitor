using ktsu.AppDataStorage;
using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.App;

public class AppSettings : AppData<AppSettings>
{
	public DetectionThresholds Thresholds { get; set; } = new();

	/// <summary>Pre-written calm suggestion shown in the alert toast.</summary>
	public string AlertSuggestion { get; set; } =
		"Step away. Five minutes. Find something quiet.";

	public string? DeviceNameFilter { get; set; }

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
}
