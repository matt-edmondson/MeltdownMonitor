using System.Windows.Input;

namespace MeltdownMonitor.Mobile.ViewModels;

/// <summary>
/// Backs the one-shot, dismissible banner that invites the user to record their
/// data to Apple Health / Health Connect. Deliberately unintrusive: it appears
/// only when the integration is available, the disclaimer is accepted, recording
/// is not already on, and the user hasn't already answered it — and it never
/// blocks the UI. Answering it (enable or dismiss) sets
/// <see cref="MobileSettings.HealthPromptDismissed"/> so it doesn't return.
///
/// <para>
/// "Enable" routes through a head-supplied authorization delegate (HealthKit /
/// Health Connect permission UI). On grant it turns on both the continuous
/// recording (<see cref="MobileSettings.RecordToHealth"/>) and episode write-back
/// (<see cref="MobileSettings.WriteEpisodesToHealthKit"/>); a denial just dismisses
/// the banner — the user can still enable it later from Settings.
/// </para>
/// </summary>
public sealed class HealthPromptViewModel : ViewModelBase
{
	private readonly MobileSettings _settings;
	private readonly Func<Task<bool>>? _requestAuthorization;
	private readonly Func<bool>? _isAvailable;
	private readonly Action? _onChanged;

	public HealthPromptViewModel(
		MobileSettings settings,
		Func<Task<bool>>? requestAuthorization = null,
		Func<bool>? isAvailable = null,
		Action? onChanged = null)
	{
		_settings = settings;
		_requestAuthorization = requestAuthorization;
		_isAvailable = isAvailable;
		_onChanged = onChanged;

		EnableCommand = new RelayCommand(() => _ = EnableAsync());
		DismissCommand = new RelayCommand(Dismiss);
	}

	public string Title => "Record to Apple Health?";

	public string Body =>
		"Save your heart rate, HRV, and episodes so you own the data and can share it with a clinician. You can turn this off anytime in Settings.";

	/// <summary>
	/// Whether the banner should be shown. True only when a health store is available,
	/// recording isn't already on, and the user hasn't already answered the prompt.
	/// </summary>
	public bool IsVisible =>
		!_settings.RecordToHealth
		&& !_settings.HealthPromptDismissed
		&& (_isAvailable?.Invoke() ?? true);

	public ICommand EnableCommand { get; }
	public ICommand DismissCommand { get; }

	private async Task EnableAsync()
	{
		bool granted = _requestAuthorization is not null
			&& await _requestAuthorization().ConfigureAwait(true);

		if (granted)
		{
			_settings.RecordToHealth = true;
			_settings.WriteEpisodesToHealthKit = true;
		}

		// Either way the user has answered: don't nag again.
		_settings.HealthPromptDismissed = true;
		_onChanged?.Invoke();
		Raise(nameof(IsVisible));
	}

	private void Dismiss()
	{
		_settings.HealthPromptDismissed = true;
		_onChanged?.Invoke();
		Raise(nameof(IsVisible));
	}
}
