using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.Mobile.ViewModels;

/// <summary>
/// Top-level VM. Owns the three tab view models so the iOS head (or any
/// other Avalonia host) can construct a single composition root and hand
/// it to <c>RootView</c>. Also gates the tabs behind the first-run
/// disclaimer (design doc §4.4).
/// </summary>
public sealed class RootViewModel : ViewModelBase
{
	private readonly MobileSettings _settings;
	private readonly IMobileSettingsStore? _store;

	public RootViewModel(
		MobileSettings settings,
		NowViewModel now,
		HistoryViewModel history,
		SettingsViewModel settings_tab,
		MetricsViewModel metrics,
		IMobileSettingsStore? store = null)
	{
		_settings = settings;
		_store = store;
		Now = now;
		History = history;
		Settings = settings_tab;
		Metrics = metrics;
		Disclaimer = new DisclaimerViewModel(AcceptDisclaimer);
	}

	public NowViewModel Now { get; }
	public HistoryViewModel History { get; }
	public SettingsViewModel Settings { get; }
	public MetricsViewModel Metrics { get; }
	public DisclaimerViewModel Disclaimer { get; }

	public bool IsDisclaimerAccepted => _settings.IsDisclaimerAccepted;

	public bool IsDisclaimerPending => !_settings.IsDisclaimerAccepted;

	/// <summary>
	/// Stub composition for design-time and screenshots — no repository,
	/// no pipeline. The iOS head replaces this in
	/// <c>OnFrameworkInitializationCompleted</c>.
	/// </summary>
	public static RootViewModel CreateDefault()
	{
		var settings = new MobileSettings();
		return new RootViewModel(
			settings,
			new NowViewModel(),
			new HistoryViewModel(),
			new SettingsViewModel(settings),
			new MetricsViewModel());
	}

	private void AcceptDisclaimer()
	{
		if (_settings.IsDisclaimerAccepted)
		{
			return;
		}

		_settings.IsDisclaimerAccepted = true;
		_store?.Save(_settings);
		Raise(nameof(IsDisclaimerAccepted));
		Raise(nameof(IsDisclaimerPending));
	}
}
