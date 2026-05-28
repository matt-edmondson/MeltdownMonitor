namespace MeltdownMonitor.Mobile.ViewModels;

/// <summary>
/// Top-level VM. Owns the three tab view models so the iOS head (or any
/// other Avalonia host) can construct a single composition root and hand
/// it to <c>RootView</c>.
/// </summary>
public sealed class RootViewModel
{
	public RootViewModel(
		NowViewModel now,
		HistoryViewModel history,
		SettingsViewModel settings)
	{
		Now = now;
		History = history;
		Settings = settings;
	}

	public NowViewModel Now { get; }
	public HistoryViewModel History { get; }
	public SettingsViewModel Settings { get; }

	/// <summary>
	/// Stub composition for design-time and screenshots — no repository,
	/// no pipeline. The iOS head replaces this in
	/// <c>OnFrameworkInitializationCompleted</c>.
	/// </summary>
	public static RootViewModel CreateDefault()
	{
		var settings = new MobileSettings();
		return new RootViewModel(
			new NowViewModel(),
			new HistoryViewModel(),
			new SettingsViewModel(settings));
	}
}
