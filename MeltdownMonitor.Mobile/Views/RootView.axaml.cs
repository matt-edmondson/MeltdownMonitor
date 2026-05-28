using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MeltdownMonitor.Mobile.Views;

public partial class RootView : UserControl
{
	public RootView() => AvaloniaXamlLoader.Load(this);
}
