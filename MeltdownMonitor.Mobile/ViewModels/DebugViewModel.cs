using Avalonia.Threading;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Motion;

namespace MeltdownMonitor.Mobile.ViewModels;

/// <summary>
/// Drives the opt-in Debug tab: a live read-out of everything the pipeline knows, for validating the
/// PMD path on-device. The headline is the <b>ECG-vs-HRS RR A/B</b> — our ECG-derived RR against the
/// sensor's own Heart Rate Service RR, side by side, with their mean-RR bias — plus per-stream artifact
/// rates, the full HRV/baseline dump, movement, ECG signal stats, and connection details. Purely
/// diagnostic: it reads pipeline state and the diagnostics side channel, and never affects detection.
/// </summary>
public sealed class DebugViewModel : ViewModelBase
{
	private readonly BeatDiagnosticsAggregator _aggregator = new(window: 60);

	private Pipeline? _pipeline;
	private BeatDiagnosticsSnapshot _snapshot = new([], null);
	private HrvSample? _sample;
	private MovementSnapshot? _movement;
	private EcgWaveformSnapshot _ecg = EcgWaveformSnapshot.Empty;
	private SensorContactStatus _contact = SensorContactStatus.NotSupported;
	private int? _battery;
	private DeviceInformation? _device;

	// ── ECG-vs-HRS A/B ───────────────────────────────────────────────────────

	/// <summary>The systematic mean-RR difference between the sensor's HRS RR and our ECG RR.</summary>
	public string AbBiasText => _snapshot.HrsVsEcgRrBiasMs is { } b
		? $"HRS − ECG mean-RR bias: {b:+0.0;-0.0;0.0} ms"
		: "HRS − ECG mean-RR bias: — (need both streams clean)";

	/// <summary>Which interval streams are currently producing intervals.</summary>
	public string SourceText
	{
		get
		{
			var live = _snapshot.Sources.Where(s => s.Count > 0).Select(s => Label(s.Source)).ToArray();
			return live.Length == 0 ? "Streams: none yet" : $"Streams live: {string.Join(", ", live)}";
		}
	}

	public string HrsSummary => Summary("HRS", Src(IntervalSource.HeartRateService));
	public string EcgSummary => Summary("ECG", Src(IntervalSource.PolarEcg));
	public string PpiSummary => Summary("PPI", Src(IntervalSource.PolarPpi));

	/// <summary>True once any PPI interval has arrived — lets the view hide the PPI row otherwise.</summary>
	public bool HasPpi => Src(IntervalSource.PolarPpi) is { Count: > 0 };

	public string HrsRecentRr => $"HRS RR: {Recent(Src(IntervalSource.HeartRateService))}";
	public string EcgRecentRr => $"ECG RR: {Recent(Src(IntervalSource.PolarEcg))}";

	/// <summary>Cross-source (ECG-vs-HRS) consensus verdict + recent conflict rate, when the ECG source is active.</summary>
	public string ConsensusText => _pipeline is { } p
		? $"Cross-check: {p.LatestConsensus} · conflicts {p.ConsensusConflictRate:P1}"
		: "Cross-check: —";

	// ── HRV dump ─────────────────────────────────────────────────────────────

	public string RmssdText => _sample is { } s ? $"RMSSD: {s.Rmssd:0.0} ms" : "RMSSD: —";
	public string Pnn50Text => _sample is { } s ? $"pNN50: {s.Pnn50:0.0} %" : "pNN50: —";
	public string MeanHrText => _sample is { } s ? $"Mean HR: {s.MeanHr:0.0} bpm" : "Mean HR: —";
	public string SdnnText => _sample?.Extended is { } e ? $"SDNN: {e.Sdnn:0.0} ms" : "SDNN: —";

	public string LfHfText => _sample?.Extended is { } e
		? $"LF/HF: {e.LfHfRatio:0.00}  (LF {e.LfPowerMs2:0} · HF {e.HfPowerMs2:0} ms²)"
		: "LF/HF: —";

	public string PoincareText => _sample?.Extended is { } e
		? $"Poincaré: SD1 {e.SD1:0.0} · SD2 {e.SD2:0.0} · SD1/SD2 {e.SD1SD2Ratio:0.00}"
		: "Poincaré: —";

	public string BaselineText => _sample is { } s
		? $"Baseline: RMSSD {s.BaselineRmssd:0.0} · HR {s.BaselineHr:0.0} · LF/HF {s.BaselineLfHfRatio:0.00}"
		: "Baseline: —";

	public string WarmupText => _pipeline is { } p
		? $"Warm-up: {p.BaselineWarmUpProgress:P0} · {(p.IsBaselineWarm ? "warm" : "calibrating")}{(p.IsColdCalibrated ? " · cold-cal" : string.Empty)}"
		: "Warm-up: —";

	public string StateText => _pipeline is { } p
		? $"State: {p.CurrentState} · hypo {p.CurrentHypoarousalState}"
		: "State: —";

	// ── Movement / ECG / connection ──────────────────────────────────────────

	public string MovementText => _movement is { } m
		? $"Movement: {m.Level} · {m.IntensityG:0.000} g · src {m.Source?.ToString() ?? "none"}"
		: "Movement: —";

	public string EcgText => _ecg.MicroVolts.Count == 0
		? "ECG: not streaming"
		: $"ECG: {_ecg.Quality} · {_ecg.SampleRateHz:0} Hz · {_ecg.MicroVolts.Count} samples · {_ecg.RPeakIndices.Count} peaks · [{_ecg.MinMicroVolts}, {_ecg.MaxMicroVolts}] µV";

	public string ContactText => $"Contact: {_contact}";
	public string BatteryText => _battery is { } b ? $"Battery: {b} %" : "Battery: —";

	public string DeviceText => _device is { } d
		? $"Device: {Join(d.ManufacturerName, d.ModelNumber)} · fw {d.FirmwareRevision ?? "—"} · sn {d.SerialNumber ?? "—"}"
		: "Device: —";

	/// <summary>Subscribes to every diagnostic stream the pipeline exposes. Public so tests can drive it.</summary>
	public void AttachPipeline(Pipeline pipeline)
	{
		ArgumentNullException.ThrowIfNull(pipeline);
		_pipeline = pipeline;
		pipeline.BeatDiagnosticReceived += OnBeatDiagnostic;
		pipeline.SampleUpdated += OnSampleUpdated;
		pipeline.MovementUpdated += OnMovementUpdated;
		pipeline.EcgUpdated += OnEcgUpdated;
		pipeline.ContactChanged += OnContactChanged;
		pipeline.BatteryUpdated += OnBatteryUpdated;
		pipeline.DeviceInfoUpdated += OnDeviceInfoUpdated;

		// Battery and device info are one-shot reads that may have already landed on the pipeline
		// before this view model attached (the iOS central manager connects early, before the UI
		// exists). Seed from the pipeline's latched values so the Debug tab doesn't show a stale
		// "—" for a sensor that already reported them — the same convergence NowViewModel does.
		if (pipeline.LatestBatteryPercent is { } percent)
		{
			OnBatteryUpdated(new BatteryReading(DateTimeOffset.UtcNow, percent));
		}

		if (pipeline.LatestDeviceInfo is { } info)
		{
			OnDeviceInfoUpdated(info);
		}
	}

	public void OnBeatDiagnostic(BeatDiagnostic diagnostic) => RunOnUi(() =>
	{
		_aggregator.Add(diagnostic);
		_snapshot = _aggregator.Snapshot();
		RaiseAll();
	});

	public void OnSampleUpdated(HrvSample sample) => RunOnUi(() =>
	{
		_sample = sample;
		RaiseAll();
	});

	public void OnMovementUpdated(MovementSnapshot movement) => RunOnUi(() =>
	{
		_movement = movement;
		Raise(nameof(MovementText));
	});

	public void OnEcgUpdated(EcgWaveformSnapshot ecg) => RunOnUi(() =>
	{
		_ecg = ecg;
		Raise(nameof(EcgText));
	});

	public void OnContactChanged(SensorContactStatus contact) => RunOnUi(() =>
	{
		_contact = contact;
		Raise(nameof(ContactText));
	});

	public void OnBatteryUpdated(BatteryReading reading) => RunOnUi(() =>
	{
		_battery = reading.Percent;
		Raise(nameof(BatteryText));
	});

	public void OnDeviceInfoUpdated(DeviceInformation info) => RunOnUi(() =>
	{
		_device = info;
		Raise(nameof(DeviceText));
	});

	private SourceDiagnostics? Src(IntervalSource source) =>
		_snapshot.Sources.FirstOrDefault(s => s.Source == source);

	private static string Summary(string label, SourceDiagnostics? d) => d is null
		? $"{label}: —"
		: $"{label}: {d.LatestRrMs:0} ms · {d.LatestBpm} bpm · mean {d.MeanRrMs:0} · med {d.MedianRrMs:0} · sdnn {d.SdnnMs:0} · n {d.Count} · art {d.ArtifactRate:P1}";

	private static string Recent(SourceDiagnostics? d) =>
		d is null || d.RecentRrMs.Count == 0
			? "—"
			: string.Join(", ", d.RecentRrMs.TakeLast(12).Select(v => v.ToString("0")));

	private static string Label(IntervalSource source) => source switch
	{
		IntervalSource.HeartRateService => "HRS",
		IntervalSource.PolarPpi => "PPI",
		IntervalSource.PolarEcg => "ECG",
		_ => source.ToString(),
	};

	private static string Join(string? a, string? b) =>
		string.Join(" ", new[] { a, b }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } s ? s : "—";

	private void RaiseAll()
	{
		Raise(nameof(AbBiasText));
		Raise(nameof(SourceText));
		Raise(nameof(HrsSummary));
		Raise(nameof(EcgSummary));
		Raise(nameof(PpiSummary));
		Raise(nameof(HasPpi));
		Raise(nameof(HrsRecentRr));
		Raise(nameof(EcgRecentRr));
		Raise(nameof(ConsensusText));
		Raise(nameof(RmssdText));
		Raise(nameof(Pnn50Text));
		Raise(nameof(MeanHrText));
		Raise(nameof(SdnnText));
		Raise(nameof(LfHfText));
		Raise(nameof(PoincareText));
		Raise(nameof(BaselineText));
		Raise(nameof(WarmupText));
		Raise(nameof(StateText));
	}

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
}
