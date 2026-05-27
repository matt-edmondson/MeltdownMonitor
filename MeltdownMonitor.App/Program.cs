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

// Status window is created on demand and torn down when hidden.
StatusWindow? statusWindow = null;

void ToggleStatusWindow()
{
	if (statusWindow is null)
	{
		statusWindow = new StatusWindow(pipeline, repository, settings);
		statusWindow.Run();
	}
	else
	{
		statusWindow.Close();
		statusWindow.Dispose();
		statusWindow = null;
	}
}

pipeline.Start();

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

using var tray = new TrayIcon(pipeline, repository, settings, ToggleStatusWindow, Application.Exit);

Application.Run();

pipeline.Stop();
