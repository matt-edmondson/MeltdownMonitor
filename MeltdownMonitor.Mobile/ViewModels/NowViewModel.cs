using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Motion;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Mobile.ViewModels;

/// <summary>
/// Backing view model for the Now screen — live state pill, HR/RMSSD
/// readout, sparkline history, and the connect/disconnect button. Pipeline
/// wiring is injected; this VM stays free of CoreBluetooth so the iOS head
/// can compose it with the real <c>BleHrSource</c> and any other host can
/// stub it with a synthetic source for screenshots.
/// </summary>
public sealed class NowViewModel : ViewModelBase
{
	private const int SparklineMaxPoints = 360; // ~60 s at 6 Hz update cadence

	// Recent RR intervals feeding the Regulation Field's live-trace texture — the same
	// window the desktop RegulationFieldView keeps (RrBufferLength).
	private const int RrBufferLength = 160;

	private readonly ObservableCollection<double> _rmssdHistory = [];
	private readonly ObservableCollection<double> _baselineHistory = [];
	private readonly ObservableCollection<double> _rmssdTimestamps = [];
	private readonly List<RegulationTrailPoint> _regulationTrail = [];
	private readonly List<double> _rrBuffer = [];
	private IReadOnlyList<double> _recentRr = [];
	private long _rrBeatsAppended;

	private readonly Func<Task>? _onConnect;
	private readonly Func<Task>? _onDisconnect;
	private readonly Func<AnnotationLabel, string?, Task>? _onAnnotate;
	private readonly Func<int>? _trailLengthProvider;
	private readonly Func<int>? _heatmapLengthProvider;
	private readonly Func<double>? _jitterExaggerationProvider;
	private readonly Func<double>? _lobeThicknessProvider;
	private readonly Func<int>? _indexBucketsProvider;
	private readonly Func<int>? _vagalBucketsProvider;
	private readonly Func<int>? _lobeSegmentsProvider;
	private readonly Func<double>? _recoveryArrowSpeedProvider;
	private readonly Func<int>? _recoveryArrowCountProvider;
	private readonly Func<double>? _lobeOpacityProvider;
	private readonly Func<double>? _trailOpacityProvider;
	private readonly Func<double>? _histogramOpacityProvider;
	private readonly Func<double>? _heatmapOpacityProvider;
	private readonly Func<double>? _heatmapPeakOpacityProvider;
	private readonly Func<double>? _heatmapRegionOpacityProvider;
	private readonly Func<double>? _heatmapRegionThresholdProvider;
	private readonly Func<bool>? _useLfHfCorroborationProvider;
	private readonly Func<bool>? _lfHfHaloAdditiveProvider;
	private readonly Func<bool>? _lobesAdditiveProvider;
	private readonly Func<bool>? _trailAdditiveProvider;
	private readonly Func<bool>? _heatmapAdditiveProvider;
	private readonly Func<bool>? _markerHaloAdditiveProvider;
	private readonly Func<bool>? _histogramAdditiveProvider;
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
	private MovementLevel _movement = MovementLevel.Unknown;
	private DeviceInformation? _deviceInfo;
	private DateTimeOffset _stateChangedAt = DateTimeOffset.UtcNow;
	private ConnectionState _connection = ConnectionState.Disconnected;
	private RegulationReading _reading = new(0.0, 1.0, 0.0, 0.5, 0.0);
	private RegulationDynamics _dynamics = RegulationDynamics.Steady;
	private RecoveryProgress _recovery = RecoveryProgress.Inactive;
	private IReadOnlyList<RegulationTrailPoint> _regulationTrailSnapshot = [];
	private IReadOnlyList<RegulationTrailPoint> _dwellTrailSnapshot = [];
	private bool _isAnnotationSheetOpen;
	private string _annotationNotes = string.Empty;
	private double _jitterExaggeration = 1.0;
	private double _lobeThickness = 1.0;
	private int _indexBuckets = 24;
	private int _vagalBuckets = 16;
	private int _lobeSegments = LemniscateGeometry.DefaultSegments;
	private double _recoveryArrowSpeed = 0.7;
	private int _recoveryArrowCount = 3;
	private double _lobeOpacity = 0.60;
	private double _trailOpacity = 0.70;
	private double _histogramOpacity = 0.60;
	private double _heatmapOpacity = 0.35;
	private double _heatmapPeakOpacity = 0.70;
	private double _heatmapRegionOpacity = 0.55;
	private double _heatmapRegionThreshold = 0.50;
	private bool _useLfHfCorroboration = true;
	private bool _lfHfHaloAdditive = true;
	private bool _lobesAdditive = true;
	private bool _trailAdditive = true;
	private bool _heatmapAdditive = true;
	private bool _markerHaloAdditive = true;
	private bool _histogramAdditive = true;

	public NowViewModel(
		Func<Task>? onConnect = null,
		Func<Task>? onDisconnect = null,
		Func<AnnotationLabel, string?, Task>? onAnnotate = null,
		Func<int>? trailLengthProvider = null,
		Func<double>? jitterExaggerationProvider = null,
		Func<double>? lobeThicknessProvider = null,
		Func<int>? indexBucketsProvider = null,
		Func<int>? vagalBucketsProvider = null,
		Func<int>? lobeSegmentsProvider = null,
		Func<double>? recoveryArrowSpeedProvider = null,
		Func<int>? recoveryArrowCountProvider = null,
		Func<int>? heatmapLengthProvider = null,
		Func<double>? lobeOpacityProvider = null,
		Func<double>? trailOpacityProvider = null,
		Func<double>? histogramOpacityProvider = null,
		Func<double>? heatmapOpacityProvider = null,
		Func<double>? heatmapPeakOpacityProvider = null,
		Func<double>? heatmapRegionOpacityProvider = null,
		Func<double>? heatmapRegionThresholdProvider = null,
		Func<bool>? useLfHfCorroborationProvider = null,
		Func<bool>? lfHfHaloAdditiveProvider = null,
		Func<bool>? lobesAdditiveProvider = null,
		Func<bool>? trailAdditiveProvider = null,
		Func<bool>? heatmapAdditiveProvider = null,
		Func<bool>? markerHaloAdditiveProvider = null,
		Func<bool>? histogramAdditiveProvider = null)
	{
		_onConnect = onConnect;
		_onDisconnect = onDisconnect;
		_onAnnotate = onAnnotate;
		_trailLengthProvider = trailLengthProvider;
		_heatmapLengthProvider = heatmapLengthProvider;
		_jitterExaggerationProvider = jitterExaggerationProvider;
		_lobeThicknessProvider = lobeThicknessProvider;
		_indexBucketsProvider = indexBucketsProvider;
		_vagalBucketsProvider = vagalBucketsProvider;
		_lobeSegmentsProvider = lobeSegmentsProvider;
		_recoveryArrowSpeedProvider = recoveryArrowSpeedProvider;
		_recoveryArrowCountProvider = recoveryArrowCountProvider;
		_lobeOpacityProvider = lobeOpacityProvider;
		_trailOpacityProvider = trailOpacityProvider;
		_histogramOpacityProvider = histogramOpacityProvider;
		_heatmapOpacityProvider = heatmapOpacityProvider;
		_heatmapPeakOpacityProvider = heatmapPeakOpacityProvider;
		_heatmapRegionOpacityProvider = heatmapRegionOpacityProvider;
		_heatmapRegionThresholdProvider = heatmapRegionThresholdProvider;
		_useLfHfCorroborationProvider = useLfHfCorroborationProvider;
		_lfHfHaloAdditiveProvider = lfHfHaloAdditiveProvider;
		_lobesAdditiveProvider = lobesAdditiveProvider;
		_trailAdditiveProvider = trailAdditiveProvider;
		_heatmapAdditiveProvider = heatmapAdditiveProvider;
		_markerHaloAdditiveProvider = markerHaloAdditiveProvider;
		_histogramAdditiveProvider = histogramAdditiveProvider;
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

	/// <summary>The longer dwell window (oldest first) the field's density heatmap accumulates
	/// over — where regulation has settled, distinct from the shorter comet trail. The backing
	/// buffer holds max(comet, heatmap) points, mirroring the desktop view's single capped
	/// buffer; this publishes the whole buffer while <see cref="RegulationTrail"/> stays the
	/// recent comet slice.</summary>
	public IReadOnlyList<RegulationTrailPoint> DwellTrail
	{
		get => _dwellTrailSnapshot;
		private set => SetField(ref _dwellTrailSnapshot, value);
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

	/// <summary>Configured bucket resolution of the arousal-index (X) axis histogram (clamped 6–64).
	/// Refreshed on each reading so a setting change applies live, like the comet-trail length.</summary>
	public int IndexBuckets
	{
		get => _indexBuckets;
		private set => SetField(ref _indexBuckets, value);
	}

	/// <summary>Configured bucket resolution of the vagal-tone (Y) axis histogram (clamped 6–64).
	/// Refreshed on each reading so a setting change applies live, like the comet-trail length.</summary>
	public int VagalBuckets
	{
		get => _vagalBuckets;
		private set => SetField(ref _vagalBuckets, value);
	}

	/// <summary>Configured Regulation Field outline resolution — points sampled along the figure-8
	/// (clamped 24–256). Refreshed on each reading so a setting change applies live, like the
	/// comet-trail length. Bound by the control's <c>LobeSegments</c> styled property.</summary>
	public int LobeSegments
	{
		get => _lobeSegments;
		private set => SetField(ref _lobeSegments, value);
	}

	/// <summary>Configured loop rate of the recovery arrows — how fast they pulse inward toward the
	/// centre (clamped 0.1–3.0). Refreshed on each reading so a setting change applies live. Bound by
	/// the control's <c>RecoveryArrowSpeed</c> styled property.</summary>
	public double RecoveryArrowSpeed
	{
		get => _recoveryArrowSpeed;
		private set => SetField(ref _recoveryArrowSpeed, value);
	}

	/// <summary>Configured number of recovery arrows in the inward-pulling train (clamped 1–6).
	/// Refreshed on each reading so a setting change applies live. Bound by the control's
	/// <c>RecoveryArrowCount</c> styled property.</summary>
	public int RecoveryArrowCount
	{
		get => _recoveryArrowCount;
		private set => SetField(ref _recoveryArrowCount, value);
	}

	/// <summary>Configured opacity of the live-trace lobes (0–1, additive). Refreshed on each
	/// reading so a setting change applies live, like the other field knobs.</summary>
	public double LobeOpacity
	{
		get => _lobeOpacity;
		private set => SetField(ref _lobeOpacity, value);
	}

	/// <summary>Configured opacity of the comet trail (0–1, additive). Refreshed on each reading.</summary>
	public double TrailOpacity
	{
		get => _trailOpacity;
		private set => SetField(ref _trailOpacity, value);
	}

	/// <summary>Configured opacity of the axis histograms (0–1, additive). Refreshed on each reading.</summary>
	public double HistogramOpacity
	{
		get => _histogramOpacity;
		private set => SetField(ref _histogramOpacity, value);
	}

	/// <summary>Configured overall opacity of the dwell heatmap (0–1). Refreshed on each reading.</summary>
	public double HeatmapOpacity
	{
		get => _heatmapOpacity;
		private set => SetField(ref _heatmapOpacity, value);
	}

	/// <summary>Configured opacity of the peak-dwell crosshair (0–1). Refreshed on each reading.</summary>
	public double HeatmapPeakOpacity
	{
		get => _heatmapPeakOpacity;
		private set => SetField(ref _heatmapPeakOpacity, value);
	}

	/// <summary>Configured opacity of the dashed high-density region box (0–1). Refreshed on each reading.</summary>
	public double HeatmapRegionOpacity
	{
		get => _heatmapRegionOpacity;
		private set => SetField(ref _heatmapRegionOpacity, value);
	}

	/// <summary>Configured peak-share threshold for the dashed region (0–1). Refreshed on each reading.</summary>
	public double HeatmapRegionThreshold
	{
		get => _heatmapRegionThreshold;
		private set => SetField(ref _heatmapRegionThreshold, value);
	}

	/// <summary>Mirrors DetectionThresholds.UseLfHfCorroboration — gates the field's LF/HF
	/// balance halo. Refreshed on each reading.</summary>
	public bool UseLfHfCorroboration
	{
		get => _useLfHfCorroboration;
		private set => SetField(ref _useLfHfCorroboration, value);
	}

	/// <summary>Whether the LF/HF halo blends additively (glow) vs alpha-over. Refreshed on each reading.</summary>
	public bool LfHfHaloAdditive
	{
		get => _lfHfHaloAdditive;
		private set => SetField(ref _lfHfHaloAdditive, value);
	}

	/// <summary>Whether the live-trace lobes blend additively (glow) vs alpha-over. Refreshed on each reading.</summary>
	public bool LobesAdditive
	{
		get => _lobesAdditive;
		private set => SetField(ref _lobesAdditive, value);
	}

	/// <summary>Whether the comet trail blends additively (glow) vs alpha-over. Refreshed on each reading.</summary>
	public bool TrailAdditive
	{
		get => _trailAdditive;
		private set => SetField(ref _trailAdditive, value);
	}

	/// <summary>Whether the dwell-heatmap cells blend additively (glow) vs alpha-over. Refreshed on each reading.</summary>
	public bool HeatmapAdditive
	{
		get => _heatmapAdditive;
		private set => SetField(ref _heatmapAdditive, value);
	}

	/// <summary>Whether the marker halos blend additively (glow) vs alpha-over. Refreshed on each reading.</summary>
	public bool MarkerHaloAdditive
	{
		get => _markerHaloAdditive;
		private set => SetField(ref _markerHaloAdditive, value);
	}

	/// <summary>Whether the axis histogram bars blend additively (glow) vs alpha-over. Refreshed on each reading.</summary>
	public bool HistogramAdditive
	{
		get => _histogramAdditive;
		private set => SetField(ref _histogramAdditive, value);
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

	/// <summary>Live movement level from the motion source (strap PMD or device IMU).</summary>
	public MovementLevel Movement
	{
		get => _movement;
		private set
		{
			if (SetField(ref _movement, value))
			{
				Raise(nameof(MovementText));
				Raise(nameof(IsMovementVisible));
				Raise(nameof(IsMovementGating));
			}
		}
	}

	/// <summary>Human-readable movement label, e.g. "Moving (walking)". Empty when no motion data.</summary>
	public string MovementText => _movement switch
	{
		MovementLevel.Still => "Still",
		MovementLevel.Light => "Light movement",
		MovementLevel.Moderate => "Moving (walking)",
		MovementLevel.Vigorous => "Moving (vigorous)",
		_ => string.Empty,
	};

	/// <summary>Whether to show the movement indicator at all — hidden when no motion source is feeding.</summary>
	public bool IsMovementVisible => _movement != MovementLevel.Unknown;

	/// <summary>True when movement is high enough (Moderate+) that detection is likely being gated —
	/// the cue that alerts are deferred and the baseline is paused.</summary>
	public bool IsMovementGating => _movement >= MovementLevel.Moderate;

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
		pipeline.MovementUpdated += OnMovementUpdated;
		pipeline.DeviceInfoUpdated += OnDeviceInfoUpdated;
		pipeline.BeatReceived += OnBeatReceived;
		_coldCalibratedProvider = () => pipeline.IsColdCalibrated;

		// Seed the comet trail + dwell window from persisted history so the field isn't
		// blank on launch. Capacity holds the longer of the two windows; the comet
		// publishes its recent slice.
		if (_regulationTrail.Count == 0)
		{
			int cometCap = CometCap;
			int cap = Math.Max(cometCap, HeatmapCap);
			var seed = pipeline.LoadRecentRegulationTrail(cap);
			if (seed.Count > 0)
			{
				_regulationTrail.AddRange(seed);
				while (_regulationTrail.Count > cap)
				{
					_regulationTrail.RemoveAt(0);
				}

				var snapshot = _regulationTrail.ToArray();
				RegulationTrail = snapshot.Length > cometCap ? snapshot[^cometCap..] : snapshot;
				DwellTrail = snapshot;
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
	/// Push a fresh movement snapshot into the VM. Wired to <see cref="Pipeline.MovementUpdated"/>
	/// and marshalled to the UI thread like the other handlers. Public so tests can drive it without
	/// a live pipeline.
	/// </summary>
	public void OnMovementUpdated(MovementSnapshot snapshot) => RunOnUi(() => Movement = snapshot.Level);

	/// <summary>
	/// Push device identity into the VM. Wired to <see cref="Pipeline.DeviceInfoUpdated"/>
	/// and marshalled to the UI thread. Public so tests can drive it without a live pipeline.
	/// </summary>
	public void OnDeviceInfoUpdated(DeviceInformation info) => RunOnUi(() => DeviceInfo = info);

	/// <summary>Recent non-artifact RR intervals (ms), oldest first, feeding the Regulation
	/// Field's live-trace texture. A fresh instance is published per beat so the control's
	/// AffectsRender binding fires.</summary>
	public IReadOnlyList<double> RecentRr
	{
		get => _recentRr;
		private set => SetField(ref _recentRr, value);
	}

	/// <summary>Total non-artifact beats ever appended — the absolute beat timeline the
	/// RR texture playhead scrolls along (mirrors the desktop view's _beatsAppended).</summary>
	public long RrBeatsAppended
	{
		get => _rrBeatsAppended;
		private set => SetField(ref _rrBeatsAppended, value);
	}

	/// <summary>
	/// Push one raw beat into the VM's RR texture buffer. Wired to
	/// <see cref="Pipeline.BeatReceived"/> and marshalled to the UI thread like the other
	/// handlers. Artifacts are skipped, as the desktop consumers do. Public so tests can
	/// drive the buffer without a live pipeline.
	/// </summary>
	public void OnBeatReceived(Beat beat) => RunOnUi(() =>
	{
		if (beat.IsArtifact)
		{
			return;
		}

		_rrBuffer.Add(beat.RrMs);
		while (_rrBuffer.Count > RrBufferLength)
		{
			_rrBuffer.RemoveAt(0);
		}

		RrBeatsAppended++;
		RecentRr = _rrBuffer.ToArray();
	});

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
		// The buffer holds the longer of the comet and dwell-heatmap windows (one buffer,
		// two views — mirroring the desktop); the comet publishes its recent slice.
		_regulationTrail.Add(new RegulationTrailPoint(reading, _state));
		int cometCap = CometCap;
		int cap = Math.Max(cometCap, HeatmapCap);
		while (_regulationTrail.Count > cap)
		{
			_regulationTrail.RemoveAt(0);
		}

		// Hand the control fresh list instances so its AffectsRender bindings fire.
		var snapshot = _regulationTrail.ToArray();
		RegulationTrail = snapshot.Length > cometCap ? snapshot[^cometCap..] : snapshot;
		DwellTrail = snapshot;

		// Pick up any live Regulation Field display changes the same way (settings can
		// be adjusted while the Now screen is open).
		JitterExaggeration = Math.Clamp(_jitterExaggerationProvider?.Invoke() ?? 1.0, 0.0, 3.0);
		LobeThickness = Math.Clamp(_lobeThicknessProvider?.Invoke() ?? 1.0, 0.5, 3.0);
		IndexBuckets = Math.Clamp(_indexBucketsProvider?.Invoke() ?? 24, 6, 64);
		VagalBuckets = Math.Clamp(_vagalBucketsProvider?.Invoke() ?? 16, 6, 64);
		LobeSegments = Math.Clamp(
			_lobeSegmentsProvider?.Invoke() ?? LemniscateGeometry.DefaultSegments,
			LemniscateGeometry.MinSegments, LemniscateGeometry.MaxSegments);
		RecoveryArrowSpeed = Math.Clamp(_recoveryArrowSpeedProvider?.Invoke() ?? 0.7, 0.1, 3.0);
		RecoveryArrowCount = Math.Clamp(_recoveryArrowCountProvider?.Invoke() ?? 3, 1, 6);
		LobeOpacity = Math.Clamp(_lobeOpacityProvider?.Invoke() ?? 0.60, 0.0, 1.0);
		TrailOpacity = Math.Clamp(_trailOpacityProvider?.Invoke() ?? 0.70, 0.0, 1.0);
		HistogramOpacity = Math.Clamp(_histogramOpacityProvider?.Invoke() ?? 0.60, 0.0, 1.0);
		HeatmapOpacity = Math.Clamp(_heatmapOpacityProvider?.Invoke() ?? 0.35, 0.0, 1.0);
		HeatmapPeakOpacity = Math.Clamp(_heatmapPeakOpacityProvider?.Invoke() ?? 0.70, 0.0, 1.0);
		HeatmapRegionOpacity = Math.Clamp(_heatmapRegionOpacityProvider?.Invoke() ?? 0.55, 0.0, 1.0);
		HeatmapRegionThreshold = Math.Clamp(_heatmapRegionThresholdProvider?.Invoke() ?? 0.50, 0.0, 1.0);
		UseLfHfCorroboration = _useLfHfCorroborationProvider?.Invoke() ?? true;
		LfHfHaloAdditive = _lfHfHaloAdditiveProvider?.Invoke() ?? true;
		LobesAdditive = _lobesAdditiveProvider?.Invoke() ?? true;
		TrailAdditive = _trailAdditiveProvider?.Invoke() ?? true;
		HeatmapAdditive = _heatmapAdditiveProvider?.Invoke() ?? true;
		MarkerHaloAdditive = _markerHaloAdditiveProvider?.Invoke() ?? true;
		HistogramAdditive = _histogramAdditiveProvider?.Invoke() ?? true;
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

	// Comet length (12–2160) and dwell-heatmap window (60–518400), clamped to the same
	// ranges the desktop knobs expose. The trail buffer holds the longer of the two.
	private int CometCap => Math.Clamp(_trailLengthProvider?.Invoke() ?? 48, 12, 2160);

	private int HeatmapCap => Math.Clamp(_heatmapLengthProvider?.Invoke() ?? 720, 60, 518400);

	private static void RunOnUi(Action apply)
	{
		// With no Avalonia Application (unit tests / design-time) there is no UI thread to marshal to,
		// so run inline. Checked first so we never touch Dispatcher.UIThread in that context.
		if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess())
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
