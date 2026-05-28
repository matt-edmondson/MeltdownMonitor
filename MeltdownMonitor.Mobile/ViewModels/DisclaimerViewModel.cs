using System.Windows.Input;

namespace MeltdownMonitor.Mobile.ViewModels;

/// <summary>
/// First-run disclaimer screen (design doc §4.4). Blocks the rest of the
/// app — including the HealthKit ask — until the user explicitly
/// acknowledges that MeltdownMonitor is informational, not medical.
/// </summary>
public sealed class DisclaimerViewModel : ViewModelBase
{
	public DisclaimerViewModel(Action onAccept)
	{
		AcceptCommand = new RelayCommand(onAccept);
	}

	public string Title { get; } = "Before we begin";

	public string BodyText { get; } =
		"MeltdownMonitor is an informational wellness tool. It is not a medical "
		+ "device and does not diagnose, treat, or manage any condition. "
		+ "It estimates short-window heart-rate variability from a Polar chest "
		+ "strap and tells you when your own baseline shifts — the rest is up "
		+ "to you. If something feels wrong physically or mentally, talk to a "
		+ "qualified clinician, not an app.";

	public string PrivacyText { get; } =
		"Your data stays on this device. The app reads recent heart-rate "
		+ "samples from Apple Health only after you grant permission, and only "
		+ "to warm up your personal baseline so you don't have to wait for "
		+ "calibration on day one. Nothing is sent anywhere.";

	public string AcceptLabel { get; } = "I understand — continue";

	public ICommand AcceptCommand { get; }
}
