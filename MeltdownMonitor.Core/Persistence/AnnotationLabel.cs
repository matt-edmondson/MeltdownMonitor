namespace MeltdownMonitor.Core.Persistence;

public enum AnnotationLabel
{
	Fine,
	Edged,
	Escalating,
	Blown,

	/// <summary>
	/// Low-arousal collapse/shutdown — the *lower* edge of the window of tolerance, distinct from
	/// the Fine→Blown hyperarousal escalation axis. Appended last so it persists cleanly: labels
	/// are stored as case-insensitive strings (see <c>MeltdownRepository.InsertAnnotation</c>), so
	/// ordinal position is irrelevant and existing databases are unaffected (audit A(c)).
	/// </summary>
	Shutdown,
}

/// <summary>
/// A self check-in the user recorded — one of the <see cref="AnnotationLabel"/> labels the
/// check-in UIs offer, plus an optional free-text note. Read back from the <c>annotations</c>
/// table for the mobile History timeline.
/// </summary>
public sealed record AnnotationRecord(DateTimeOffset Timestamp, AnnotationLabel Label, string? Notes);
