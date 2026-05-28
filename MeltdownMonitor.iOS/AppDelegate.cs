using Avalonia;
using Avalonia.iOS;
using Foundation;

namespace MeltdownMonitor.iOS;

[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<MeltdownMonitor.Mobile.App>
{
	protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
		base.CustomizeAppBuilder(builder);
}
