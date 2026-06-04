using MeltdownMonitor.Core.Diagnostics;

using UIKit;

namespace MeltdownMonitor.iOS;

public static class Program
{
	public static void Main(string[] args)
	{
		// Bring crash reporting up first, before UIKit/Avalonia and the audio
		// session in CustomizeAppBuilder. Launch crashes (Avalonia bootstrap,
		// the composition root, trimming/AOT faults) were previously lost because
		// the SDK only initialized once Avalonia called the root-view-model
		// factory in OnFrameworkInitializationCompleted — well past the window
		// where most launch crashes happen. No-op unless a DSN is configured.
		IosCompositionRoot.InitializeCrashReporting();

		try
		{
			UIApplication.Main(args, null, typeof(AppDelegate));
		}
		catch (Exception ex)
		{
			// A managed exception escaping the run loop is fatal — capture it and
			// flush synchronously so the report survives the imminent process exit
			// (the async unhandled-exception hook may not get to flush in time).
			CrashReporting.CaptureException(ex);
			CrashReporting.Flush(TimeSpan.FromSeconds(5));
			throw;
		}
	}
}
