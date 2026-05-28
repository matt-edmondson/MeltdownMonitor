namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// Hands the user a copy of the SQLite database via the platform's share
/// surface (UIActivityViewController on iOS, design doc §6 "export DB").
/// Implementation lives in the head project so this assembly stays free of
/// UIKit / AppKit dependencies.
/// </summary>
public interface IDatabaseExporter
{
	/// <summary>
	/// Present the share sheet for the database at <paramref name="databasePath"/>.
	/// Returns when the user dismisses the sheet. Safe to call multiple
	/// times; each call presents a fresh sheet.
	/// </summary>
	Task ExportAsync(string databasePath);
}
