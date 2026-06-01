using System.Reflection;
using System.Runtime.CompilerServices;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Mobile;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MobilePipelineThresholdTests
{
	/// <summary>
	/// The mobile Settings tab edits thresholds by replacing the whole
	/// <see cref="DetectionThresholds"/> record (<c>settings.Thresholds = ... with { ... }</c>).
	/// The pipeline's detector must read those edits live, mirroring the desktop
	/// pipeline. If the detector snapshots the record at construction, a lowered
	/// threshold never reaches detection until the pipeline is rebuilt.
	/// </summary>
	[TestMethod]
	public void LiveThresholdEdit_IsHonouredByDetector()
	{
		var settings = new MobileSettings();
		using var repo = new MeltdownRepository(":memory:");
		using var pipeline = new Pipeline(settings, repo, new EmptyBeatSource());

		var detector = Detector(pipeline);

		// A 10% RMSSD drop is well below the default 50% severe-alert threshold,
		// so at construction-time thresholds this sample does NOT alert.
		var sample = new HrvSample(
			Timestamp: DateTimeOffset.UnixEpoch,
			Rmssd: 90.0,
			Pnn50: 0.0,
			MeanHr: 60.0,
			BaselineRmssd: 100.0,
			BaselineHr: 60.0,
			State: DetectorState.Idle);

		// User lowers the severe-drop threshold in Settings AFTER the pipeline
		// (and its detector) were constructed. Pin SevereDropConfirmationCount = 1 so
		// the single qualifying sample below fires immediately — this test exercises
		// live threshold reading, not the (default 2) severe-confirmation mechanic.
		settings.Thresholds = settings.Thresholds with
		{
			RmssdAlertingDropFraction = 0.05,
			SevereDropConfirmationCount = 1,
		};

		bool alertFired = false;
		pipeline.AlertFired += _ => alertFired = true;

		var state = detector.Process(sample, baselineIsWarm: true);

		Assert.AreEqual(DetectorState.Alerting, state,
			"A 10% RMSSD drop must alert once the severe threshold is lowered to 5% — the detector must read the edited thresholds, not a stale snapshot.");
		Assert.IsTrue(alertFired, "Lowering the threshold should have fired an alert through the pipeline.");
	}

	private static DysregulationDetector Detector(Pipeline pipeline)
	{
		var field = typeof(Pipeline).GetField(
			"_detector",
			BindingFlags.Instance | BindingFlags.NonPublic);
		return (DysregulationDetector)field!.GetValue(pipeline)!;
	}

	private sealed class EmptyBeatSource : IBeatSource
	{
#pragma warning disable CS1998 // intentionally empty async stream
		public async IAsyncEnumerable<Beat> GetBeatsAsync(
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			yield break;
		}
#pragma warning restore CS1998
	}
}
