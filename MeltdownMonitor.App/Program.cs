using ktsu.AppDataStorage;
using ktsu.SingleAppInstance;
using MeltdownMonitor.App;
using MeltdownMonitor.Core.Persistence;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]

// Prevent multiple instances — exactly one tray icon per user.
if (!SingleAppInstance.ShouldLaunch())
{
	return;
}

var settings = AppSettings.LoadOrCreate();

Directory.CreateDirectory(Path.GetDirectoryName(settings.DatabasePath)!);
using var repository = new MeltdownRepository(settings.DatabasePath);
using var pipeline = new Pipeline(settings, repository);
var dispatcher = new AlertDispatcher(settings);

pipeline.AlertFired += dispatcher.Dispatch;

// Status window is created on demand. The render loop runs on its own thread, so
// access to the reference is guarded and the window clears it when it closes.
StatusWindow? statusWindow = null;
object statusWindowLock = new();

void ShowStatusWindow()
{
	lock (statusWindowLock)
	{
		// Already open — the window owns its own close button.
		if (statusWindow is not null)
		{
			return;
		}

		statusWindow = new StatusWindow(pipeline, repository, settings, onClosed: OnStatusWindowClosed);
		statusWindow.Run();
	}
}

// Fired on the status-window thread when its render loop exits. Drop the reference
// so the next request reopens it. The loop has already ended, so we must not Join.
void OnStatusWindowClosed()
{
	lock (statusWindowLock)
	{
		statusWindow = null;
	}
}

pipeline.Start();

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

using var tray = new TrayIcon(pipeline, repository, settings, ShowStatusWindow, Application.Exit);

Application.Run();

StatusWindow? remaining;
lock (statusWindowLock)
{
	remaining = statusWindow;
	statusWindow = null;
}

remaining?.Dispose();
pipeline.Stop();
