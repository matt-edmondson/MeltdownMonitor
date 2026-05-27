using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// Replays beats from a logged SQLite database for offline threshold tuning.
/// Reads in chronological order; does not re-apply the artifact filter since
/// the artifact flag is stored in the database.
/// </summary>
public sealed class ReplayBeatSource : IBeatSource
{
	private readonly string _databasePath;

	public ReplayBeatSource(string databasePath)
	{
		_databasePath = databasePath;
	}

	public async IAsyncEnumerable<Beat> GetBeatsAsync(
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		using var conn = new SqliteConnection($"Data Source={_databasePath}");
		conn.Open();

		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT ts, rr_ms, hr_bpm, artifact FROM beats ORDER BY ts";

		using var reader = cmd.ExecuteReader();
		while (reader.Read())
		{
			cancellationToken.ThrowIfCancellationRequested();
			var ts = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0));
			double rr = reader.GetDouble(1);
			int hr = reader.GetInt32(2);
			bool artifact = reader.GetInt32(3) != 0;
			yield return new Beat(ts, rr, hr, artifact);
			await Task.Yield();
		}
	}
}
