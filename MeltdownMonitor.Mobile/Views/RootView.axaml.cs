using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Markup.Xaml;

namespace MeltdownMonitor.Mobile.Views;

public partial class RootView : UserControl
{
	private IInsetsManager? _insets;

	public RootView() => AvaloniaXamlLoader.Load(this);

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);

		// Draw under the system bars and inset the content ourselves, so the dark app
		// background fills the whole screen while the tab bar and top content stay clear
		// of the notch / Dynamic Island / home indicator. Desktop has no InsetsManager,
		// so this is a no-op there (the window already excludes the chrome).
		_insets = TopLevel.GetTopLevel(this)?.InsetsManager;
		if (_insets is not null)
		{
			_insets.DisplayEdgeToEdgePreference = true;
			_insets.SafeAreaChanged += OnSafeAreaChanged;
			ApplySafeArea(_insets.SafeAreaPadding);
		}
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		if (_insets is not null)
		{
			_insets.SafeAreaChanged -= OnSafeAreaChanged;
			_insets = null;
		}

		base.OnDetachedFromVisualTree(e);
	}

	private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e) =>
		ApplySafeArea(e.SafeAreaPadding);

	// Margin (not Padding) so it works without depending on the UserControl template
	// honouring Padding. The disclaimer overlay and the tab host both live inside
	// RootLayout, so both respect the safe area.
	private void ApplySafeArea(Thickness padding) => RootLayout.Margin = padding;
}
