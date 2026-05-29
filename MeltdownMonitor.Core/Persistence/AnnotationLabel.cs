namespace MeltdownMonitor.Core.Persistence;

public enum AnnotationLabel
{
	Fine,
	Edged,
	Escalating,
	Blown,
}

/// <summary>
/// A self check-in the user recorded — the same four labels the desktop's
/// annotation dialog offers, plus an optional free-text note. Read back from
/// the <c>annotations</c> table for the mobile History timeline.
/// </summary>
public sealed record AnnotationRecord(DateTimeOffset Timestamp, AnnotationLabel Label, string? Notes);
