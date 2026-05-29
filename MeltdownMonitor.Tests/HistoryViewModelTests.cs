using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.Tests;

[TestClass]
public class HistoryViewModelTests
{
	[TestMethod]
	public async Task LoadAsync_MergesAnnotationsIntoStateTimelineChronologically()
	{
		var path = NewTempDbPath();
		try
		{
			var t0 = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30);
			using (var repo = new MeltdownRepository(path))
			{
				repo.InsertHrvSample(Sample(t0, DetectorState.Idle));
				repo.InsertHrvSample(Sample(t0.AddMinutes(1), DetectorState.Watching));
				repo.InsertHrvSample(Sample(t0.AddMinutes(2), DetectorState.Watching));
				repo.InsertHrvSample(Sample(t0.AddMinutes(3), DetectorState.Warning));
				repo.InsertAnnotation(t0.AddSeconds(90), AnnotationLabel.Edged, "note");
			}

			var vm = new HistoryViewModel(path);
			await vm.LoadAsync();

			// Idle@0, Watching@60s, Edged@90s, Warning@180s — the repeated
			// Watching sample collapses, the annotation slots in by timestamp.
			Assert.AreEqual(4, vm.Events.Count);
			Assert.AreEqual(HistoryEventKind.StateChange, vm.Events[0].Kind);
			Assert.AreEqual(DetectorState.Idle, vm.Events[0].State);
			Assert.AreEqual(DetectorState.Watching, vm.Events[1].State);

			var annotation = vm.Events[2];
			Assert.AreEqual(HistoryEventKind.Annotation, annotation.Kind);
			Assert.AreEqual(AnnotationLabel.Edged, annotation.AnnotationLabel);
			StringAssert.Contains(annotation.Title, "Edged", StringComparison.Ordinal);
			Assert.AreEqual("note", annotation.Detail);

			Assert.AreEqual(DetectorState.Warning, vm.Events[3].State);
			Assert.IsFalse(vm.IsEmpty);
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public async Task LoadAsync_AnnotationWithoutNotes_ShowsFallbackDetail()
	{
		var path = NewTempDbPath();
		try
		{
			var ts = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
			using (var repo = new MeltdownRepository(path))
			{
				repo.InsertAnnotation(ts, AnnotationLabel.Fine);
			}

			var vm = new HistoryViewModel(path);
			await vm.LoadAsync();

			Assert.AreEqual(1, vm.Events.Count);
			Assert.AreEqual("Self check-in", vm.Events[0].Detail);
		}
		finally
		{
			TryDelete(path);
		}
	}

	private static HrvSample Sample(DateTimeOffset ts, DetectorState state) =>
		new(ts, Rmssd: 40, Pnn50: 20, MeanHr: 70, BaselineRmssd: 50, BaselineHr: 65, state);

	private static string NewTempDbPath() =>
		Path.Combine(Path.GetTempPath(), $"mm-{Guid.NewGuid():N}.db");

	private static void TryDelete(string path)
	{
		foreach (var f in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
		{
			try
			{
				File.Delete(f);
			}
			catch
			{
				// best-effort temp cleanup
			}
		}
	}
}
