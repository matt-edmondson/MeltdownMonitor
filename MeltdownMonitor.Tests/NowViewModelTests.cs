using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;
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
