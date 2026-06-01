using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.Mobile.Views;

public partial class NowView : UserControl
{
	// Refreshes the "time in current state" label so it counts up while the state
	// holds — the value otherwise only changes on a state transition.
	private readonly DispatcherTimer _stateClock;

	public NowView()
	{
		AvaloniaXamlLoader.Load(this);
		_stateClock = new DispatcherTimer(
			TimeSpan.FromSeconds(1),
			DispatcherPriority.Background,
			(_, _) => (DataContext as NowViewModel)?.TickTimeDisplay());
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		_stateClock.Start();
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		_stateClock.Stop();
	}
}
