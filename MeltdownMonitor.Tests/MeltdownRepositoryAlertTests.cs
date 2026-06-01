using Microsoft.Data.Sqlite;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MeltdownRepositoryAlertTests
{
	[TestMethod]
	public void Alert_RoundTripsKind()
	{
		var path = NewTempDbPath();
		try
		{
			var ts = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
			using (var repo = new MeltdownRepository(path))
			{
				repo.InsertAlert(new AlertPayload(ts, "hyper", 20, 50, AlertKind.Hyperarousal));
				repo.InsertAlert(new AlertPayload(ts.AddMinutes(1), "hypo", 18, 50, AlertKind.Hypoarousal));
			}

			var read = MeltdownRepository.ReadAlerts(path, ts.AddMinutes(-1), ts.AddMinutes(5));

			Assert.AreEqual(2, read.Count);
			Assert.AreEqual(AlertKind.Hyperarousal, read[0].Kind);
			Assert.AreEqual(AlertKind.Hypoarousal, read[1].Kind);
			Assert.AreEqual("hypo", read[1].TriggerReason);
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void DefaultKindAlert_PersistsAsHyperarousal()
	{
		// The 4-arg construction used by every legacy call site defaults to Hyperarousal.
		var path = NewTempDbPath();
		try
		{
			var ts = DateTimeOffset.UtcNow;
			using (var repo = new MeltdownRepository(path))
			{
				repo.InsertAlert(new AlertPayload(ts, "x", 20, 50));
			}

			var read = MeltdownRepository.ReadAlerts(path, ts.AddMinutes(-1), ts.AddMinutes(1));

			Assert.AreEqual(1, read.Count);
			Assert.AreEqual(AlertKind.Hyperarousal, read[0].Kind);
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void MigratesLegacyAlertsTable_NullKindReadsAsHyperarousal()
	{
		var path = NewTempDbPath();
		try
		{
			// A pre-2026-06-01 alerts table with no kind column, holding one row.
			using (var conn = new SqliteConnection($"Data Source={path}"))
			{
				conn.Open();
				using var cmd = conn.CreateCommand();
				cmd.CommandText = """
					CREATE TABLE alerts (
						ts                  INTEGER PRIMARY KEY,
						trigger_reason      TEXT    NOT NULL,
						rmssd_at_trigger    REAL,
						baseline_at_trigger REAL
					);
					INSERT INTO alerts (ts, trigger_reason, rmssd_at_trigger, baseline_at_trigger)
					VALUES (1700000000000, 'legacy', 20, 50);
					""";
				cmd.ExecuteNonQuery();
			}

			// Opening with the repository runs the migration (adds the kind column).
			using (var _ = new MeltdownRepository(path))
			{
			}

			var read = MeltdownRepository.ReadAlerts(
				path,
				DateTimeOffset.FromUnixTimeMilliseconds(1_699_999_000_000),
				DateTimeOffset.FromUnixTimeMilliseconds(1_700_001_000_000));

			Assert.AreEqual(1, read.Count);
			Assert.AreEqual("legacy", read[0].TriggerReason);
			Assert.AreEqual(AlertKind.Hyperarousal, read[0].Kind,
				"A legacy row with a null kind must read back as Hyperarousal.");
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
