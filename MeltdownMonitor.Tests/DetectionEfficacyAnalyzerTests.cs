using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.Tests;

[TestClass]
public class DetectionEfficacyAnalyzerTests
{
	private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
	private static AnnotationRecord Ann(double minutes, AnnotationLabel label) =>
		new(T0.AddMinutes(minutes), label, null);

	[TestMethod]
	public void AlertBeforeEscalation_CountsAsPrecededWithLeadTime()
	{
		var alerts = new[] { T0.AddMinutes(3) };
		var annotations = new[] { Ann(6, AnnotationLabel.Escalating) };

		var r = DetectionEfficacyAnalyzer.Analyze(alerts, annotations, TimeSpan.FromMinutes(10));

		Assert.AreEqual(1, r.EscalationAnnotations);
		Assert.AreEqual(1, r.PrecededByAlert);
		Assert.AreEqual(1.0, r.Sensitivity, 0.001);
		Assert.AreEqual(TimeSpan.FromMinutes(3), r.MedianLeadTime);
	}

	[TestMethod]
	public void AlertOutsideLeadWindow_DoesNotCount()
	{
		var alerts = new[] { T0 };
		var annotations = new[] { Ann(30, AnnotationLabel.Blown) };

		var r = DetectionEfficacyAnalyzer.Analyze(alerts, annotations, TimeSpan.FromMinutes(10));

		Assert.AreEqual(1, r.EscalationAnnotations);
		Assert.AreEqual(0, r.PrecededByAlert);
		Assert.AreEqual(0.0, r.Sensitivity, 0.001);
		Assert.IsNull(r.MedianLeadTime);
	}

	[TestMethod]
	public void AlertAfterAnnotation_DoesNotCountAsLead()
	{
		var alerts = new[] { T0.AddMinutes(8) };
		var annotations = new[] { Ann(5, AnnotationLabel.Escalating) };

		var r = DetectionEfficacyAnalyzer.Analyze(alerts, annotations, TimeSpan.FromMinutes(10));

		Assert.AreEqual(0, r.PrecededByAlert);
	}

	[TestMethod]
	public void FineAndEdged_AreNotEscalations()
	{
		var annotations = new[] { Ann(5, AnnotationLabel.Fine), Ann(6, AnnotationLabel.Edged) };

		var r = DetectionEfficacyAnalyzer.Analyze([], annotations, TimeSpan.FromMinutes(10));

		Assert.AreEqual(0, r.EscalationAnnotations);
		Assert.AreEqual(0.0, r.Sensitivity, 0.001);
	}

	[TestMethod]
	public void AlertWithNoFollowingEscalation_IsCountedAsFalseAlarmProxy()
	{
		var alerts = new[] { T0, T0.AddMinutes(40) };
		var annotations = new[] { Ann(5, AnnotationLabel.Escalating) }; // follows the first alert only

		var r = DetectionEfficacyAnalyzer.Analyze(alerts, annotations, TimeSpan.FromMinutes(10));

		Assert.AreEqual(1, r.AlertsWithNoFollowingEscalation);
	}

	[TestMethod]
	public void MedianLeadTime_IsTrueMedianAcrossMultiple()
	{
		var alerts = new[] { T0.AddMinutes(1), T0.AddMinutes(11), T0.AddMinutes(21) };
		var annotations = new[]
		{
			Ann(3, AnnotationLabel.Escalating),  // lead 2m
			Ann(15, AnnotationLabel.Blown),      // lead 4m
			Ann(27, AnnotationLabel.Escalating), // lead 6m
		};

		var r = DetectionEfficacyAnalyzer.Analyze(alerts, annotations, TimeSpan.FromMinutes(10));

		Assert.AreEqual(3, r.PrecededByAlert);
		Assert.AreEqual(TimeSpan.FromMinutes(4), r.MedianLeadTime);
	}
}
