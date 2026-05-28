using MeltdownMonitor.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MeltdownRepositoryOptionsTests
{
	[TestMethod]
	public void DesktopDefaults_LeaveJournalAtDefault()
	{
		string path = Path.Combine(Path.GetTempPath(), $"mm-test-{Guid.NewGuid():N}.db");
		try
		{
			using (var repo = new MeltdownRepository(path, MeltdownRepositoryOptions.DesktopDefaults))
			{
				// Constructor side-effects only.
			}

			Assert.AreNotEqual(
				"truncate",
				ReadJournalMode(path),
				ignoreCase: true,
				"Desktop defaults should not force TRUNCATE journaling.");
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void MobileSafeDefaults_ApplyTruncateJournal()
	{
		string path = Path.Combine(Path.GetTempPath(), $"mm-test-{Guid.NewGuid():N}.db");
		try
		{
			using (var repo = new MeltdownRepository(path, MeltdownRepositoryOptions.MobileSafeDefaults))
			{
				// Constructor side-effects only.
			}

			Assert.AreEqual(
				"truncate",
				ReadJournalMode(path),
				ignoreCase: true,
				"Mobile defaults must force TRUNCATE journaling (design doc §4.7).");
		}
		finally
		{
			TryDelete(path);
		}
	}

	private static string ReadJournalMode(string path)
	{
		using var conn = new SqliteConnection($"Data Source={path}");
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "PRAGMA journal_mode";
		using var reader = cmd.ExecuteReader();
		reader.Read();
		return reader.GetString(0);
	}

	private static void TryDelete(string path)
	{
		try
		{
			SqliteConnection.ClearAllPools();
			if (File.Exists(path)) File.Delete(path);
		}
		catch
		{
		}
	}
}
