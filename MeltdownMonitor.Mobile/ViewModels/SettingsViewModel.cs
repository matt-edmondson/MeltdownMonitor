using System.Windows.Input;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Regulation;

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
	private readonly Func<Task>? _revokeHealthAccess;
	private readonly Func<Task>? _exportDatabase;
	private readonly Func<Task>? _clearData;
	private readonly Action? _onChanged;
	private bool _isClearDataConfirmPending;
	private string? _clearDataStatus;

	public SettingsViewModel(
		MobileSettings settings,
		Func<Task<bool>>? requestNotifications = null,
		Func<Task<bool>>? requestHealthKit = null,
		Func<Task>? exportDatabase = null,
		Action? onChanged = null,
		Func<Task>? revokeHealthAccess = null,
		Func<Task>? clearData = null)
	{
		_settings = settings;
		_requestNotifications = requestNotifications;
		_requestHealthKit = requestHealthKit;
		_revokeHealthAccess = revokeHealthAccess;
		_exportDatabase = exportDatabase;
		_clearData = clearData;
		_onChanged = onChanged;

		PauseOneHourCommand = new RelayCommand(PauseOneHour);
		ResumeCommand = new RelayCommand(Resume, () => _settings.PausedUntil is not null);
		RequestNotificationsCommand = new RelayCommand(() => _ = RequestNotificationsAsync());
		RequestHealthKitCommand = new RelayCommand(() => _ = RequestHealthKitAsync());
		RevokeHealthCommand = new RelayCommand(() => _ = RevokeHealthAsync());
		ExportDatabaseCommand = new RelayCommand(
			() => _ = ExportDatabaseAsync(),
			() => _exportDatabase is not null);

		// Clearing data is destructive and irreversible, so it's a two-step confirm: the first command
		// arms the confirmation panel, the second actually wipes.
		ClearDataCommand = new RelayCommand(BeginClearData, () => _clearData is not null);
		ConfirmClearDataCommand = new RelayCommand(() => _ = ConfirmClearDataAsync());
		CancelClearDataCommand = new RelayCommand(CancelClearData);
	}

	/// <summary>
	/// Invoked after any setting mutates so the platform head can persist the
	/// full <see cref="MobileSettings"/> blob (design doc §6.4).
	/// </summary>
	private void Persist() => _onChanged?.Invoke();

	public IReadOnlyList<HeartRateDeviceType> DeviceTypes { get; } =
		Enum.GetValues<HeartRateDeviceType>();

	public HeartRateDeviceType DeviceType
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

	public IReadOnlyList<IntervalSource> IntervalSources { get; } = Enum.GetValues<IntervalSource>();

	/// <summary>Which stream supplies beat-to-beat intervals (HRS RR, Polar PPI, or Polar ECG).
	/// Applies on the next monitoring start.</summary>
	public IntervalSource PreferredIntervalSource
	{
		get => _settings.PreferredIntervalSource;
		set
		{
			if (_settings.PreferredIntervalSource != value)
			{
				_settings.PreferredIntervalSource = value;
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

	/// <summary>
	/// Use device/strap motion to corroborate detection: defers alerts and freezes the baseline during
	/// exertion so exercise isn't mistaken for dysregulation. Streams the Polar strap accelerometer when
	/// available, otherwise the phone IMU. Takes effect on the next monitoring restart.
	/// </summary>
	public bool EnableMotionCorroboration
	{
		get => _settings.EnableMotionCorroboration;
		set
		{
			if (_settings.EnableMotionCorroboration != value)
			{
				_settings.EnableMotionCorroboration = value;
				Raise();
				Persist();
			}
		}
	}

	/// <summary>Raised when <see cref="EnableDebugMode"/> toggles, so the shell can show or hide the Debug tab.</summary>
	public event Action? DebugModeChanged;

	/// <summary>
	/// Show the Debug tab with live PMD diagnostics — the ECG-vs-HRS RR A/B, per-stream artifact rates,
	/// the full HRV/baseline dump, ECG signal stats, and connection details. Diagnostic only; it changes
	/// no detection behaviour.
	/// </summary>
	public bool EnableDebugMode
	{
		get => _settings.EnableDebugMode;
		set
		{
			if (_settings.EnableDebugMode != value)
			{
				_settings.EnableDebugMode = value;
				Raise();
				Persist();
				DebugModeChanged?.Invoke();
			}
		}
	}

	public bool EnableLiveActivity
	{
		get => _settings.EnableLiveActivity;
		set
		{
			if (_settings.EnableLiveActivity != value)
			{
				_settings.EnableLiveActivity = value;
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

	/// <summary>
	/// Master switch for the continuous health-store integration (read for warm-start +
	/// write HR/HRV/RR). Turning it off is the in-app half of revoking access: all
	/// reading and writing stops immediately. <see cref="RevokeHealthCommand"/> additionally
	/// drives the platform-level revoke / Health settings deep link.
	/// </summary>
	public bool RecordToHealth
	{
		get => _settings.RecordToHealth;
		set
		{
			if (_settings.RecordToHealth != value)
			{
				_settings.RecordToHealth = value;
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

	public int RegulationTrailLength
	{
		get => _settings.RegulationTrailLength;
		set
		{
			int clamped = Math.Clamp(value, 12, 2160);
			if (_settings.RegulationTrailLength != clamped)
			{
				_settings.RegulationTrailLength = clamped;
				Raise();
				Persist();
			}
		}
	}

	public double JitterExaggeration
	{
		get => _settings.JitterExaggeration;
		set
		{
			double clamped = Math.Clamp(value, 0.0, 3.0);
			if (Math.Abs(_settings.JitterExaggeration - clamped) > 1e-6)
			{
				_settings.JitterExaggeration = clamped;
				Raise();
				Persist();
			}
		}
	}

	public double LobeThickness
	{
		get => _settings.LobeThickness;
		set
		{
			double clamped = Math.Clamp(value, 0.5, 3.0);
			if (Math.Abs(_settings.LobeThickness - clamped) > 1e-6)
			{
				_settings.LobeThickness = clamped;
				Raise();
				Persist();
			}
		}
	}

	public int LobeSegments
	{
		get => _settings.LobeSegments;
		set
		{
			int clamped = Math.Clamp(value, LemniscateGeometry.MinSegments, LemniscateGeometry.MaxSegments);
			if (_settings.LobeSegments != clamped)
			{
				_settings.LobeSegments = clamped;
				Raise();
				Persist();
			}
		}
	}

	public int FieldIndexBuckets
	{
		get => _settings.FieldIndexBuckets;
		set
		{
			int clamped = Math.Clamp(value, 6, 64);
			if (_settings.FieldIndexBuckets != clamped)
			{
				_settings.FieldIndexBuckets = clamped;
				Raise();
				Persist();
			}
		}
	}

	public int FieldVagalBuckets
	{
		get => _settings.FieldVagalBuckets;
		set
		{
			int clamped = Math.Clamp(value, 6, 64);
			if (_settings.FieldVagalBuckets != clamped)
			{
				_settings.FieldVagalBuckets = clamped;
				Raise();
				Persist();
			}
		}
	}

	public double RecoveryArrowSpeed
	{
		get => _settings.RecoveryArrowSpeed;
		set
		{
			double clamped = Math.Clamp(value, 0.1, 3.0);
			if (Math.Abs(_settings.RecoveryArrowSpeed - clamped) > 1e-6)
			{
				_settings.RecoveryArrowSpeed = clamped;
				Raise();
				Persist();
			}
		}
	}

	public int RecoveryArrowCount
	{
		get => _settings.RecoveryArrowCount;
		set
		{
			int clamped = Math.Clamp(value, 1, 6);
			if (_settings.RecoveryArrowCount != clamped)
			{
				_settings.RecoveryArrowCount = clamped;
				Raise();
				Persist();
			}
		}
	}

	public double RmssdAlertingDropPercent
	{
		get => _settings.Thresholds.RmssdAlertingDropFraction * 100;
		set
		{
			double frac = Math.Clamp(value, 5, 95) / 100;
			if (Math.Abs(_settings.Thresholds.RmssdAlertingDropFraction - frac) > 1e-6)
			{
				_settings.Thresholds = _settings.Thresholds with { RmssdAlertingDropFraction = frac };
				Raise();
				Persist();
			}
		}
	}

	public double WarningHoldSeconds
	{
		get => _settings.Thresholds.WarningHoldDuration.TotalSeconds;
		set
		{
			var span = TimeSpan.FromSeconds(Math.Clamp(value, 5, 300));
			if (_settings.Thresholds.WarningHoldDuration != span)
			{
				_settings.Thresholds = _settings.Thresholds with { WarningHoldDuration = span };
				Raise();
				Persist();
			}
		}
	}

	public double AlertingEscalationSeconds
	{
		get => _settings.Thresholds.AlertingEscalationDuration.TotalSeconds;
		set
		{
			var span = TimeSpan.FromSeconds(Math.Clamp(value, 10, 600));
			if (_settings.Thresholds.AlertingEscalationDuration != span)
			{
				_settings.Thresholds = _settings.Thresholds with { AlertingEscalationDuration = span };
				Raise();
				Persist();
			}
		}
	}

	public double CooldownMinutes
	{
		get => _settings.Thresholds.CooldownDuration.TotalMinutes;
		set
		{
			var span = TimeSpan.FromMinutes(Math.Clamp(value, 1, 60));
			if (_settings.Thresholds.CooldownDuration != span)
			{
				_settings.Thresholds = _settings.Thresholds with { CooldownDuration = span };
				Raise();
				Persist();
			}
		}
	}

	public double LobeOpacityPercent
	{
		get => _settings.LobeOpacity * 100;
		set
		{
			double frac = Math.Clamp(value, 0, 100) / 100;
			if (Math.Abs(_settings.LobeOpacity - frac) > 1e-6)
			{
				_settings.LobeOpacity = frac;
				Raise();
				Persist();
			}
		}
	}

	public double TrailOpacityPercent
	{
		get => _settings.TrailOpacity * 100;
		set
		{
			double frac = Math.Clamp(value, 0, 100) / 100;
			if (Math.Abs(_settings.TrailOpacity - frac) > 1e-6)
			{
				_settings.TrailOpacity = frac;
				Raise();
				Persist();
			}
		}
	}

	public double HistogramOpacityPercent
	{
		get => _settings.HistogramOpacity * 100;
		set
		{
			double frac = Math.Clamp(value, 0, 100) / 100;
			if (Math.Abs(_settings.HistogramOpacity - frac) > 1e-6)
			{
				_settings.HistogramOpacity = frac;
				Raise();
				Persist();
			}
		}
	}

	public double HeatmapOpacityPercent
	{
		get => _settings.HeatmapOpacity * 100;
		set
		{
			double frac = Math.Clamp(value, 0, 100) / 100;
			if (Math.Abs(_settings.HeatmapOpacity - frac) > 1e-6)
			{
				_settings.HeatmapOpacity = frac;
				Raise();
				Persist();
			}
		}
	}

	public double HeatmapPeakOpacityPercent
	{
		get => _settings.HeatmapPeakOpacity * 100;
		set
		{
			double frac = Math.Clamp(value, 0, 100) / 100;
			if (Math.Abs(_settings.HeatmapPeakOpacity - frac) > 1e-6)
			{
				_settings.HeatmapPeakOpacity = frac;
				Raise();
				Persist();
			}
		}
	}

	public double HeatmapRegionOpacityPercent
	{
		get => _settings.HeatmapRegionOpacity * 100;
		set
		{
			double frac = Math.Clamp(value, 0, 100) / 100;
			if (Math.Abs(_settings.HeatmapRegionOpacity - frac) > 1e-6)
			{
				_settings.HeatmapRegionOpacity = frac;
				Raise();
				Persist();
			}
		}
	}

	public double HeatmapRegionThresholdPercent
	{
		get => _settings.HeatmapRegionThreshold * 100;
		set
		{
			double frac = Math.Clamp(value, 0, 100) / 100;
			if (Math.Abs(_settings.HeatmapRegionThreshold - frac) > 1e-6)
			{
				_settings.HeatmapRegionThreshold = frac;
				Raise();
				Persist();
			}
		}
	}

	public bool LfHfHaloAdditive
	{
		get => _settings.LfHfHaloAdditive;
		set
		{
			if (_settings.LfHfHaloAdditive != value)
			{
				_settings.LfHfHaloAdditive = value;
				Raise();
				Persist();
			}
		}
	}

	public bool LobesAdditive
	{
		get => _settings.LobesAdditive;
		set
		{
			if (_settings.LobesAdditive != value)
			{
				_settings.LobesAdditive = value;
				Raise();
				Persist();
			}
		}
	}

	public bool TrailAdditive
	{
		get => _settings.TrailAdditive;
		set
		{
			if (_settings.TrailAdditive != value)
			{
				_settings.TrailAdditive = value;
				Raise();
				Persist();
			}
		}
	}

	public bool HeatmapAdditive
	{
		get => _settings.HeatmapAdditive;
		set
		{
			if (_settings.HeatmapAdditive != value)
			{
				_settings.HeatmapAdditive = value;
				Raise();
				Persist();
			}
		}
	}

	public bool MarkerHaloAdditive
	{
		get => _settings.MarkerHaloAdditive;
		set
		{
			if (_settings.MarkerHaloAdditive != value)
			{
				_settings.MarkerHaloAdditive = value;
				Raise();
				Persist();
			}
		}
	}

	public bool HistogramAdditive
	{
		get => _settings.HistogramAdditive;
		set
		{
			if (_settings.HistogramAdditive != value)
			{
				_settings.HistogramAdditive = value;
				Raise();
				Persist();
			}
		}
	}

	public int RegulationHeatmapLength
	{
		get => _settings.RegulationHeatmapLength;
		set
		{
			int clamped = Math.Clamp(value, 60, 518400);
			if (_settings.RegulationHeatmapLength != clamped)
			{
				_settings.RegulationHeatmapLength = clamped;
				Raise();
				Persist();
			}
		}
	}

	public double HrvEmitIntervalSeconds
	{
		get => _settings.HrvEmitIntervalSeconds;
		set
		{
			double clamped = Math.Clamp(value, 0.5, 30.0);
			if (Math.Abs(_settings.HrvEmitIntervalSeconds - clamped) > 1e-6)
			{
				_settings.HrvEmitIntervalSeconds = clamped;
				Raise();
				Persist();
			}
		}
	}

	public int SparklineWindowMinutes
	{
		get => _settings.SparklineWindowMinutes;
		set
		{
			int clamped = Math.Clamp(value, 1, 360);
			if (_settings.SparklineWindowMinutes != clamped)
			{
				_settings.SparklineWindowMinutes = clamped;
				Raise();
				Persist();
			}
		}
	}

	public double EcgCenteringEaseRate
	{
		get => _settings.EcgCenteringEaseRate;
		set
		{
			double clamped = Math.Clamp(value, 0.5, 12.0);
			if (Math.Abs(_settings.EcgCenteringEaseRate - clamped) > 1e-6)
			{
				_settings.EcgCenteringEaseRate = clamped;
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
	public ICommand RevokeHealthCommand { get; }
	public ICommand ExportDatabaseCommand { get; }
	public ICommand ClearDataCommand { get; }
	public ICommand ConfirmClearDataCommand { get; }
	public ICommand CancelClearDataCommand { get; }

	/// <summary>True while the destructive "clear my data" confirmation panel is showing.</summary>
	public bool IsClearDataConfirmPending
	{
		get => _isClearDataConfirmPending;
		private set => SetField(ref _isClearDataConfirmPending, value);
	}

	/// <summary>One-line outcome shown after a clear completes; null when nothing to report.</summary>
	public string? ClearDataStatus
	{
		get => _clearDataStatus;
		private set
		{
			if (SetField(ref _clearDataStatus, value))
			{
				Raise(nameof(HasClearDataStatus));
			}
		}
	}

	public bool HasClearDataStatus => !string.IsNullOrEmpty(_clearDataStatus);

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

	private void BeginClearData()
	{
		ClearDataStatus = null;
		IsClearDataConfirmPending = true;
	}

	private void CancelClearData() => IsClearDataConfirmPending = false;

	private async Task ConfirmClearDataAsync()
	{
		IsClearDataConfirmPending = false;
		if (_clearData is null)
		{
			return;
		}

		await _clearData().ConfigureAwait(true);
		ClearDataStatus = "Your data has been cleared.";
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
		bool granted = await _requestHealthKit().ConfigureAwait(true);
		if (granted && !_settings.RecordToHealth)
		{
			RecordToHealth = true;
		}
	}

	/// <summary>
	/// Revokes the health-store integration. Stops all in-app reading/writing
	/// immediately by clearing the opt-in flags, then drives the platform revoke:
	/// on Android, Health Connect's programmatic revoke + its settings screen; on
	/// iOS, a deep link into the Health app (HealthKit grants can't be revoked by an
	/// app — only the user can, in Health).
	/// </summary>
	private async Task RevokeHealthAsync()
	{
		RecordToHealth = false;
		WriteEpisodesToHealthKit = false;

		if (_revokeHealthAccess is not null)
		{
			await _revokeHealthAccess().ConfigureAwait(true);
		}
	}
}
