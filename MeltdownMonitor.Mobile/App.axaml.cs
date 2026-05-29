using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MeltdownMonitor.Mobile.ViewModels;
using MeltdownMonitor.Mobile.Views;

namespace MeltdownMonitor.Mobile;

public partial class App : Application
{
	/// <summary>
	/// Factory used to build the root view model. The iOS head replaces
	/// this before <c>OnFrameworkInitializationCompleted</c> runs so it
	/// can inject the BLE pipeline, repository, and permission ask
	/// delegates. Default builds a stubbed VM suitable for previewing
	/// the layout without a connected sensor.
	/// </summary>
	public static Func<RootViewModel> RootViewModelFactory { get; set; } =
		RootViewModel.CreateDefault;

	/// <summary>
	/// Invoked once the root view is in place, on the UI thread, after
	/// framework initialization completes. The iOS head uses this to kick off
	/// the live BLE pipeline composition (design doc §6.1) — it can't run
	/// before Avalonia is up because the view models it feeds are built by
	/// <see cref="RootViewModelFactory"/>. No-op by default.
	/// </summary>
	public static Action? Started { get; set; }

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		var root = new RootView
		{
			DataContext = RootViewModelFactory(),
		};

		if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
		{
			singleView.MainView = root;
		}
		else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.MainWindow = new Avalonia.Controls.Window
			{
				Content = root,
				Title = "Meltdown Monitor",
				Width = 420,
				Height = 780,
			};
		}

		base.OnFrameworkInitializationCompleted();

		Started?.Invoke();
	}
}
