namespace MeltdownMonitor.Core.Persistence;

/// <summary>
/// Connection tuning for <see cref="MeltdownRepository"/>. Keeps the
/// platform-specific SQLite knobs out of the repository's behaviour so the
/// desktop build stays on SQLite's defaults while the iOS head can opt into
/// the data-protection-friendly settings described in design doc §4.7.
/// </summary>
/// <param name="JournalMode">
/// Value for <c>PRAGMA journal_mode</c> (e.g. <c>TRUNCATE</c>), or null to
/// leave SQLite's default (which is WAL once the DB has been written).
/// </param>
/// <param name="FullFsync">
/// When true, sets <c>PRAGMA fullfsync=ON</c> so commits are flushed all the
/// way to disk — matters on iOS where the app can be suspended mid-write.
/// </param>
public sealed record MeltdownRepositoryOptions(string? JournalMode = null, bool FullFsync = false)
{
	/// <summary>Desktop default — no extra pragmas, SQLite's own behaviour.</summary>
	public static MeltdownRepositoryOptions Default { get; } = new();

	/// <summary>
	/// iOS sandbox profile: TRUNCATE journal (no WAL sidecar files that the
	/// data-protection layer can lock out) plus full fsync durability.
	/// </summary>
	public static MeltdownRepositoryOptions IosSandbox { get; } =
		new(JournalMode: "TRUNCATE", FullFsync: true);
}
