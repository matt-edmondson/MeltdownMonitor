namespace MeltdownMonitor.Core.Detection;

/// <summary>How an LF/HF baseline participates in the Warning decision.</summary>
public enum LfHfCorroborationMode
{
	/// <summary>
	/// LF/HF can veto a core-satisfied Warning. More specific, but the 5-minute LF/HF window lags
	/// a fast onset and can suppress the early Warning — superseded as the default by the
	/// 2026-06-01 clinical audit in favour of <see cref="Additive"/>.
	/// </summary>
	Veto,

	/// <summary>
	/// LF/HF never vetoes; the core RMSSD+HR condition alone enters Warning. Avoids suppressing
	/// the early-warning value proposition. The default since the 2026-06-01 clinical audit.
	/// </summary>
	Additive,
}
