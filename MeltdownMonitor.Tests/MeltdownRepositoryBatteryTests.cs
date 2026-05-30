using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MeltdownRepositoryBatteryTests
{
	[TestMethod]
	public void InsertBattery_RoundTripsThroughStaticReader()
	{
		var path = NewTempDbPath();
		try
		{
			var ts = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
			using (var repo = new MeltdownRepository(path))
			{
				repo.InsertBattery(new BatteryReading(ts, 87));
			}

			var read = MeltdownRepository.ReadBatteryHistory(path, ts.AddMinutes(-1), ts.AddMinutes(1));

			Assert.AreEqual(1, read.Count);
			Assert.AreEqual(ts, read[0].Timestamp);
			Assert.AreEqual(87, read[0].Percent);
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void ReadBatteryHistory_FiltersByWindowAndOrdersAscending()
	{
		var path = NewTempDbPath();
		try
		{
			var t0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
			using (var repo = new MeltdownRepository(path))
			{
				repo.InsertBattery(new BatteryReading(t0, 90));
				repo.InsertBattery(new BatteryReading(t0.AddMinutes(10), 88));
				repo.InsertBattery(new BatteryReading(t0.AddHours(48), 50));
			}

			var read = MeltdownRepository.ReadBatteryHistory(path, t0.AddMinutes(-1), t0.AddHours(1));

			Assert.AreEqual(2, read.Count, "The third reading is outside the window.");
			Assert.AreEqual(90, read[0].Percent);
			Assert.AreEqual(88, read[1].Percent);
			Assert.IsTrue(read[0].Timestamp < read[1].Timestamp, "Results must be chronological.");
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void InsertBattery_SameTimestampReplacesPercent()
	{
		// INSERT OR REPLACE keys on the timestamp, so a re-read at the same instant
		// updates rather than failing on the primary key.
		var path = NewTempDbPath();
		try
		{
			var ts = DateTimeOffset.UtcNow;
			using (var repo = new MeltdownRepository(path))
			{
				repo.InsertBattery(new BatteryReading(ts, 80));
				repo.InsertBattery(new BatteryReading(ts, 79));
			}

			var read = MeltdownRepository.ReadBatteryHistory(path, ts.AddMinutes(-1), ts.AddMinutes(1));

			Assert.AreEqual(1, read.Count);
			Assert.AreEqual(79, read[0].Percent);
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
