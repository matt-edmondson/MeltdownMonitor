namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// Platform facade for handing the SQLite database out to the user — iOS
/// doesn't let users browse the sandbox, so export goes through the system
/// share sheet (design doc §6.5 / §10). The iOS head implements this with a
/// <c>UIActivityViewController</c>; other hosts can copy the file to a
/// user-chosen location.
/// </summary>
public interface IDatabaseExporter
{
	/// <summary>
	/// Share or copy the database at <paramref name="databasePath"/>. The
	/// implementation is responsible for handing out a flushed, self-consistent
	/// copy (the live file may still be open for writing).
	/// </summary>
	Task ExportAsync(string databasePath);
}
