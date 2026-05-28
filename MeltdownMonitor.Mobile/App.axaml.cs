using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MeltdownMonitor.Mobile.Views;

namespace MeltdownMonitor.Mobile;

public partial class App : Application
{
	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
		{
			singleView.MainView = new NowView();
		}

		base.OnFrameworkInitializationCompleted();
	}
}
