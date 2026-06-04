using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MeltdownMonitor.Mobile.Views;

public partial class MetricsView : UserControl
{
	public MetricsView()
	{
		AvaloniaXamlLoader.Load(this);
	}
}
