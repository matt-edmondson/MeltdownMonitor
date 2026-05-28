using Foundation;
using MeltdownMonitor.Mobile.Services;
using UIKit;

namespace MeltdownMonitor.iOS.Services;

/// <summary>
/// Wraps <see cref="UIActivityViewController"/> so the user can hand off the
/// SQLite database to Files, AirDrop, Mail, or any third-party share target
/// (design doc §6). Copies the live DB to a temp file before sharing so the
/// activity controller doesn't race against ongoing writes from the BLE
/// pipeline — the source DB has data-protection enabled and the OS can't
/// always read it through the share path otherwise.
/// </summary>
public sealed class UIDatabaseExporter : IDatabaseExporter
{
	public Task ExportAsync(string databasePath)
	{
		var tcs = new TaskCompletionSource();

		// Snapshot the DB to a temp file so the share sheet hands out a
		// stable byte stream even if BLE writes keep landing. Best-effort:
		// if copy fails (file locked, disk full), surface the share with
		// whatever the OS can see and let the user retry.
		string tempPath = Path.Combine(
			Path.GetTempPath(),
			$"meltdownmonitor-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.db");
		try
		{
			File.Copy(databasePath, tempPath, overwrite: true);
		}
		catch
		{
			tempPath = databasePath;
		}

		var url = NSUrl.FromFilename(tempPath);
		var activityController = new UIActivityViewController(
			new NSObject[] { url },
			applicationActivities: null);

		activityController.CompletionWithItemsHandler = (_, _, _, _) => tcs.TrySetResult();

		// On iPad, UIActivityViewController must declare a popover anchor;
		// pin it to the centre of the root view as a safe default since
		// our settings VM doesn't expose the originating button rect.
		var keyWindow = UIApplication.SharedApplication
			.ConnectedScenes
			.OfType<UIWindowScene>()
			.SelectMany(s => s.Windows)
			.FirstOrDefault(w => w.IsKeyWindow);

		var presenter = keyWindow?.RootViewController;
		if (presenter is null)
		{
			tcs.TrySetResult();
			return tcs.Task;
		}

		if (activityController.PopoverPresentationController is { } popover)
		{
			popover.SourceView = presenter.View;
			popover.SourceRect = new CoreGraphics.CGRect(
				presenter.View!.Bounds.GetMidX(),
				presenter.View.Bounds.GetMidY(),
				0,
				0);
			popover.PermittedArrowDirections = 0;
		}

		presenter.PresentViewController(activityController, animated: true, completionHandler: null);

		return tcs.Task;
	}
}
