using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.Core.Detection;

/// <summary>
/// Result of measuring whether alerts actually preceded felt escalation. All inputs are
/// supplied by the caller so the analysis is pure and unit-testable; it never reads the clock
/// or the database.
/// </summary>
public sealed record AlertEfficacyResult(
	int EscalationAnnotations,
	int PrecededByAlert,
	double Sensitivity,
	TimeSpan? MedianLeadTime,
	int AlertsWithNoFollowingEscalation);

/// <summary>
/// Measures detection efficacy from the data the app already persists: alert timestamps and
/// self check-in annotations. Answers the README's unvalidated claim that alerts fire "seconds
/// to minutes before the person consciously registers it" — and surfaces a false-alarm proxy.
/// </summary>
public static class DetectionEfficacyAnalyzer
{
	/// <summary>Self-report labels treated as "the user felt escalated".</summary>
	private static bool IsEscalation(AnnotationLabel label) =>
		label is AnnotationLabel.Escalating or AnnotationLabel.Blown;

	/// <param name="alertTimes">Alert timestamps (any order).</param>
	/// <param name="annotations">Self check-ins (any order).</param>
	/// <param name="leadWindow">How long before an escalation an alert may fire and still "count".</param>
	public static AlertEfficacyResult Analyze(
		IReadOnlyList<DateTimeOffset> alertTimes,
		IReadOnlyList<AnnotationRecord> annotations,
		TimeSpan leadWindow)
	{
		List<DateTimeOffset> alerts = [.. alertTimes.OrderBy(t => t)];
		List<AnnotationRecord> escalations = [.. annotations.Where(a => IsEscalation(a.Label))];

		var leads = new List<TimeSpan>();
		foreach (AnnotationRecord e in escalations)
		{
			// Nearest alert at or before the annotation, within the lead window.
			DateTimeOffset windowStart = e.Timestamp - leadWindow;
			TimeSpan? best = null;
			foreach (DateTimeOffset a in alerts)
			{
				if (a <= e.Timestamp && a >= windowStart)
				{
					TimeSpan lead = e.Timestamp - a;
					if (best is null || lead < best)
					{
						best = lead;
					}
				}
			}

			if (best is not null)
			{
				leads.Add(best.Value);
			}
		}

		int precededByAlert = leads.Count;
		double sensitivity = escalations.Count == 0 ? 0.0 : (double)precededByAlert / escalations.Count;

		int alertsWithNoFollowingEscalation = alerts.Count(a =>
			!escalations.Any(e => e.Timestamp >= a && e.Timestamp <= a + leadWindow));

		return new AlertEfficacyResult(
			escalations.Count,
			precededByAlert,
			sensitivity,
			Median(leads),
			alertsWithNoFollowingEscalation);
	}

	private static TimeSpan? Median(IReadOnlyList<TimeSpan> values)
	{
		if (values.Count == 0)
		{
			return null;
		}

		long[] ticks = [.. values.Select(v => v.Ticks).OrderBy(t => t)];
		int mid = ticks.Length / 2;
		long median = ticks.Length % 2 == 0
			? (ticks[mid - 1] + ticks[mid]) / 2
			: ticks[mid];
		return TimeSpan.FromTicks(median);
	}
}
