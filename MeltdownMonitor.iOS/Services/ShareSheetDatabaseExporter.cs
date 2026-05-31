using CoreFoundation;
using Foundation;
using MeltdownMonitor.Mobile.Services;
using UIKit;

namespace MeltdownMonitor.iOS.Services;

/// <summary>
/// Exports the SQLite database through the iOS share sheet
/// (<c>UIActivityViewController</c>) — the sandbox isn't user-browsable, so
/// this is how the user gets their data out (design doc §6.5 / §10). A copy
/// is handed out rather than the live file so an in-flight write can't be
/// observed half-committed.
/// </summary>
public sealed class ShareSheetDatabaseExporter : IDatabaseExporter
{
	public Task ExportAsync(string databasePath)
	{
		var tcs = new TaskCompletionSource<bool>();

		DispatchQueue.MainQueue.DispatchAsync(() =>
		{
			try
			{
				string copyPath = CopyToTemp(databasePath);
				var url = NSUrl.FromFilename(copyPath);
				var activity = new UIActivityViewController(new NSObject[] { url }, null);

				var controller = TopViewController();
				if (controller is null)
				{
					tcs.TrySetResult(false);
					return;
				}

				// iPad requires a popover anchor; anchor to the presenting view.
				if (activity.PopoverPresentationController is { } popover && controller.View is { } view)
				{
					popover.SourceView = view;
					popover.SourceRect = new CoreGraphics.CGRect(
						view.Bounds.GetMidX(),
						view.Bounds.GetMidY(),
						0,
						0);
					popover.PermittedArrowDirections = 0;
				}

				activity.CompletionWithItemsHandler = (_, _, _, _) => tcs.TrySetResult(true);
				controller.PresentViewController(activity, animated: true, completionHandler: null);
			}
			catch (Exception)
			{
				tcs.TrySetResult(false);
			}
		});

		return tcs.Task;
	}

	private static string CopyToTemp(string databasePath)
	{
		string dest = Path.Combine(Path.GetTempPath(), "MeltdownMonitor-export.db");
		File.Copy(databasePath, dest, overwrite: true);
		return dest;
	}

	private static UIViewController? TopViewController()
	{
		var window = UIApplication.SharedApplication.ConnectedScenes
			.OfType<UIWindowScene>()
			.SelectMany(scene => scene.Windows)
			.FirstOrDefault(w => w.IsKeyWindow);

		var controller = window?.RootViewController;
		while (controller?.PresentedViewController is { } presented)
		{
			controller = presented;
		}

		return controller;
	}
}
