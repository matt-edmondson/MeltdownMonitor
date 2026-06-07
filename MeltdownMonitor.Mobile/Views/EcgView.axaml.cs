using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MeltdownMonitor.Mobile.Views;

public partial class EcgView : UserControl
{
	public EcgView()
	{
		AvaloniaXamlLoader.Load(this);
	}
}
