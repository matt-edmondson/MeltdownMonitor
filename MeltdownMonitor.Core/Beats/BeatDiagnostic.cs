namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// One raw beat-to-beat interval as decoded from a <i>specific</i> stream, before the pipeline narrows
/// to a single source. Unlike <see cref="Beat"/> (which carries only the chosen stream that drives HRV),
/// a diagnostic is raised for <b>every</b> interval from <b>every</b> stream the sensor produces —
/// including the standard Heart Rate Service RR that the head suppresses from the beat flow once a Polar
/// PMD interval source goes live. That lets a debug view compare, e.g., our ECG-derived RR against the
/// sensor's own HRS RR in real time, without ever feeding both into HRV (which would double-count).
/// </summary>
/// <param name="Timestamp">Arrival time of the interval (UTC).</param>
/// <param name="Source">Which stream produced it.</param>
/// <param name="RrMs">The interval in milliseconds.</param>
/// <param name="HeartRateBpm">Instantaneous/instrument heart rate reported alongside it.</param>
/// <param name="IsArtifact">Whether this stream's own filter flagged the interval as unreliable.</param>
public record BeatDiagnostic(
	DateTimeOffset Timestamp,
	IntervalSource Source,
	double RrMs,
	int HeartRateBpm,
	bool IsArtifact);

/// <summary>
/// Optional capability: a beat source that also reports every raw interval it decodes, tagged with its
/// originating stream, for the debug A/B view. Purely a side channel — implementing it never changes
/// which beats drive HRV (that stays the single chosen <see cref="IntervalSource"/>). The pipeline wires
/// it only when the debug surface needs it; a source that doesn't implement it simply offers no A/B.
/// </summary>
public interface IBeatDiagnosticsSource
{
	/// <summary>Raised per raw interval from any stream. Fires on a background BLE thread, in bursts —
	/// subscribers must marshal to their UI thread and tolerate high-rate delivery.</summary>
	event Action<BeatDiagnostic>? BeatDiagnosticReceived;
}
