using System.Reflection;

using ktsu.AppDataStorage;
using ktsu.SingleAppInstance;
using MeltdownMonitor.App;
using MeltdownMonitor.Core.Diagnostics;
using MeltdownMonitor.Core.Persistence;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]

// Prevent multiple instances — exactly one tray icon per user.
if (!SingleAppInstance.ShouldLaunch())
{
	return;
}

var settings = AppSettings.LoadOrCreate();

// Start crash reporting as early as possible so failures during startup are
// captured. No-op (and no network) unless a DSN is configured in settings or
// the MELTDOWN_CRASH_REPORTING_DSN environment variable.
using var crashReporting = CrashReporting.Initialize(new CrashReportingOptions
{
	Dsn = settings.CrashReportingDsn,
	Environment = "windows-desktop",
	Release = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
});

Directory.CreateDirectory(Path.GetDirectoryName(settings.DatabasePath)!);
using var repository = new MeltdownRepository(settings.DatabasePath);
using var pipeline = new Pipeline(settings, repository);
var dispatcher = new AlertDispatcher(settings);

pipeline.AlertFired += dispatcher.Dispatch;

// The status window runs hidden for the application's lifetime — its render loop can't
// be restarted once stopped, so we start it once and toggle visibility from the tray.
using var statusWindow = new StatusWindow(pipeline, repository, settings);
statusWindow.Run();

pipeline.Start();

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

using var tray = new TrayIcon(
	pipeline,
	repository,
	settings,
	statusWindow.ToggleVisibility,
	statusWindow.ToggleOverlay,
	statusWindow.ToggleOverlayClickThrough,
	Application.Exit);

Application.Run();

pipeline.Stop();
