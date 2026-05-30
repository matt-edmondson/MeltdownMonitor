using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Mobile.ViewModels;

/// <summary>
/// Backing view model for the Now screen — live state pill, HR/RMSSD
/// readout, sparkline history, and the connect/disconnect button. Pipeline
/// wiring is injected; this VM stays free of CoreBluetooth so the iOS head
/// can compose it with the real <c>PolarHrSource</c> and any other host can
/// stub it with a synthetic source for screenshots.
/// </summary>
public sealed class NowViewModel : ViewModelBase
{
	private const int SparklineMaxPoints = 360; // ~60 s at 6 Hz update cadence
	private const int RegulationTrailLength = 48; // comet trail length, matches the desktop field

	private readonly ObservableCollection<double> _rmssdHistory = [];
	private readonly ObservableCollection<double> _baselineHistory = [];
	private readonly List<RegulationReading> _regulationTrail = [];

	private readonly Func<Task>? _onConnect;
	private readonly Func<Task>? _onDisconnect;
	private readonly Func<AnnotationLabel, string?, Task>? _onAnnotate;

	private DetectorState _state = DetectorState.Idle;
	private bool _isPaused;
	private double _heartRate;
	private double _rmssd;
	private double _baselineRmssd;
	private int? _batteryPercent;
	private SensorContactStatus _contact = SensorContactStatus.NotSupported;
	private DeviceInformation? _deviceInfo;
	private DateTimeOffset _stateChangedAt = DateTimeOffset.UtcNow;
	private ConnectionState _connection = ConnectionState.Disconnected;
	private RegulationReading _reading = new(0.0, 1.0, 0.0);
	private IReadOnlyList<RegulationReading> _regulationTrailSnapshot = [];
	private bool _isAnnotationSheetOpen;
	private string _annotationNotes = string.Empty;

	public NowViewModel(
		Func<Task>? onConnect = null,
		Func<Task>? onDisconnect = null,
		Func<AnnotationLabel, string?, Task>? onAnnotate = null)
	{
		_onConnect = onConnect;
		_onDisconnect = onDisconnect;
		_onAnnotate = onAnnotate;
		ToggleConnectionCommand = new RelayCommand(ToggleConnection);
		OpenAnnotationCommand = new RelayCommand(() => IsAnnotationSheetOpen = true);
		CancelAnnotationCommand = new RelayCommand(CloseAnnotationSheet);
		RecordAnnotationCommand = new RelayCommand<AnnotationLabel>(label => _ = RecordAnnotationAsync(label));
	}

	public IReadOnlyList<double> RmssdHistory => _rmssdHistory;
	public IReadOnlyList<double> BaselineHistory => _baselineHistory;

	/// <summary>Latest arousal-vs-baseline reading driving the Regulation Field.</summary>
	public RegulationReading Reading
	{
		get => _reading;
		private set => SetField(ref _reading, value);
	}

	/// <summary>Recent readings (oldest first) drawn as the field's comet trail.
	/// Replaced with a fresh snapshot on each update so the control re-renders.</summary>
	public IReadOnlyList<RegulationReading> RegulationTrail
	{
		get => _regulationTrailSnapshot;
		private set => SetField(ref _regulationTrailSnapshot, value);
	}

	/// <summary>The detector-state accent the field's marker and trail take.</summary>
	public Color RegulationStateColor => StateColors.ColorFor(_state, _isPaused);

	public DetectorState State
	{
		get => _state;
		private set
		{
			if (SetField(ref _state, value))
			{
				_stateChangedAt = DateTimeOffset.UtcNow;
				Raise(nameof(StateLabel));
				Raise(nameof(StateBrush));
				Raise(nameof(RegulationStateColor));
				Raise(nameof(TimeSinceStateChange));
			}
		}
	}

	public bool IsPaused
	{
		get => _isPaused;
		set
		{
			if (SetField(ref _isPaused, value))
			{
				Raise(nameof(StateLabel));
				Raise(nameof(StateBrush));
				Raise(nameof(RegulationStateColor));
			}
		}
	}

	public string StateLabel => StateColors.LabelFor(_state, _isPaused);
	public IBrush StateBrush => StateColors.BrushFor(_state, _isPaused);

	public double HeartRate
	{
		get => _heartRate;
		private set
		{
			if (SetField(ref _heartRate, value))
			{
				Raise(nameof(HeartRateText));
			}
		}
	}

	public string HeartRateText =>
		_heartRate > 0 ? $"{_heartRate:F0} bpm" : "— bpm";

	public double Rmssd
	{
		get => _rmssd;
		private set
		{
			if (SetField(ref _rmssd, value))
			{
				Raise(nameof(RmssdText));
			}
		}
	}

	public string RmssdText =>
		_rmssd > 0 ? $"RMSSD {_rmssd:F1} ms" : "RMSSD —";

	public double BaselineRmssd
	{
		get => _baselineRmssd;
		private set
		{
			if (SetField(ref _baselineRmssd, value))
			{
				Raise(nameof(BaselineText));
			}
		}
	}

	public string BaselineText =>
		_baselineRmssd > 0 ? $"Baseline {_baselineRmssd:F1} ms" : "Baseline warming up…";

	public int? BatteryPercent
	{
		get => _batteryPercent;
		private set
		{
			if (SetField(ref _batteryPercent, value))
			{
				Raise(nameof(BatteryText));
			}
		}
	}

	public string BatteryText =>
		_batteryPercent is { } percent ? $"Battery {percent}%" : "Battery —";

	/// <summary>Live skin / electrode contact state from the sensor.</summary>
	public SensorContactStatus Contact
	{
		get => _contact;
		private set
		{
			if (SetField(ref _contact, value))
			{
				Raise(nameof(IsContactLost));
			}
		}
	}

	/// <summary>True when the sensor supports contact reporting and is currently
	/// out of contact — the cue to warn that readings are unreliable.</summary>
	public bool IsContactLost => _contact == SensorContactStatus.NotDetected;

	/// <summary>Sensor identity (model / firmware) once read from the device, else null.</summary>
	public DeviceInformation? DeviceInfo
	{
		get => _deviceInfo;
		private set
		{
			if (SetField(ref _deviceInfo, value))
			{
				Raise(nameof(DeviceInfoText));
				Raise(nameof(HasDeviceInfo));
			}
		}
	}

	/// <summary>One-line device summary for display, or null before it's read.</summary>
	public string? DeviceInfoText => _deviceInfo?.Summary;

	/// <summary>Whether device identity is available to show.</summary>
	public bool HasDeviceInfo => _deviceInfo is not null;

	public string TimeSinceStateChange
	{
		get
		{
			var span = DateTimeOffset.UtcNow - _stateChangedAt;
			if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds}s in {StateLabel}";
			if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m in {StateLabel}";
			return $"{(int)span.TotalHours}h in {StateLabel}";
		}
	}

	public ConnectionState Connection
	{
		get => _connection;
		set
		{
			if (SetField(ref _connection, value))
			{
				Raise(nameof(ConnectionLabel));
				(ToggleConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
			}
		}
	}

	public string ConnectionLabel => _connection switch
	{
		ConnectionState.Disconnected => "Connect device",
		ConnectionState.Connecting => "Connecting…",
		ConnectionState.Connected => "Disconnect",
		ConnectionState.Reconnecting => "Reconnecting…",
		_ => "Connect device",
	};

	public ICommand ToggleConnectionCommand { get; }

	/// <summary>
	/// The four self check-in labels (design doc §5) — the same set the desktop
	/// annotation dialog offers, surfaced as buttons in the Now-screen sheet.
	/// </summary>
	public IReadOnlyList<AnnotationLabel> AnnotationLabels { get; } = Enum.GetValues<AnnotationLabel>();

	/// <summary>Whether the "How are you feeling?" sheet is visible.</summary>
	public bool IsAnnotationSheetOpen
	{
		get => _isAnnotationSheetOpen;
		private set => SetField(ref _isAnnotationSheetOpen, value);
	}

	/// <summary>Optional free-text note bound to the sheet's text box.</summary>
	public string AnnotationNotes
	{
		get => _annotationNotes;
		set => SetField(ref _annotationNotes, value);
	}

	public ICommand OpenAnnotationCommand { get; }
	public ICommand CancelAnnotationCommand { get; }
	public ICommand RecordAnnotationCommand { get; }

	/// <summary>
	/// Persist a self check-in. The actual write is host-injected (the iOS head
	/// routes it through <c>MeltdownRepository.WriteAnnotation</c> and refreshes
	/// the History tab); the VM owns only the sheet's transient state. Exposed
	/// publicly so it can be awaited directly in tests rather than via the
	/// fire-and-forget command.
	/// </summary>
	public async Task RecordAnnotationAsync(AnnotationLabel label)
	{
		var notes = string.IsNullOrWhiteSpace(_annotationNotes) ? null : _annotationNotes.Trim();
		if (_onAnnotate is not null)
		{
			await _onAnnotate(label, notes).ConfigureAwait(true);
		}

		CloseAnnotationSheet();
	}

	private void CloseAnnotationSheet()
	{
		IsAnnotationSheetOpen = false;
		AnnotationNotes = string.Empty;
	}

	/// <summary>
	/// Subscribe to a live <see cref="Pipeline"/> so its samples and state
	/// transitions drive the Now screen. Called by the iOS head once the BLE
	/// pipeline is composed (design doc §6.1). Reflects the pipeline's current
	/// state immediately in case it advanced before the view subscribed.
	/// </summary>
	public void AttachPipeline(Pipeline pipeline)
	{
		ArgumentNullException.ThrowIfNull(pipeline);
		pipeline.SampleUpdated += OnSampleUpdated;
		pipeline.StateChanged += OnStateChanged;
		pipeline.ReadingUpdated += OnReadingUpdated;
		pipeline.BatteryUpdated += OnBatteryUpdated;
		pipeline.ContactChanged += OnContactChanged;
		pipeline.DeviceInfoUpdated += OnDeviceInfoUpdated;
		OnStateChanged(pipeline.CurrentState);
		OnContactChanged(pipeline.LatestContact);

		// Reflect a battery level the source may have already reported before we subscribed.
		if (pipeline.LatestBatteryPercent is { } percent)
		{
			OnBatteryUpdated(new BatteryReading(DateTimeOffset.UtcNow, percent));
		}

		// Reflect device identity if it was already read before we subscribed.
		if (pipeline.LatestDeviceInfo is { } info)
		{
			OnDeviceInfoUpdated(info);
		}
	}

	/// <summary>
	/// Push a fresh sample into the VM. Marshals to the UI thread so the
	/// pipeline callback can call this from a background BLE thread.
	/// </summary>
	public void OnSampleUpdated(HrvSample sample) => RunOnUi(() =>
	{
		// A sample arriving means beats are flowing — the link is live.
		Connection = ConnectionState.Connected;

		HeartRate = sample.MeanHr;
		Rmssd = sample.Rmssd;
		BaselineRmssd = sample.BaselineRmssd;
		State = sample.State;

		_rmssdHistory.Add(sample.Rmssd);
		_baselineHistory.Add(sample.BaselineRmssd);
		TrimHistory();

		Raise(nameof(RmssdHistory));
		Raise(nameof(BaselineHistory));
	});

	/// <summary>
	/// Update the state pill from a <see cref="Pipeline.StateChanged"/> event.
	/// State can advance without a fresh sample (e.g. Idle→Watching as the
	/// baseline warms, or Cooldown→Watching on the timer), so this is wired
	/// independently of <see cref="OnSampleUpdated"/>.
	/// </summary>
	public void OnStateChanged(DetectorState state) => RunOnUi(() => State = state);

	/// <summary>
	/// Push a fresh sensor battery level into the VM. Wired to
	/// <see cref="Pipeline.BatteryUpdated"/> and marshalled to the UI thread like
	/// the other handlers. Public so tests can drive it without a live pipeline.
	/// </summary>
	public void OnBatteryUpdated(BatteryReading reading) => RunOnUi(() => BatteryPercent = reading.Percent);

	/// <summary>
	/// Push a fresh sensor contact state into the VM. Wired to
	/// <see cref="Pipeline.ContactChanged"/> and marshalled to the UI thread like
	/// the other handlers. Public so tests can drive it without a live pipeline.
	/// </summary>
	public void OnContactChanged(SensorContactStatus status) => RunOnUi(() => Contact = status);

	/// <summary>
	/// Push device identity into the VM. Wired to <see cref="Pipeline.DeviceInfoUpdated"/>
	/// and marshalled to the UI thread. Public so tests can drive it without a live pipeline.
	/// </summary>
	public void OnDeviceInfoUpdated(DeviceInformation info) => RunOnUi(() => DeviceInfo = info);

	/// <summary>
	/// Push a fresh Regulation Field reading into the VM, appending it to the
	/// comet trail. Wired to <see cref="Pipeline.ReadingUpdated"/> and marshalled
	/// to the UI thread like <see cref="OnSampleUpdated"/>. Public so tests can
	/// drive the trail without a live pipeline.
	/// </summary>
	public void OnReadingUpdated(RegulationReading reading) => RunOnUi(() =>
	{
		Reading = reading;

		_regulationTrail.Add(reading);
		while (_regulationTrail.Count > RegulationTrailLength)
		{
			_regulationTrail.RemoveAt(0);
		}

		// Hand the control a fresh list instance so its AffectsRender binding fires.
		RegulationTrail = _regulationTrail.ToArray();
	});

	public void TickTimeDisplay() => Raise(nameof(TimeSinceStateChange));

	private static void RunOnUi(Action apply)
	{
		if (Dispatcher.UIThread.CheckAccess())
		{
			apply();
		}
		else
		{
			Dispatcher.UIThread.Post(apply);
		}
	}

	private void TrimHistory()
	{
		while (_rmssdHistory.Count > SparklineMaxPoints)
		{
			_rmssdHistory.RemoveAt(0);
		}

		while (_baselineHistory.Count > SparklineMaxPoints)
		{
			_baselineHistory.RemoveAt(0);
		}
	}

	private async void ToggleConnection()
	{
		switch (_connection)
		{
			case ConnectionState.Disconnected:
				Connection = ConnectionState.Connecting;
				if (_onConnect is not null)
				{
					await _onConnect().ConfigureAwait(true);
					Connection = ConnectionState.Connected;
				}

				break;
			case ConnectionState.Connected:
				if (_onDisconnect is not null)
				{
					await _onDisconnect().ConfigureAwait(true);
				}

				Connection = ConnectionState.Disconnected;
				break;
		}
	}
}
