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
	public void OnStateChanged_UpdatesStatePillWithoutASample()
	{
		var vm = new NowViewModel();

		vm.OnStateChanged(DetectorState.Alerting);

		Assert.AreEqual(DetectorState.Alerting, vm.State);
		Assert.AreEqual(0, vm.RmssdHistory.Count, "A bare state change should not push a chart point.");
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
	public void AnnotationLabels_ExposesTheFourCheckInChoices()
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
		Assert.AreEqual(0.42, vm.RegulationTrail[^1].Index, 0.001);
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
		Assert.AreEqual(199 / 200.0, vm.RegulationTrail[^1].Index, 0.001);
		// Oldest retained entry is newer than the very first push (which fell off).
		Assert.IsTrue(vm.RegulationTrail[0].Index > 0.0, "the oldest readings should have been trimmed");
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

	private static HrvSample Sample(double rmssd, double meanHr, double baseline, DetectorState state) =>
		new(
			DateTimeOffset.UtcNow,
			rmssd,
			Pnn50: 20,
			meanHr,
			BaselineRmssd: baseline,
			BaselineHr: 65,
			state);
}
