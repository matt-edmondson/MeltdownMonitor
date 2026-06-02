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

	private readonly ObservableCollection<double> _rmssdHistory = [];
	private readonly ObservableCollection<double> _baselineHistory = [];
	private readonly ObservableCollection<double> _rmssdTimestamps = [];
	private readonly List<RegulationTrailPoint> _regulationTrail = [];

	private readonly Func<Task>? _onConnect;
	private readonly Func<Task>? _onDisconnect;
	private readonly Func<AnnotationLabel, string?, Task>? _onAnnotate;
	private readonly Func<int>? _trailLengthProvider;
	private readonly Func<double>? _jitterExaggerationProvider;
	private readonly Func<double>? _lobeThicknessProvider;
	private Func<bool>? _coldCalibratedProvider;

	private DetectorState _state = DetectorState.Idle;
	private bool _isShutdown;
	private RegulationDynamics _hypoarousalDynamics = RegulationDynamics.Steady;
	private bool _isColdCalibrated;
	private bool _isPaused;
	private double _heartRate;
	private double _rmssd;
	private double _baselineRmssd;
	private int? _batteryPercent;
	private SensorContactStatus _contact = SensorContactStatus.NotSupported;
	private DeviceInformation? _deviceInfo;
	private DateTimeOffset _stateChangedAt = DateTimeOffset.UtcNow;
	private ConnectionState _connection = ConnectionState.Disconnected;
	private RegulationReading _reading = new(0.0, 1.0, 0.0, 0.5, 0.0);
	private RegulationDynamics _dynamics = RegulationDynamics.Steady;
	private RecoveryProgress _recovery = RecoveryProgress.Inactive;
	private IReadOnlyList<RegulationTrailPoint> _regulationTrailSnapshot = [];
	private bool _isAnnotationSheetOpen;
	private string _annotationNotes = string.Empty;
	private double _jitterExaggeration = 1.0;
	private double _lobeThickness = 1.0;

	public NowViewModel(
		Func<Task>? onConnect = null,
		Func<Task>? onDisconnect = null,
		Func<AnnotationLabel, string?, Task>? onAnnotate = null,
		Func<int>? trailLengthProvider = null,
		Func<double>? jitterExaggerationProvider = null,
		Func<double>? lobeThicknessProvider = null)
	{
		_onConnect = onConnect;
		_onDisconnect = onDisconnect;
		_onAnnotate = onAnnotate;
		_trailLengthProvider = trailLengthProvider;
		_jitterExaggerationProvider = jitterExaggerationProvider;
		_lobeThicknessProvider = lobeThicknessProvider;
		ToggleConnectionCommand = new RelayCommand(ToggleConnection);
		OpenAnnotationCommand = new RelayCommand(() => IsAnnotationSheetOpen = true);
		CancelAnnotationCommand = new RelayCommand(CloseAnnotationSheet);
		RecordAnnotationCommand = new RelayCommand<AnnotationLabel>(label => _ = RecordAnnotationAsync(label));
	}

	public IReadOnlyList<double> RmssdHistory => _rmssdHistory;
	public IReadOnlyList<double> BaselineHistory => _baselineHistory;

	/// <summary>Unix epoch seconds for each <see cref="RmssdHistory"/> / <see cref="BaselineHistory"/>
	/// point — both series share this sample cadence. Lets the sparkline space points by real time.</summary>
	public IReadOnlyList<double> RmssdTimestamps => _rmssdTimestamps;

	/// <summary>Latest arousal-vs-baseline reading driving the Regulation Field.</summary>
	public RegulationReading Reading
	{
		get => _reading;
		private set => SetField(ref _reading, value);
	}

	/// <summary>Latest escalation/de-escalation velocity + trend driving the field's arrow.</summary>
	public RegulationDynamics Dynamics
	{
		get => _dynamics;
		private set
		{
			if (SetField(ref _dynamics, value))
			{
				Raise(nameof(TrendLabel));
				Raise(nameof(VelocityText));
				Raise(nameof(NormalizedSpeed));
				Raise(nameof(IsTrendVisible));
			}
		}
	}

	/// <summary>Velocity/trend of the Hypoarousal scalar — the rate of approach to collapse —
	/// for the Regulation Field's collapse arrow. Bound to <c>RegulationField.HypoarousalDynamics</c>.</summary>
	public RegulationDynamics HypoarousalDynamics
	{
		get => _hypoarousalDynamics;
		private set => SetField(ref _hypoarousalDynamics, value);
	}

	/// <summary>Human-readable trend word for the readout.</summary>
	public string TrendLabel => _dynamics.Trend switch
	{
		RegulationTrend.Escalating => "Escalating",
		RegulationTrend.DeEscalating => "Easing",
		_ => "Steady",
	};

	/// <summary>Signed rate for the readout, or "steady" inside the deadband.</summary>
	public string VelocityText => _dynamics.Trend == RegulationTrend.Steady
		? "steady"
		: $"{_dynamics.Velocity:+0.00;-0.00} /s";

	/// <summary>[0,1] magnitude for the readout bar.</summary>
	public double NormalizedSpeed => _dynamics.NormalizedSpeed;

	/// <summary>Whether to show the trend readout — only when moving and the baseline is warm.</summary>
	public bool IsTrendVisible => _dynamics.Trend != RegulationTrend.Steady && _reading.Confidence >= 0.999;

	/// <summary>How close the body is to clearing the current episode, driving the field's
	/// recovery indicator. <see cref="RecoveryProgress.Inactive"/> outside Warning/Alerting.</summary>
	public RecoveryProgress Recovery
	{
		get => _recovery;
		private set
		{
			if (SetField(ref _recovery, value))
			{
				Raise(nameof(RecoveryFraction));
				Raise(nameof(RecoveryText));
				Raise(nameof(IsRecoveryVisible));
			}
		}
	}

	/// <summary>[0,1] two-stage recovery progress for the readout bar.</summary>
	public double RecoveryFraction => _recovery.Overall;

	/// <summary>Human-readable recovery progress for the readout.</summary>
	public string RecoveryText => $"Recovery {_recovery.Overall * 100:F0}%";

	/// <summary>Whether to show the recovery readout — only during an active episode.</summary>
	public bool IsRecoveryVisible => _recovery.IsActive;

	/// <summary>True during a sustained low-arousal/shutdown episode, so the Now screen can flag
	/// collapse distinctly from calm REST (audit A(b)). Driven by the debounced detector state.</summary>
	public bool IsShutdown
	{
		get => _isShutdown;
		private set => SetField(ref _isShutdown, value);
	}

	/// <summary>True when the baseline was self-calibrated cold with no personal history anchor, so
	/// the UI can flag that readings may be measured against a possibly-activated baseline (audit B).</summary>
	public bool IsColdCalibrated
	{
		get => _isColdCalibrated;
		private set => SetField(ref _isColdCalibrated, value);
	}

	/// <summary>Recent trail points (oldest first) drawn as the field's comet trail, each
	/// carrying the detector state it was captured under so segments keep their original
	/// colour. Replaced with a fresh snapshot on each update so the control re-renders.</summary>
	public IReadOnlyList<RegulationTrailPoint> RegulationTrail
	{
		get => _regulationTrailSnapshot;
		private set => SetField(ref _regulationTrailSnapshot, value);
	}

	/// <summary>Configured Regulation Field jitter exaggeration multiplier (clamped 0–3),
	/// driving the live trace's variability undulation. Refreshed on each reading so a
	/// setting change applies live, mirroring the comet-trail length.</summary>
	public double JitterExaggeration
	{
		get => _jitterExaggeration;
		private set => SetField(ref _jitterExaggeration, value);
	}

	/// <summary>Configured Regulation Field lobe stroke-thickness multiplier (clamped 0.5–3),
	/// driving the live trace's stroke width. Refreshed on each reading so a setting change
	/// applies live, mirroring the comet-trail length.</summary>
	public double LobeThickness
	{
		get => _lobeThickness;
		private set => SetField(ref _lobeThickness, value);
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
		pipeline.HypoarousalStateChanged += OnHypoarousalStateChanged;
		pipeline.HypoarousalDynamicsUpdated += OnHypoarousalDynamicsUpdated;
		pipeline.ReadingUpdated += OnReadingUpdated;
		pipeline.DynamicsUpdated += OnDynamicsUpdated;
		pipeline.RecoveryUpdated += OnRecoveryUpdated;
		pipeline.BatteryUpdated += OnBatteryUpdated;
		pipeline.ContactChanged += OnContactChanged;
		pipeline.DeviceInfoUpdated += OnDeviceInfoUpdated;
		_coldCalibratedProvider = () => pipeline.IsColdCalibrated;

		// Seed the comet trail from persisted history so the field isn't blank on launch.
		if (_regulationTrail.Count == 0)
		{
			int cap = Math.Clamp(_trailLengthProvider?.Invoke() ?? 48, 12, 2160);
			var seed = pipeline.LoadRecentRegulationTrail(cap);
			if (seed.Count > 0)
			{
				_regulationTrail.AddRange(seed);
				while (_regulationTrail.Count > cap)
				{
					_regulationTrail.RemoveAt(0);
				}

				RegulationTrail = _regulationTrail.ToArray();
			}
		}

		OnStateChanged(pipeline.CurrentState);
		OnHypoarousalStateChanged(pipeline.CurrentHypoarousalState);
		OnHypoarousalDynamicsUpdated(pipeline.LatestHypoarousalDynamics);
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
		// Cold-calibration is a stable provenance flag (it flips once at warm-completion); read it
		// off the pipeline each sample rather than threading a dedicated event.
		IsColdCalibrated = _coldCalibratedProvider?.Invoke() ?? false;

		_rmssdHistory.Add(sample.Rmssd);
		_baselineHistory.Add(sample.BaselineRmssd);
		_rmssdTimestamps.Add(sample.Timestamp.ToUnixTimeMilliseconds() / 1000.0);
		TrimHistory();

		Raise(nameof(RmssdHistory));
		Raise(nameof(BaselineHistory));
		Raise(nameof(RmssdTimestamps));
	});

	/// <summary>
	/// Update the state pill from a <see cref="Pipeline.StateChanged"/> event.
	/// State can advance without a fresh sample (e.g. Idle→Watching as the
	/// baseline warms, or Cooldown→Watching on the timer), so this is wired
	/// independently of <see cref="OnSampleUpdated"/>.
	/// </summary>
	public void OnStateChanged(DetectorState state) => RunOnUi(() => State = state);

	/// <summary>
	/// Reflect a low-arousal/shutdown state change from <see cref="Pipeline.HypoarousalStateChanged"/>.
	/// Marshalled to the UI thread. Public so tests can drive it without a live pipeline.
	/// </summary>
	public void OnHypoarousalStateChanged(HypoarousalState state) =>
		RunOnUi(() => IsShutdown = state == HypoarousalState.LowArousal);

	/// <summary>Reflect the Hypoarousal-scalar velocity from <see cref="Pipeline.HypoarousalDynamicsUpdated"/>.</summary>
	public void OnHypoarousalDynamicsUpdated(RegulationDynamics dynamics) =>
		RunOnUi(() => HypoarousalDynamics = dynamics);

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
		Raise(nameof(IsTrendVisible));

		// Capture the current detector state with the point so the segment keeps the
		// colour it was drawn in, rather than recolouring as the state later advances.
		_regulationTrail.Add(new RegulationTrailPoint(reading, _state));
		int cap = Math.Clamp(_trailLengthProvider?.Invoke() ?? 48, 12, 2160);
		while (_regulationTrail.Count > cap)
		{
			_regulationTrail.RemoveAt(0);
		}

		// Hand the control a fresh list instance so its AffectsRender binding fires.
		RegulationTrail = _regulationTrail.ToArray();

		// Pick up any live Regulation Field display changes the same way (settings can
		// be adjusted while the Now screen is open).
		JitterExaggeration = Math.Clamp(_jitterExaggerationProvider?.Invoke() ?? 1.0, 0.0, 3.0);
		LobeThickness = Math.Clamp(_lobeThicknessProvider?.Invoke() ?? 1.0, 0.5, 3.0);
	});

	/// <summary>
	/// Push fresh velocity/trend dynamics into the VM. Wired to
	/// <see cref="Pipeline.DynamicsUpdated"/> and marshalled to the UI thread like the
	/// other handlers. Public so tests can drive it without a live pipeline.
	/// </summary>
	public void OnDynamicsUpdated(RegulationDynamics dynamics) => RunOnUi(() => Dynamics = dynamics);

	/// <summary>
	/// Push fresh two-stage recovery progress into the VM. Wired to
	/// <see cref="Pipeline.RecoveryUpdated"/> and marshalled to the UI thread like the
	/// other handlers. Public so tests can drive it without a live pipeline.
	/// </summary>
	public void OnRecoveryUpdated(RecoveryProgress recovery) => RunOnUi(() => Recovery = recovery);

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

		while (_rmssdTimestamps.Count > SparklineMaxPoints)
		{
			_rmssdTimestamps.RemoveAt(0);
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
