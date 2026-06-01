namespace MeltdownMonitor.Core.Detection;

/// <summary>
/// State of the <see cref="HypoarousalDetector"/> — the low edge of the window of tolerance.
/// Single-tier (no Warning/Alerting split): a sustained low-arousal signal is one episode.
/// </summary>
public enum HypoarousalState
{
	/// <summary>Baseline not yet warm — the detector is dormant.</summary>
	Idle,

	/// <summary>Armed and watching; no sustained low-arousal signal.</summary>
	Monitoring,

	/// <summary>A sustained low-arousal collapse episode is in progress.</summary>
	LowArousal,
}
