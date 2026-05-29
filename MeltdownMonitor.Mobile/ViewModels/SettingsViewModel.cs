using System.Windows.Input;
using MeltdownMonitor.Core.Beats;

namespace MeltdownMonitor.Mobile.ViewModels;

/// <summary>
/// Settings tab — bound to <see cref="MobileSettings"/>. Permissions
/// (HealthKit, notifications) ask through delegates so the iOS head can
/// route them at <c>UNUserNotificationCenter</c> / <c>HKHealthStore</c>
/// without this assembly taking a CoreBluetooth dependency.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
	private readonly MobileSettings _settings;
	private readonly Func<Task<bool>>? _requestNotifications;
	private readonly Func<Task<bool>>? _requestHealthKit;
	private readonly Func<Task>? _exportDatabase;
	private readonly Action? _onChanged;

	public SettingsViewModel(
		MobileSettings settings,
		Func<Task<bool>>? requestNotifications = null,
		Func<Task<bool>>? requestHealthKit = null,
		Func<Task>? exportDatabase = null,
		Action? onChanged = null)
	{
		_settings = settings;
		_requestNotifications = requestNotifications;
		_requestHealthKit = requestHealthKit;
		_exportDatabase = exportDatabase;
		_onChanged = onChanged;

		PauseOneHourCommand = new RelayCommand(PauseOneHour);
		ResumeCommand = new RelayCommand(Resume, () => _settings.PausedUntil is not null);
		RequestNotificationsCommand = new RelayCommand(() => _ = RequestNotificationsAsync());
		RequestHealthKitCommand = new RelayCommand(() => _ = RequestHealthKitAsync());
		ExportDatabaseCommand = new RelayCommand(
			() => _ = ExportDatabaseAsync(),
			() => _exportDatabase is not null);
	}

	/// <summary>
	/// Invoked after any setting mutates so the platform head can persist the
	/// full <see cref="MobileSettings"/> blob (design doc §6.4).
	/// </summary>
	private void Persist() => _onChanged?.Invoke();

	public IReadOnlyList<PolarDeviceType> DeviceTypes { get; } =
		Enum.GetValues<PolarDeviceType>();

	public PolarDeviceType DeviceType
	{
		get => _settings.DeviceType;
		set
		{
			if (_settings.DeviceType != value)
			{
				_settings.DeviceType = value;
				Raise();
				Persist();
			}
		}
	}

	public bool EnableChime
	{
		get => _settings.EnableChime;
		set
		{
			if (_settings.EnableChime != value)
			{
				_settings.EnableChime = value;
				Raise();
				Persist();
			}
		}
	}

	public bool EnableNotifications
	{
		get => _settings.EnableNotifications;
		set
		{
			if (_settings.EnableNotifications != value)
			{
				_settings.EnableNotifications = value;
				Raise();
				Persist();
			}
		}
	}

	public bool WriteEpisodesToHealthKit
	{
		get => _settings.WriteEpisodesToHealthKit;
		set
		{
			if (_settings.WriteEpisodesToHealthKit != value)
			{
				_settings.WriteEpisodesToHealthKit = value;
				Raise();
				Persist();
			}
		}
	}

	public string AlertSuggestion
	{
		get => _settings.AlertSuggestion;
		set
		{
			if (_settings.AlertSuggestion != value)
			{
				_settings.AlertSuggestion = value;
				Raise();
				Persist();
			}
		}
	}

	public double RmssdWarningDropPercent
	{
		get => _settings.Thresholds.RmssdWarningDropFraction * 100;
		set
		{
			double frac = Math.Clamp(value, 5, 90) / 100;
			if (Math.Abs(_settings.Thresholds.RmssdWarningDropFraction - frac) > 1e-6)
			{
				_settings.Thresholds = _settings.Thresholds with { RmssdWarningDropFraction = frac };
				Raise();
				Persist();
			}
		}
	}

	public double HrWarningRisePercent
	{
		get => _settings.Thresholds.HrWarningRiseFraction * 100;
		set
		{
			double frac = Math.Clamp(value, 1, 90) / 100;
			if (Math.Abs(_settings.Thresholds.HrWarningRiseFraction - frac) > 1e-6)
			{
				_settings.Thresholds = _settings.Thresholds with { HrWarningRiseFraction = frac };
				Raise();
				Persist();
			}
		}
	}

	public string PausedUntilLabel =>
		_settings.PausedUntil is null
			? "Not paused"
			: $"Paused until {_settings.PausedUntil.Value.ToLocalTime():HH:mm}";

	public ICommand PauseOneHourCommand { get; }
	public ICommand ResumeCommand { get; }
	public ICommand RequestNotificationsCommand { get; }
	public ICommand RequestHealthKitCommand { get; }
	public ICommand ExportDatabaseCommand { get; }

	private void PauseOneHour()
	{
		_settings.PausedUntil = DateTimeOffset.UtcNow.AddHours(1);
		Raise(nameof(PausedUntilLabel));
		(ResumeCommand as RelayCommand)?.RaiseCanExecuteChanged();
		Persist();
	}

	private void Resume()
	{
		_settings.PausedUntil = null;
		Raise(nameof(PausedUntilLabel));
		(ResumeCommand as RelayCommand)?.RaiseCanExecuteChanged();
		Persist();
	}

	private async Task ExportDatabaseAsync()
	{
		if (_exportDatabase is null)
		{
			return;
		}

		await _exportDatabase().ConfigureAwait(true);
	}

	private async Task RequestNotificationsAsync()
	{
		if (_requestNotifications is null) return;
		bool granted = await _requestNotifications().ConfigureAwait(true);
		EnableNotifications = granted;
	}

	private async Task RequestHealthKitAsync()
	{
		if (_requestHealthKit is null) return;
		_ = await _requestHealthKit().ConfigureAwait(true);
	}
}
