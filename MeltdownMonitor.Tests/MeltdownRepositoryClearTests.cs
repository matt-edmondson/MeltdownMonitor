using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MeltdownRepositoryClearTests
{
	[TestMethod]
	public void ClearAllData_RemovesEveryStream()
	{
		string path = NewTempDbPath();
		try
		{
			var t0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
			using var repo = new MeltdownRepository(path);
			repo.InsertBeat(new Beat(t0, 800, 75, IsArtifact: false));
			repo.InsertHrvSample(new HrvSample(t0, 40, 20, 70, 50, 70, DetectorState.Watching));
			repo.InsertAlert(new AlertPayload(t0, "test", 30, 50, AlertKind.Hyperarousal));
			repo.InsertAnnotation(t0, AnnotationLabel.Edged);
			repo.InsertBattery(new BatteryReading(t0, 80));

			repo.ClearAllData();

			Assert.AreEqual(0, repo.ReadRecentHrvSamples(100).Count);
			Assert.AreEqual(0, repo.GetHrvSamples(t0.AddDays(-1), t0.AddDays(1)).Count);
			Assert.AreEqual(0, MeltdownRepository.ReadAlerts(path, t0.AddDays(-1), t0.AddDays(1)).Count);
			Assert.AreEqual(0, MeltdownRepository.ReadAnnotations(path, t0.AddDays(-1), t0.AddDays(1)).Count);
			Assert.AreEqual(0, MeltdownRepository.ReadBatteryHistory(path, t0.AddDays(-1), t0.AddDays(1)).Count);
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void ClearAllData_OnEmptyDatabase_IsSafe()
	{
		string path = NewTempDbPath();
		try
		{
			using var repo = new MeltdownRepository(path);
			repo.ClearAllData();
			Assert.AreEqual(0, repo.ReadRecentHrvSamples(10).Count);
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void ClearAllData_KeepsRepositoryUsable()
	{
		string path = NewTempDbPath();
		try
		{
			var t0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
			using var repo = new MeltdownRepository(path);
			repo.InsertHrvSample(new HrvSample(t0, 40, 20, 70, 50, 70, DetectorState.Watching));
			repo.ClearAllData();

			// Writing after a clear still works and is readable.
			repo.InsertHrvSample(new HrvSample(t0.AddSeconds(5), 42, 21, 72, 50, 70, DetectorState.Watching));
			Assert.AreEqual(1, repo.ReadRecentHrvSamples(100).Count);
		}
		finally
		{
			TryDelete(path);
		}
	}

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
