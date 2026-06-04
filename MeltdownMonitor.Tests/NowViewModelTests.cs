using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Core.Regulation;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.Tests;

[TestClass]
public class NowViewModelTests
{
	// On a plain test thread Avalonia's Dispatcher.UIThread.CheckAccess()
	// returns true, so NowViewModel applies updates synchronously — no UI
	// pump needed to observe the results.

	[TestMethod]
	public void OnSampleUpdated_UpdatesReadoutsStateAndConnection()
	{
		var vm = new NowViewModel();
		Assert.AreEqual(ConnectionState.Disconnected, vm.Connection);

		vm.OnSampleUpdated(Sample(rmssd: 38, meanHr: 81, baseline: 55, state: DetectorState.Warning));

		Assert.AreEqual(81, vm.HeartRate, 0.001);
		Assert.AreEqual(38, vm.Rmssd, 0.001);
		Assert.AreEqual(55, vm.BaselineRmssd, 0.001);
		Assert.AreEqual(DetectorState.Warning, vm.State);
		Assert.AreEqual(ConnectionState.Connected, vm.Connection, "A flowing sample means the link is live.");
		Assert.AreEqual(1, vm.RmssdHistory.Count);
		Assert.AreEqual(1, vm.BaselineHistory.Count);
	}

	[TestMethod]
	public void OnHypoarousalStateChanged_TogglesIsShutdown()
	{
		var vm = new NowViewModel();
		Assert.IsFalse(vm.IsShutdown);

		vm.OnHypoarousalStateChanged(HypoarousalState.LowArousal);
		Assert.IsTrue(vm.IsShutdown, "Entering LowArousal flags shutdown.");

		vm.OnHypoarousalStateChanged(HypoarousalState.Monitoring);
		Assert.IsFalse(vm.IsShutdown, "Leaving LowArousal clears the flag.");
	}

	[TestMethod]
	public void OnStateChanged_UpdatesStatePillWithoutASample()
	{
		var vm = new NowViewModel();

		vm.OnStateChanged(DetectorState.Alerting);

		Assert.AreEqual(DetectorState.Alerting, vm.State);
		Assert.AreEqual(0, vm.RmssdHistory.Count, "A bare state change should not push a chart point.");
	}

	[TestMethod]
	public void OnRecoveryUpdated_SurfacesRecoveryReadout()
	{
		var vm = new NowViewModel();
		Assert.IsFalse(vm.IsRecoveryVisible, "No episode → nothing to recover from.");

		// Metrics in band, 50% through the hold → two-stage Overall of 0.75.
		vm.OnRecoveryUpdated(new RecoveryProgress(MetricProximity: 1.0, HoldProgress: 0.5, IsActive: true));

		Assert.IsTrue(vm.IsRecoveryVisible);
		Assert.AreEqual(0.75, vm.RecoveryFraction, 1e-9);
		StringAssert.Contains(vm.RecoveryText, "75");

		vm.OnRecoveryUpdated(RecoveryProgress.Inactive);
		Assert.IsFalse(vm.IsRecoveryVisible);
	}

	[TestMethod]
	public void StateLabel_ReflectsPauseOverride()
	{
		var vm = new NowViewModel();
		vm.OnStateChanged(DetectorState.Watching);

		vm.IsPaused = true;

		StringAssert.Contains(vm.StateLabel, "Paused", StringComparison.OrdinalIgnoreCase);
	}

	[TestMethod]
	public void OpenAnnotationCommand_ShowsSheet()
	{
		var vm = new NowViewModel();
		Assert.IsFalse(vm.IsAnnotationSheetOpen);

		vm.OpenAnnotationCommand.Execute(null);

		Assert.IsTrue(vm.IsAnnotationSheetOpen);
	}

	[TestMethod]
	public async Task RecordAnnotation_InvokesCallbackWithTrimmedNotesThenClosesSheet()
	{
		(AnnotationLabel Label, string? Notes)? captured = null;
		var vm = new NowViewModel(onAnnotate: (label, notes) =>
		{
			captured = (label, notes);
			return Task.CompletedTask;
		});
		vm.OpenAnnotationCommand.Execute(null);
		vm.AnnotationNotes = "  shaky  ";

		await vm.RecordAnnotationAsync(AnnotationLabel.Escalating);

		Assert.IsNotNull(captured);
		Assert.AreEqual(AnnotationLabel.Escalating, captured.Value.Label);
		Assert.AreEqual("shaky", captured.Value.Notes, "Notes should be trimmed before persisting.");
		Assert.IsFalse(vm.IsAnnotationSheetOpen, "Recording dismisses the sheet.");
		Assert.AreEqual(string.Empty, vm.AnnotationNotes, "Notes reset after recording.");
	}

	[TestMethod]
	public async Task RecordAnnotation_BlankNotesBecomeNull()
	{
		string? captured = "sentinel";
		var vm = new NowViewModel(onAnnotate: (_, notes) =>
		{
			captured = notes;
			return Task.CompletedTask;
		});
		vm.AnnotationNotes = "   ";

		await vm.RecordAnnotationAsync(AnnotationLabel.Fine);

		Assert.IsNull(captured, "Whitespace-only notes must collapse to null.");
	}

	[TestMethod]
	public void CancelAnnotation_ClosesSheetAndClearsNotes()
	{
		var vm = new NowViewModel();
		vm.OpenAnnotationCommand.Execute(null);
		vm.AnnotationNotes = "draft";

		vm.CancelAnnotationCommand.Execute(null);

		Assert.IsFalse(vm.IsAnnotationSheetOpen);
		Assert.AreEqual(string.Empty, vm.AnnotationNotes);
	}

	[TestMethod]
	public void AnnotationLabels_ExposesEveryCheckInChoice()
	{
		var vm = new NowViewModel();

		CollectionAssert.AreEquivalent(
			Enum.GetValues<AnnotationLabel>(),
			vm.AnnotationLabels.ToArray());
	}

	[TestMethod]
	public void BatteryText_IsPlaceholderUntilAReadingArrives()
	{
		var vm = new NowViewModel();

		Assert.IsNull(vm.BatteryPercent);
		StringAssert.Contains(vm.BatteryText, "—", StringComparison.Ordinal);
	}

	[TestMethod]
	public void OnBatteryUpdated_SetsPercentAndText()
	{
		var vm = new NowViewModel();

		vm.OnBatteryUpdated(new BatteryReading(DateTimeOffset.UtcNow, 73));

		Assert.AreEqual(73, vm.BatteryPercent);
		StringAssert.Contains(vm.BatteryText, "73%", StringComparison.Ordinal);
	}

	[TestMethod]
	public void DeviceInfo_DefaultsToHidden()
	{
		var vm = new NowViewModel();

		Assert.IsFalse(vm.HasDeviceInfo);
		Assert.IsNull(vm.DeviceInfoText);
	}

	[TestMethod]
	public void OnDeviceInfoUpdated_ExposesSummary()
	{
		var vm = new NowViewModel();

		vm.OnDeviceInfoUpdated(new DeviceInformation(ModelNumber: "Polar H10", FirmwareRevision: "3.1.1"));

		Assert.IsTrue(vm.HasDeviceInfo);
		Assert.AreEqual("Polar H10 · fw 3.1.1", vm.DeviceInfoText);
	}

	[TestMethod]
	public void Contact_DefaultsToNotSupportedAndNotLost()
	{
		var vm = new NowViewModel();

		Assert.AreEqual(SensorContactStatus.NotSupported, vm.Contact);
		Assert.IsFalse(vm.IsContactLost);
	}

	[TestMethod]
	public void OnContactChanged_LostThenRegained_TogglesWarning()
	{
		var vm = new NowViewModel();

		vm.OnContactChanged(SensorContactStatus.NotDetected);
		Assert.IsTrue(vm.IsContactLost, "A supported-but-absent contact should raise the warning.");

		vm.OnContactChanged(SensorContactStatus.Detected);
		Assert.IsFalse(vm.IsContactLost, "Regaining contact clears the warning.");
	}

	[TestMethod]
	public void OnReadingUpdated_SetsReadingAndAppendsToTrail()
	{
		var vm = new NowViewModel();
		Assert.AreEqual(0, vm.RegulationTrail.Count);

		var reading = new RegulationReading(Index: 0.42, VariabilityQuality: 0.7, Confidence: 1.0, LobeRoundness: 0.5, LfHfBalance: 0.0);
		vm.OnReadingUpdated(reading);

		Assert.AreEqual(0.42, vm.Reading.Index, 0.001);
		Assert.AreEqual(1, vm.RegulationTrail.Count);
		Assert.AreEqual(0.42, vm.RegulationTrail[^1].Reading.Index, 0.001);
	}

	[TestMethod]
	public void OnReadingUpdated_TrailIsBoundedAndKeepsTheNewest()
	{
		var vm = new NowViewModel();

		// Push well past the trail cap; the oldest readings should fall off.
		for (int i = 0; i < 200; i++)
		{
			vm.OnReadingUpdated(new RegulationReading(Index: i / 200.0, VariabilityQuality: 1, Confidence: 1, LobeRoundness: 0.5, LfHfBalance: 0.0));
		}

		Assert.IsTrue(vm.RegulationTrail.Count is > 0 and <= 48,
			$"trail should be capped at 48, was {vm.RegulationTrail.Count}");
		// Newest reading is retained at the end of the trail.
		Assert.AreEqual(199 / 200.0, vm.RegulationTrail[^1].Reading.Index, 0.001);
		// Oldest retained entry is newer than the very first push (which fell off).
		Assert.IsTrue(vm.RegulationTrail[0].Reading.Index > 0.0, "the oldest readings should have been trimmed");
	}

	[TestMethod]
	public void OnReadingUpdated_HandsControlAFreshTrailInstance()
	{
		var vm = new NowViewModel();

		vm.OnReadingUpdated(new RegulationReading(0.1, 1, 1, 0.5, 0.0));
		var first = vm.RegulationTrail;
		vm.OnReadingUpdated(new RegulationReading(0.2, 1, 1, 0.5, 0.0));
		var second = vm.RegulationTrail;

		Assert.AreNotSame(first, second, "a new list instance must be published so AffectsRender fires");
	}

	[TestMethod]
	public void RegulationStateColor_TracksStateAndPause()
	{
		var vm = new NowViewModel();
		vm.OnStateChanged(DetectorState.Warning);
		Assert.AreEqual(StateColors.ColorFor(DetectorState.Warning), vm.RegulationStateColor);

		vm.IsPaused = true;
		Assert.AreEqual(StateColors.ColorFor(DetectorState.Warning, isPaused: true), vm.RegulationStateColor);
	}

	[TestMethod]
	public void Dynamics_IsSteady_ByDefault()
	{
		var vm = new NowViewModel();
		Assert.AreEqual(RegulationDynamics.Steady, vm.Dynamics);
		Assert.AreEqual("Steady", vm.TrendLabel);
		Assert.AreEqual("steady", vm.VelocityText);
		Assert.AreEqual(0.0, vm.NormalizedSpeed, 1e-12);
		Assert.IsFalse(vm.IsTrendVisible);
	}

	[TestMethod]
	public void OnDynamicsUpdated_Escalating_SetsLabelVelocityAndVisibility()
	{
		var vm = new NowViewModel();
		// A confident reading is required for the trend to be visible.
		vm.OnReadingUpdated(new RegulationReading(0.2, 0.8, 1.0, 0.5, 0.0));

		vm.OnDynamicsUpdated(new RegulationDynamics(0.03, RegulationTrend.Escalating, 0.6));

		Assert.AreEqual("Escalating", vm.TrendLabel);
		Assert.AreEqual("+0.03 /s", vm.VelocityText);
		Assert.AreEqual(0.6, vm.NormalizedSpeed, 1e-9);
		Assert.IsTrue(vm.IsTrendVisible);
	}

	[TestMethod]
	public void OnDynamicsUpdated_DeEscalating_FormatsNegativeRate()
	{
		var vm = new NowViewModel();
		vm.OnReadingUpdated(new RegulationReading(0.2, 0.8, 1.0, 0.5, 0.0));

		vm.OnDynamicsUpdated(new RegulationDynamics(-0.03, RegulationTrend.DeEscalating, 0.6));

		Assert.AreEqual("Easing", vm.TrendLabel);
		Assert.AreEqual("-0.03 /s", vm.VelocityText);
	}

	[TestMethod]
	public void IsTrendVisible_IsFalse_WhileCalibrating()
	{
		var vm = new NowViewModel();
		// Low confidence (baseline still warming) hides the trend even if escalating.
		vm.OnReadingUpdated(new RegulationReading(0.0, 1.0, 0.4, 0.5, 0.0));
		vm.OnDynamicsUpdated(new RegulationDynamics(0.03, RegulationTrend.Escalating, 0.6));

		Assert.IsFalse(vm.IsTrendVisible);
	}

	[TestMethod]
	public void Trail_CapsAtProvidedLength()
	{
		var vm = new NowViewModel(trailLengthProvider: () => 20);
		for (int i = 0; i < 50; i++)
		{
			vm.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));
		}

		Assert.AreEqual(20, vm.RegulationTrail.Count);
	}

	[TestMethod]
	public void Trail_NullProvider_CapsAtDefault48()
	{
		var vm = new NowViewModel();
		for (int i = 0; i < 100; i++)
		{
			vm.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));
		}

		Assert.AreEqual(48, vm.RegulationTrail.Count);
	}

	[TestMethod]
	public void Trail_LoweringCap_TrimsKeepingNewest()
	{
		int cap = 40;
		var vm = new NowViewModel(trailLengthProvider: () => cap);
		for (int i = 0; i < 40; i++)
		{
			vm.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));
		}

		cap = 20;
		vm.OnReadingUpdated(new RegulationReading(0.9, 1.0, 1.0, 0.5, 0.0)); // newest, distinct

		Assert.AreEqual(20, vm.RegulationTrail.Count);
		Assert.AreEqual(0.9, vm.RegulationTrail[^1].Reading.Index, 1e-9, "the newest reading must be kept");
	}

	[TestMethod]
	public void Trail_FreezesDetectorStateAtCaptureTime()
	{
		var vm = new NowViewModel();

		vm.OnStateChanged(DetectorState.Warning);
		vm.OnReadingUpdated(new RegulationReading(0.3, 1.0, 1.0, 0.5, 0.0));

		vm.OnStateChanged(DetectorState.Alerting);
		vm.OnReadingUpdated(new RegulationReading(0.6, 1.0, 1.0, 0.5, 0.0));

		Assert.AreEqual(2, vm.RegulationTrail.Count);
		Assert.AreEqual(DetectorState.Warning, vm.RegulationTrail[0].State,
			"the first point must keep the state it was captured under");
		Assert.AreEqual(DetectorState.Alerting, vm.RegulationTrail[1].State,
			"the second point captures the later state");
	}

	[TestMethod]
	public void Trail_ClampsProviderToValidRange()
	{
		var tiny = new NowViewModel(trailLengthProvider: () => 3);     // below 12 floor
		for (int i = 0; i < 300; i++)
		{
			tiny.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));
		}

		Assert.AreEqual(12, tiny.RegulationTrail.Count, "below-floor cap clamps to 12");

		// Push past the ceiling so the buffer actually reaches the clamped maximum.
		var huge = new NowViewModel(trailLengthProvider: () => 99999); // above 2160 ceiling
		for (int i = 0; i < 2200; i++)
		{
			huge.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));
		}

		Assert.AreEqual(2160, huge.RegulationTrail.Count, "above-ceiling cap clamps to 2160");
	}

	[TestMethod]
	public void HistogramBuckets_FlowFromProviders()
	{
		var vm = new NowViewModel(indexBucketsProvider: () => 32, vagalBucketsProvider: () => 12);
		vm.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));

		Assert.AreEqual(32, vm.IndexBuckets);
		Assert.AreEqual(12, vm.VagalBuckets);
	}

	[TestMethod]
	public void HistogramBuckets_NullProviders_DefaultTo24And16()
	{
		var vm = new NowViewModel();
		vm.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));

		Assert.AreEqual(24, vm.IndexBuckets);
		Assert.AreEqual(16, vm.VagalBuckets);
	}

	[TestMethod]
	public void HistogramBuckets_ClampProvidersToValidRange()
	{
		var low = new NowViewModel(indexBucketsProvider: () => 1, vagalBucketsProvider: () => 0);
		low.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));
		Assert.AreEqual(6, low.IndexBuckets, "below-floor clamps to 6");
		Assert.AreEqual(6, low.VagalBuckets, "below-floor clamps to 6");

		var high = new NowViewModel(indexBucketsProvider: () => 999, vagalBucketsProvider: () => 999);
		high.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));
		Assert.AreEqual(64, high.IndexBuckets, "above-ceiling clamps to 64");
		Assert.AreEqual(64, high.VagalBuckets, "above-ceiling clamps to 64");
	}

	[TestMethod]
	public void LobeSegments_FlowsFromProvider()
	{
		var vm = new NowViewModel(lobeSegmentsProvider: () => 128);
		vm.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));

		Assert.AreEqual(128, vm.LobeSegments);
	}

	[TestMethod]
	public void LobeSegments_NullProvider_DefaultsTo96()
	{
		var vm = new NowViewModel();
		vm.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));

		Assert.AreEqual(96, vm.LobeSegments);
	}

	[TestMethod]
	public void LobeSegments_ClampsProviderToValidRange()
	{
		var low = new NowViewModel(lobeSegmentsProvider: () => 1);
		low.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));
		Assert.AreEqual(24, low.LobeSegments, "below-floor clamps to 24");

		var high = new NowViewModel(lobeSegmentsProvider: () => 9999);
		high.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));
		Assert.AreEqual(256, high.LobeSegments, "above-ceiling clamps to 256");
	}

	[TestMethod]
	public void OnSampleUpdated_RecordsOneTimestampPerValue()
	{
		var vm = new NowViewModel();
		var t0 = DateTimeOffset.UtcNow;

		vm.OnSampleUpdated(SampleAt(t0, rmssd: 40));
		vm.OnSampleUpdated(SampleAt(t0.AddSeconds(5), rmssd: 42));

		Assert.AreEqual(2, vm.RmssdTimestamps.Count);
		Assert.AreEqual(vm.RmssdHistory.Count, vm.RmssdTimestamps.Count,
			"every charted value carries exactly one timestamp");
		Assert.AreEqual(5.0, vm.RmssdTimestamps[1] - vm.RmssdTimestamps[0], 1e-6,
			"timestamps are epoch seconds spaced by the real sample gap");
	}

	[TestMethod]
	public void OnSampleUpdated_TimestampsPreserveSubSecondSpacing()
	{
		var vm = new NowViewModel();
		var t0 = DateTimeOffset.UtcNow;

		vm.OnSampleUpdated(SampleAt(t0, rmssd: 40));
		vm.OnSampleUpdated(SampleAt(t0.AddMilliseconds(500), rmssd: 41));

		Assert.AreEqual(0.5, vm.RmssdTimestamps[1] - vm.RmssdTimestamps[0], 1e-6,
			"sub-second sample spacing must survive (epoch ms / 1000), not collapse to whole seconds");
	}

	[TestMethod]
	public void TrimHistory_KeepsValuesAndTimestampsTheSameLength()
	{
		var vm = new NowViewModel();
		var t0 = DateTimeOffset.UtcNow;

		for (int i = 0; i < 400; i++) // past the 360-point cap
		{
			vm.OnSampleUpdated(SampleAt(t0.AddSeconds(i), rmssd: 30 + (i % 5)));
		}

		Assert.AreEqual(vm.RmssdHistory.Count, vm.RmssdTimestamps.Count);
		Assert.AreEqual(360, vm.RmssdTimestamps.Count, "trimmed to exactly the 360-point cap");
	}

	[TestMethod]
	public void HypoarousalDynamics_IsSteady_ByDefault()
	{
		var vm = new NowViewModel();
		Assert.AreEqual(RegulationDynamics.Steady, vm.HypoarousalDynamics);
	}

	[TestMethod]
	public void OnHypoarousalDynamicsUpdated_PublishesAFreshValue()
	{
		var vm = new NowViewModel();
		var rising = new RegulationDynamics(0.03, RegulationTrend.Escalating, 0.6);

		vm.OnHypoarousalDynamicsUpdated(rising);

		Assert.AreEqual(rising, vm.HypoarousalDynamics);
	}

	[TestMethod]
	public void OnBeatReceived_CollectsNonArtifactRrAndCounts()
	{
		var vm = new NowViewModel();

		vm.OnBeatReceived(new Beat(DateTimeOffset.UnixEpoch, 820, 73, IsArtifact: false));
		vm.OnBeatReceived(new Beat(DateTimeOffset.UnixEpoch, 9999, 73, IsArtifact: true));
		vm.OnBeatReceived(new Beat(DateTimeOffset.UnixEpoch, 810, 74, IsArtifact: false));

		CollectionAssert.AreEqual(new[] { 820.0, 810.0 }, vm.RecentRr.ToList());
		Assert.AreEqual(2L, vm.RrBeatsAppended, "artifacts must not advance the beat timeline");
	}

	[TestMethod]
	public void OnBeatReceived_CapsBufferAt160KeepingNewest()
	{
		var vm = new NowViewModel();
		for (int i = 0; i < 200; i++)
		{
			vm.OnBeatReceived(new Beat(DateTimeOffset.UnixEpoch, 800 + i, 70, IsArtifact: false));
		}

		Assert.AreEqual(160, vm.RecentRr.Count, "buffer must cap at the desktop's RrBufferLength");
		Assert.AreEqual(999.0, vm.RecentRr[^1], 1e-9, "newest beat must be kept");
		Assert.AreEqual(200L, vm.RrBeatsAppended, "the absolute timeline keeps counting past the cap");
	}

	[TestMethod]
	public void OnBeatReceived_PublishesAFreshRrInstance()
	{
		var vm = new NowViewModel();
		vm.OnBeatReceived(new Beat(DateTimeOffset.UnixEpoch, 820, 73, IsArtifact: false));
		var first = vm.RecentRr;
		vm.OnBeatReceived(new Beat(DateTimeOffset.UnixEpoch, 810, 74, IsArtifact: false));

		Assert.AreNotSame(first, vm.RecentRr, "a new list instance must be published so AffectsRender fires");
	}

	private static HrvSample Sample(double rmssd, double meanHr, double baseline, DetectorState state) =>
		new(
			DateTimeOffset.UtcNow,
			rmssd,
			Pnn50: 20,
			meanHr,
			BaselineRmssd: baseline,
			BaselineHr: 65,
			state);

	private static HrvSample SampleAt(DateTimeOffset timestamp, double rmssd) =>
		new(
			timestamp,
			rmssd,
			Pnn50: 20,
			MeanHr: 70,
			BaselineRmssd: 50,
			BaselineHr: 65,
			DetectorState.Watching);
}
