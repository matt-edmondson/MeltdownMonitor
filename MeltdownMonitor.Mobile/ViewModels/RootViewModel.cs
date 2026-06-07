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
		EcgViewModel ecg,
		IMobileSettingsStore? store = null,
		HealthPromptViewModel? healthPrompt = null)
	{
		_settings = settings;
		_store = store;
		Now = now;
		History = history;
		Settings = settings_tab;
		Metrics = metrics;
		Ecg = ecg;
		// A null prompt (design-time / desktop hosts) collapses to a never-shown banner.
		HealthPrompt = healthPrompt ?? new HealthPromptViewModel(settings, isAvailable: () => false);
		Disclaimer = new DisclaimerViewModel(AcceptDisclaimer);
	}

	public NowViewModel Now { get; }
	public HistoryViewModel History { get; }
	public SettingsViewModel Settings { get; }
	public MetricsViewModel Metrics { get; }
	public EcgViewModel Ecg { get; }
	public DisclaimerViewModel Disclaimer { get; }

	/// <summary>The one-shot, dismissible "record to Apple Health / Health Connect" prompt.</summary>
	public HealthPromptViewModel HealthPrompt { get; }

	/// <summary>
	/// Raised once, on the UI thread, when the user accepts the first-run
	/// disclaimer. A head can use this to sequence its runtime permission asks
	/// behind the acknowledgement, matching the iOS "acknowledge, then ask"
	/// ordering (design doc §5.2). Not raised again on subsequent launches —
	/// the disclaimer is already accepted then, so a head asks on launch instead.
	/// </summary>
	public event Action? DisclaimerAccepted;

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
			new MetricsViewModel(),
			new EcgViewModel());
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
		DisclaimerAccepted?.Invoke();
	}
}
