using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MeltdownRepositoryContactTests
{
	private static HrvSample Sample(DateTimeOffset ts, SensorContactStatus contact) => new(
		Timestamp: ts,
		Rmssd: 40, Pnn50: 10, MeanHr: 70,
		BaselineRmssd: 45, BaselineHr: 68,
		State: DetectorState.Watching)
	{
		SensorContact = contact,
	};

	[TestMethod]
	public void InsertThenGet_RoundTripsContact()
	{
		var path = NewTempDbPath();
		var ts = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
		try
		{
			using var repo = new MeltdownRepository(path);
			repo.InsertHrvSample(Sample(ts, SensorContactStatus.NotDetected));

			var read = repo.GetHrvSamples(ts.AddMinutes(-1), ts.AddMinutes(1));

			Assert.AreEqual(1, read.Count);
			Assert.AreEqual(SensorContactStatus.NotDetected, read[0].SensorContact);
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void InsertThenReadHistory_RoundTripsContact()
	{
		var path = NewTempDbPath();
		var ts = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
		try
		{
			using (var repo = new MeltdownRepository(path))
			{
				repo.InsertHrvSample(Sample(ts, SensorContactStatus.Detected));
			}

			var read = MeltdownRepository.ReadHistory(path, ts.AddMinutes(-1), ts.AddMinutes(1));

			Assert.AreEqual(1, read.Count);
			Assert.AreEqual(SensorContactStatus.Detected, read[0].SensorContact);
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void DefaultSample_ReadsBackAsNotSupported()
	{
		var path = NewTempDbPath();
		var ts = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
		try
		{
			using var repo = new MeltdownRepository(path);
			repo.InsertHrvSample(new HrvSample(ts, 40, 10, 70, 45, 68, DetectorState.Watching));

			var read = repo.GetHrvSamples(ts.AddMinutes(-1), ts.AddMinutes(1));

			Assert.AreEqual(1, read.Count);
			Assert.AreEqual(SensorContactStatus.NotSupported, read[0].SensorContact);
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
			try { File.Delete(f); } catch { /* best-effort temp cleanup */ }
		}
	}
}
