using Android.Content;
using AndroidX.Core.Content;
using MeltdownMonitor.Mobile.Services;
using Uri = Android.Net.Uri;

namespace MeltdownMonitor.Android.Services;

/// <summary>
/// <see cref="IDatabaseExporter"/> that hands the SQLite database out through an
/// <c>ACTION_SEND</c> share chooser, with the file exposed via a
/// <c>FileProvider</c> content URI (design doc §6 / §8) — the Android analog of
/// the iOS share-sheet exporter. A flushed copy is shared rather than the live
/// file so an in-flight write can never be observed half-committed.
/// </summary>
public sealed class IntentDatabaseExporter : IDatabaseExporter
{
	private const string Authority = "com.matthewedmondson.meltdownmonitor.fileprovider";

	private readonly Context _context;

	public IntentDatabaseExporter(Context context) =>
		_context = context ?? throw new ArgumentNullException(nameof(context));

	public Task ExportAsync(string databasePath)
	{
		try
		{
			string copyPath = CopyToCache(databasePath);
			var uri = FileProvider.GetUriForFile(_context, Authority, new global::Java.IO.File(copyPath));

			var send = new Intent(Intent.ActionSend);
			send.SetType("application/octet-stream");
			send.PutExtra(Intent.ExtraStream, uri);
			send.AddFlags(ActivityFlags.GrantReadUriPermission);

			var chooser = Intent.CreateChooser(send, "Export database");
			// Started from the application context (no Activity in scope), so a new
			// task is required.
			chooser!.AddFlags(ActivityFlags.NewTask);
			_context.StartActivity(chooser);
		}
		catch (global::Java.Lang.Exception)
		{
			// Export is best-effort; never crash the caller if the share fails.
		}
		catch (IOException)
		{
		}

		return Task.CompletedTask;
	}

	private string CopyToCache(string databasePath)
	{
		string exportsDir = Path.Combine(_context.CacheDir!.AbsolutePath, "exports");
		Directory.CreateDirectory(exportsDir);
		string dest = Path.Combine(exportsDir, "meltdownmonitor-export.db");
		File.Copy(databasePath, dest, overwrite: true);
		return dest;
	}
}
