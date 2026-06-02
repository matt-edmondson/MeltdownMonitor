using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MeltdownRepositoryRecentSamplesTests
{
	[TestMethod]
	public void ReadRecentHrvSamples_ReturnsNewestLimitOldestFirst()
	{
		var path = NewTempDbPath();
		try
		{
			var t0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
			using var repo = new MeltdownRepository(path);
			for (int i = 0; i < 5; i++)
			{
				repo.InsertHrvSample(Sample(t0.AddSeconds(i * 5), rmssd: 30 + i));
			}

			// Ask for the last 3 of 5: should be samples i=2,3,4 in chronological order.
			var recent = repo.ReadRecentHrvSamples(3);

			Assert.AreEqual(3, recent.Count);
			Assert.AreEqual(32.0, recent[0].Rmssd, 1e-9);
			Assert.AreEqual(33.0, recent[1].Rmssd, 1e-9);
			Assert.AreEqual(34.0, recent[2].Rmssd, 1e-9);
			Assert.IsTrue(recent[0].Timestamp < recent[2].Timestamp, "oldest first");
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void ReadRecentHrvSamples_FewerRowsThanLimit_ReturnsAll()
	{
		var path = NewTempDbPath();
		try
		{
			var t0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
			using var repo = new MeltdownRepository(path);
			repo.InsertHrvSample(Sample(t0, rmssd: 40));
			repo.InsertHrvSample(Sample(t0.AddSeconds(5), rmssd: 41));

			var recent = repo.ReadRecentHrvSamples(100);

			Assert.AreEqual(2, recent.Count);
			Assert.AreEqual(40.0, recent[0].Rmssd, 1e-9);
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void ReadRecentHrvSamples_EmptyTable_ReturnsEmpty()
	{
		var path = NewTempDbPath();
		try
		{
			using var repo = new MeltdownRepository(path);
			Assert.AreEqual(0, repo.ReadRecentHrvSamples(10).Count);
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void ReadRecentHrvSamples_NonPositiveLimit_Throws()
	{
		var path = NewTempDbPath();
		try
		{
			using var repo = new MeltdownRepository(path);
			Assert.Throws<ArgumentOutOfRangeException>(() => repo.ReadRecentHrvSamples(0));
		}
		finally
		{
			TryDelete(path);
		}
	}

	private static HrvSample Sample(DateTimeOffset ts, double rmssd) =>
		new(ts, rmssd, 20, 70, 50, 70, DetectorState.Watching);

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
