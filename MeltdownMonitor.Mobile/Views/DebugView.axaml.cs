using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MeltdownMonitor.Mobile.Views;

public partial class DebugView : UserControl
{
	public DebugView()
	{
		AvaloniaXamlLoader.Load(this);
	}
}
