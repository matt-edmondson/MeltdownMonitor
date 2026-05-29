using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MeltdownRepositoryOptionsTests
{
	[TestMethod]
	public void IosSandbox_ForcesTruncateJournalMode()
	{
		var path = NewTempDbPath();
		try
		{
			using var repo = new MeltdownRepository(path, MeltdownRepositoryOptions.IosSandbox);
			Assert.AreEqual("truncate", repo.JournalMode,
				"iOS sandbox profile must disable WAL in favour of TRUNCATE (design doc §4.7).");
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void Default_LeavesSqliteRollbackJournal()
	{
		// No pragma override → SQLite's rollback-journal default ("delete"),
		// confirming desktop callers are unaffected by the iOS tuning.
		var path = NewTempDbPath();
		try
		{
			using var repo = new MeltdownRepository(path);
			Assert.AreEqual("delete", repo.JournalMode);
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
