using Hexa.NET.ImGui;
using Hexa.NET.ImPlot;
using ktsu.Containers;
using ktsu.ImGui.Widgets;
using ktsu.ImGui.App;
using ktsu.IntervalAction;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;
using System.Numerics;

namespace MeltdownMonitor.App;

/// <summary>
/// Optional status window: live HRV / HR / frequency-domain / Poincaré visualizations
/// plus a settings tab. Toggled from the tray menu.
/// </summary>
public sealed class StatusWindow : IDisposable
{
	private const int PoincareScatterPoints = 256;
	private const int PlotHeight = 170;
	private const int InitialSparklineCapacity = 1024;

	private readonly object _historyLock = new();
	private readonly Pipeline _pipeline;
	private readonly MeltdownRepository _repository;
	private readonly AppSettings _settings;
	private readonly IntervalAction _historyRefreshAction;
	private readonly ImGuiWidgets.TabPanel _tabs;
	private readonly Action? _onClosed;
	private Thread? _uiThread;
	private int _appliedCapacity = InitialSparklineCapacity;
	private int _subscriptionsReleased;
	private bool _settingsDirty;

	private readonly RingBuffer<float> _rmssd = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _baselineRmssd = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _pnn50 = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _sdnn = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _meanHr = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _baselineHr = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _lfPower = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _hfPower = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _lfHfRatio = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _baselineLfHf = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _sd1 = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _sd2 = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _sd1Sd2 = new(InitialSparklineCapacity);
	private readonly RingBuffer<double> _recentRr = new(PoincareScatterPoints);

	private RingBuffer<float>[] AllSparklines => [
		_rmssd, _baselineRmssd, _pnn50, _sdnn,
		_meanHr, _baselineHr,
		_lfPower, _hfPower, _lfHfRatio, _baselineLfHf,
		_sd1, _sd2, _sd1Sd2,
	];

	/// <param name="onClosed">
	/// Invoked once when the render loop exits — whether the user closed the window
	/// directly or <see cref="Close"/> was called. Lets the owner drop its reference
	/// so the window can be reopened.
	/// </param>
	public StatusWindow(Pipeline pipeline, MeltdownRepository repository, AppSettings settings, Action? onClosed = null)
	{
		_pipeline = pipeline;
		_repository = repository;
		_settings = settings;
		_onClosed = onClosed;

		_pipeline.SampleUpdated += OnSampleUpdated;
		_pipeline.BeatReceived += OnBeatReceived;

		_tabs = new ImGuiWidgets.TabPanel("status-tabs");
		_tabs.AddTab("Overview", DrawOverviewTab);
		_tabs.AddTab("Heart Rate", DrawHeartRateTab);
		_tabs.AddTab("Time-Domain HRV", DrawTimeDomainTab);
		_tabs.AddTab("Frequency-Domain", DrawFrequencyTab);
		_tabs.AddTab("Poincaré", DrawPoincareTab);
		_tabs.AddTab("Annotations", DrawAnnotationsTab);
		_tabs.AddTab("Settings", DrawSettingsTab);

		// Fires immediately on first poll, then periodically — backfills sparklines
		// with persisted history so they aren't empty when the window opens.
		_historyRefreshAction = IntervalAction.Start(new IntervalActionOptions
		{
			Action = BackfillFromRepository,
			ActionInterval = TimeSpan.FromMinutes(5),
			PollingInterval = TimeSpan.FromSeconds(10),
		});
	}

	public void Run()
	{
		_uiThread = new Thread(() =>
		{
			try
			{
				ImGuiApp.Start(new ImGuiAppConfig
				{
					Title = "Meltdown Monitor — Status",
					InitialWindowState = new ImGuiAppWindowState
					{
						Size = new Vector2(960, 640),
					},
					OnRender = SafeRender,
				});
			}
			catch (Exception ex)
			{
				LogException("ImGuiApp.Start", ex);
			}
			finally
			{
				// ImGuiApp.Start blocks until the window closes (by the user's close
				// button or by Close()). Either way, detach from the pipeline and let
				// the owner clear its reference so the window can be reopened.
				ReleaseSubscriptions();
				_onClosed?.Invoke();
			}
		})
		{
			IsBackground = true,
			Name = "MeltdownMonitor-StatusWindow",
		};
		_uiThread.Start();
	}

	private void SafeRender(float deltaSeconds)
	{
		try
		{
			OnRender(deltaSeconds);
		}
		catch (Exception ex)
		{
			LogException("OnRender", ex);
			throw;
		}
	}

	private static void LogException(string where, Exception ex)
	{
		try
		{
			string path = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"MeltdownMonitor",
				"status-window-crash.log");
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.AppendAllText(path,
				$"[{DateTimeOffset.Now:O}] {where}: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
		}
		catch
		{
			// Logging is best-effort.
		}
	}

	public void Close()
	{
		try
		{
			ImGuiApp.Stop();
		}
		catch
		{
			// Stop may throw if the window was never fully started.
		}

		_uiThread?.Join(TimeSpan.FromSeconds(2));
		_uiThread = null;
	}

	private int DesiredCapacity =>
		Math.Max(60, (int)(_settings.SparklineWindowMinutes * 60.0 / Math.Max(0.5, _settings.HrvEmitIntervalSeconds)));

	private void ApplyCapacityIfChanged()
	{
		int desired = DesiredCapacity;
		if (desired == _appliedCapacity)
		{
			return;
		}

		// Resample preserves the existing curve when the user changes the window.
		foreach (var rb in AllSparklines)
		{
			rb.Resample(desired);
		}
		_appliedCapacity = desired;
	}

	private void OnSampleUpdated(HrvSample sample)
	{
		lock (_historyLock)
		{
			ApplyCapacityIfChanged();

			_rmssd.PushBack((float)sample.Rmssd);
			_baselineRmssd.PushBack((float)sample.BaselineRmssd);
			_pnn50.PushBack((float)sample.Pnn50);
			_meanHr.PushBack((float)sample.MeanHr);
			_baselineHr.PushBack((float)sample.BaselineHr);
			_baselineLfHf.PushBack((float)sample.BaselineLfHfRatio);

			if (sample.Extended is { } ext)
			{
				_sdnn.PushBack((float)ext.Sdnn);
				_lfPower.PushBack((float)ext.LfPowerMs2);
				_hfPower.PushBack((float)ext.HfPowerMs2);
				_lfHfRatio.PushBack((float)ext.LfHfRatio);
				_sd1.PushBack((float)ext.SD1);
				_sd2.PushBack((float)ext.SD2);
				_sd1Sd2.PushBack((float)ext.SD1SD2Ratio);
			}
		}
	}

	private void OnBeatReceived(Beat beat)
	{
		if (beat.IsArtifact)
		{
			return;
		}

		lock (_historyLock)
		{
			_recentRr.PushBack(beat.RrMs);
		}
	}

	private void BackfillFromRepository()
	{
		var to = DateTimeOffset.UtcNow;
		var from = to.AddMinutes(-_settings.SparklineWindowMinutes);

		IReadOnlyList<HrvSample> samples;
		try
		{
			samples = MeltdownRepository.ReadHistory(_settings.DatabasePath, from, to);
		}
		catch
		{
			return;
		}

		lock (_historyLock)
		{
			int desired = DesiredCapacity;
			foreach (var rb in AllSparklines)
			{
				rb.Resize(desired);
			}
			_appliedCapacity = desired;

			foreach (var s in samples.TakeLast(desired))
			{
				_rmssd.PushBack((float)s.Rmssd);
				_baselineRmssd.PushBack((float)s.BaselineRmssd);
				_pnn50.PushBack((float)s.Pnn50);
				_meanHr.PushBack((float)s.MeanHr);
				_baselineHr.PushBack((float)s.BaselineHr);
				_baselineLfHf.PushBack((float)s.BaselineLfHfRatio);

				if (s.Extended is { } ext)
				{
					_sdnn.PushBack((float)ext.Sdnn);
					_lfPower.PushBack((float)ext.LfPowerMs2);
					_hfPower.PushBack((float)ext.HfPowerMs2);
					_lfHfRatio.PushBack((float)ext.LfHfRatio);
					_sd1.PushBack((float)ext.SD1);
					_sd2.PushBack((float)ext.SD2);
					_sd1Sd2.PushBack((float)ext.SD1SD2Ratio);
				}
			}
		}
	}

	private static float[] SnapshotF(RingBuffer<float> rb)
	{
		var arr = new float[rb.Count];
		for (int i = 0; i < rb.Count; i++)
		{
			arr[i] = rb.At(i);
		}

		return arr;
	}

	private static double[] SnapshotD(RingBuffer<double> rb)
	{
		var arr = new double[rb.Count];
		for (int i = 0; i < rb.Count; i++)
		{
			arr[i] = rb.At(i);
		}

		return arr;
	}

	private void OnRender(float deltaSeconds)
	{
		DrawStatusHeader();
		ImGui.Separator();
		_tabs.Draw();
	}

	private void DrawStatusHeader()
	{
		var state = _pipeline.CurrentState;
		ImGuiWidgets.ColorIndicator(StateColor(state), enabled: true);
		ImGui.SameLine();
		ImGui.Text($"State: {state}");

		var latest = _pipeline.LatestSample;
		if (latest is not null)
		{
			ImGui.SameLine();
			ImGui.Text("   |");
			ImGui.SameLine();
			ImGui.Text($"RMSSD {latest.Rmssd:F1} ms");
			ImGui.SameLine();
			ImGui.TextDisabled($"(base {latest.BaselineRmssd:F1})");
			ImGui.SameLine();
			ImGui.Text($"   HR {latest.MeanHr:F0} bpm");
			ImGui.SameLine();
			ImGui.TextDisabled($"(base {latest.BaselineHr:F0})");

			if (latest.Extended is { } ext)
			{
				ImGui.SameLine();
				ImGui.Text($"   LF/HF {ext.LfHfRatio:F2}");
			}
		}
	}

	private void DrawOverviewTab()
	{
		float warmUp = (float)_pipeline.Baseline.WarmUpProgress;
		ImGui.Text("Baseline warm-up");
		ImGuiWidgets.RadialProgressBar(warmUp, radius: 48, thickness: 8);
		ImGui.SameLine();
		ImGui.BeginGroup();
		ImGui.TextDisabled(_pipeline.Baseline.IsWarm
			? "Baseline is warm — detector active."
			: "10-minute warm-up needed before the detector arms.");
		var latest = _pipeline.LatestSample;
		if (latest is not null)
		{
			ImGui.Text($"Last sample: {latest.Timestamp.LocalDateTime:HH:mm:ss}");
			double drop = latest.BaselineRmssd > 0
				? (latest.BaselineRmssd - latest.Rmssd) / latest.BaselineRmssd
				: 0.0;
			double rise = latest.BaselineHr > 0
				? (latest.MeanHr - latest.BaselineHr) / latest.BaselineHr
				: 0.0;
			ImGui.Text($"RMSSD vs baseline: {drop * 100:+0.0;-0.0;0.0}%");
			ImGui.Text($"HR vs baseline:    {rise * 100:+0.0;-0.0;0.0}%");
		}
		ImGui.EndGroup();

		ImGui.Separator();

		float[] rmssd; float[] baseRmssd;
		lock (_historyLock)
		{
			rmssd = SnapshotF(_rmssd);
			baseRmssd = SnapshotF(_baselineRmssd);
		}

		PlotPair("RMSSD vs baseline (ms)", "RMSSD", rmssd, "Baseline", baseRmssd);
	}

	private void DrawHeartRateTab()
	{
		float[] hr; float[] baseHr; double[] rrsD;
		lock (_historyLock)
		{
			hr = SnapshotF(_meanHr);
			baseHr = SnapshotF(_baselineHr);
			rrsD = SnapshotD(_recentRr);
		}

		PlotPair("Heart rate vs baseline (bpm)", "HR", hr, "Baseline HR", baseHr);

		ImGui.Separator();
		float[] rrs = new float[rrsD.Length];
		for (int i = 0; i < rrsD.Length; i++)
		{
			rrs[i] = (float)rrsD[i];
		}

		Plot("RR intervals (ms, last received beats)", rrs, FullWidth(PlotHeight));
	}

	private void DrawTimeDomainTab()
	{
		float[] rmssd; float[] baseRmssd; float[] pnn50; float[] sdnn;
		lock (_historyLock)
		{
			rmssd = SnapshotF(_rmssd);
			baseRmssd = SnapshotF(_baselineRmssd);
			pnn50 = SnapshotF(_pnn50);
			sdnn = SnapshotF(_sdnn);
		}

		PlotPair("RMSSD (ms)", "RMSSD", rmssd, "Baseline", baseRmssd);
		Plot("pNN50 (%)", pnn50, FullWidth(PlotHeight));
		Plot("SDNN (ms)", sdnn, FullWidth(PlotHeight));
	}

	private void DrawFrequencyTab()
	{
		float[] lf; float[] hf; float[] ratio; float[] baseRatio;
		lock (_historyLock)
		{
			lf = SnapshotF(_lfPower);
			hf = SnapshotF(_hfPower);
			ratio = SnapshotF(_lfHfRatio);
			baseRatio = SnapshotF(_baselineLfHf);
		}

		PlotPair("LF/HF ratio (sympathovagal balance)", "LF/HF", ratio, "Baseline LF/HF", baseRatio);
		Plot("LF power (ms², 0.04–0.15 Hz)", lf, FullWidth(PlotHeight));
		Plot("HF power (ms², 0.15–0.40 Hz)", hf, FullWidth(PlotHeight));

		if (ratio.Length < 2)
		{
			ImGui.TextDisabled("Frequency metrics need ≥2 minutes of clean beats to populate.");
		}
	}

	private void DrawPoincareTab()
	{
		float[] sd1; float[] sd2; float[] ratio; double[] rrsD;
		lock (_historyLock)
		{
			sd1 = SnapshotF(_sd1);
			sd2 = SnapshotF(_sd2);
			ratio = SnapshotF(_sd1Sd2);
			rrsD = SnapshotD(_recentRr);
		}

		float[] rrs = new float[rrsD.Length];
		for (int i = 0; i < rrsD.Length; i++)
		{
			rrs[i] = (float)rrsD[i];
		}

		DrawPoincareScatter(rrs);

		Plot("SD1 (short-term variability, ms)", sd1, FullWidth(PlotHeight));
		Plot("SD2 (long-term variability, ms)", sd2, FullWidth(PlotHeight));
		Plot("SD1/SD2 ratio (parasympathetic index)", ratio, FullWidth(PlotHeight));
	}

	private static void DrawPoincareScatter(float[] rrs)
	{
		Vector2 size = FullWidth(340);
		if (rrs.Length < 2)
		{
			ImGui.TextDisabled("Poincaré plot: (needs ≥2 beats)");
			ImGui.Dummy(size);
			return;
		}

		// Equal keeps the axes square so the identity line reads at 45°.
		if (ImPlot.BeginPlot("Poincaré (RR[i] vs RR[i+1])", size,
				ImPlotFlags.Equal | ImPlotFlags.NoLegend | ImPlotFlags.NoMouseText))
		{
			ImPlot.SetupAxes("RR[i] (ms)", "RR[i+1] (ms)",
				ImPlotAxisFlags.AutoFit, ImPlotAxisFlags.AutoFit);

			// Identity line RR[i] = RR[i+1] — the cloud clusters along it.
			float[] diagonal = [rrs.Min(), rrs.Max()];
			ImPlot.SetNextLineStyle(new Vector4(0.55f, 0.55f, 0.55f, 0.40f), 1f);
			ImPlot.PlotLine("identity", ref diagonal[0], ref diagonal[0], diagonal.Length);

			// Offset trick: consecutive pairs (rrs[k], rrs[k+1]) without a second array.
			ImPlot.SetNextMarkerStyle(ImPlotMarker.Circle, 2.5f,
				new Vector4(0.40f, 0.80f, 1.00f, 0.85f), 1f);
			ImPlot.PlotScatter("RR", ref rrs[0], ref rrs[1], rrs.Length - 1);

			ImPlot.EndPlot();
		}
	}

	private void DrawAnnotationsTab()
	{
		ImGui.Text("Tag this moment with how you're feeling:");
		ImGui.Spacing();

		foreach (AnnotationLabel label in Enum.GetValues<AnnotationLabel>())
		{
			if (ImGui.Button(label.ToString(), new Vector2(140, 0)))
			{
				_repository.InsertAnnotation(DateTimeOffset.UtcNow, label);
			}

			ImGui.SameLine();
		}

		ImGui.NewLine();
		ImGui.TextDisabled("Annotations are stored alongside HRV samples for later review.");
	}

	private void DrawSettingsTab()
	{
		ImGui.TextDisabled("Changes apply live; persisted to disk when you release the control.");
		ImGui.Spacing();

		// ── Refresh / window ─────────────────────────────────────────────
		ImGui.SeparatorText("Refresh");

		float emit = (float)_settings.HrvEmitIntervalSeconds;
		if (ImGuiWidgets.Knob("HRV emit (s)", ref emit, 0.5f, 30f, format: "%.1f s",
				flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.HrvEmitIntervalSeconds = emit;
			_settingsDirty = true;
		}
		ImGui.SameLine();

		int window = _settings.SparklineWindowMinutes;
		if (ImGuiWidgets.Knob("Window (min)", ref window, 1, 360, format: "%d min",
				flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.SparklineWindowMinutes = window;
			_settingsDirty = true;
		}

		// ── Detection thresholds ─────────────────────────────────────────
		// Fraction knobs work in percent (0..100) and divide on assign so the
		// %% formatter renders sane values; the underlying field stays a fraction.
		ImGui.SeparatorText("Detection thresholds");

		var t = _settings.Thresholds;
		float rmssdWarnPct = (float)(t.RmssdWarningDropFraction * 100.0);
		float hrRisePct = (float)(t.HrWarningRiseFraction * 100.0);
		float rmssdAlertPct = (float)(t.RmssdAlertingDropFraction * 100.0);

		if (ImGuiWidgets.Knob("RMSSD warn drop", ref rmssdWarnPct, 5f, 90f, format: "%.0f%%",
				flags: ImGuiKnobOptions.ValueTooltip))
		{
			t = t with { RmssdWarningDropFraction = rmssdWarnPct / 100.0 };
			_settingsDirty = true;
		}
		ImGui.SameLine();

		if (ImGuiWidgets.Knob("HR rise", ref hrRisePct, 5f, 80f, format: "%.0f%%",
				flags: ImGuiKnobOptions.ValueTooltip))
		{
			t = t with { HrWarningRiseFraction = hrRisePct / 100.0 };
			_settingsDirty = true;
		}
		ImGui.SameLine();

		if (ImGuiWidgets.Knob("RMSSD alert drop", ref rmssdAlertPct, 5f, 95f, format: "%.0f%%",
				flags: ImGuiKnobOptions.ValueTooltip))
		{
			t = t with { RmssdAlertingDropFraction = rmssdAlertPct / 100.0 };
			_settingsDirty = true;
		}

		float holdSec = (float)t.WarningHoldDuration.TotalSeconds;
		float escalateSec = (float)t.AlertingEscalationDuration.TotalSeconds;
		float cooldownMin = (float)t.CooldownDuration.TotalMinutes;

		if (ImGuiWidgets.Knob("Warning hold (s)", ref holdSec, 5f, 300f, format: "%.0f s",
				flags: ImGuiKnobOptions.ValueTooltip))
		{
			t = t with { WarningHoldDuration = TimeSpan.FromSeconds(holdSec) };
			_settingsDirty = true;
		}
		ImGui.SameLine();

		if (ImGuiWidgets.Knob("Escalate (s)", ref escalateSec, 10f, 600f, format: "%.0f s",
				flags: ImGuiKnobOptions.ValueTooltip))
		{
			t = t with { AlertingEscalationDuration = TimeSpan.FromSeconds(escalateSec) };
			_settingsDirty = true;
		}
		ImGui.SameLine();

		if (ImGuiWidgets.Knob("Cooldown (min)", ref cooldownMin, 1f, 60f, format: "%.0f min",
				flags: ImGuiKnobOptions.ValueTooltip))
		{
			t = t with { CooldownDuration = TimeSpan.FromMinutes(cooldownMin) };
			_settingsDirty = true;
		}

		// ── LF/HF corroboration ─────────────────────────────────────────
		ImGui.SeparatorText("LF/HF corroboration (optional)");

		bool useLfHf = t.UseLfHfCorroboration;
		if (ImGui.Checkbox("Require LF/HF to also rise before Warning", ref useLfHf))
		{
			t = t with { UseLfHfCorroboration = useLfHf };
			_settingsDirty = true;
		}

		using (new ScopedDisable(!useLfHf))
		{
			float lfHfRisePct = (float)(t.LfHfWarningRiseFraction * 100.0);
			if (ImGuiWidgets.Knob("LF/HF rise", ref lfHfRisePct, 5f, 200f, format: "%.0f%%",
					flags: ImGuiKnobOptions.ValueTooltip))
			{
				t = t with { LfHfWarningRiseFraction = lfHfRisePct / 100.0 };
				_settingsDirty = true;
			}
		}

		// Apply changes to the live settings on every frame (so the Func<>
		// provider sees them immediately), but defer the disk write until
		// no widget is actively being dragged — otherwise we'd rewrite the
		// settings file 30+ times per second.
		_settings.Thresholds = t;

		if (_settingsDirty && !ImGui.IsAnyItemActive())
		{
			_settings.Save();
			_settingsDirty = false;
		}
	}

	private static void Plot(string title, float[] data, Vector2 size)
	{
		if (data.Length < 2)
		{
			ImGui.TextDisabled($"{title}: (waiting for data)");
			ImGui.Dummy(size);
			return;
		}

		// X is just the sample index, so hide its tick labels; ImPlot auto-fits Y.
		if (ImPlot.BeginPlot(title, size, ImPlotFlags.NoLegend | ImPlotFlags.NoMouseText))
		{
			ImPlot.SetupAxes(string.Empty, string.Empty,
				ImPlotAxisFlags.AutoFit | ImPlotAxisFlags.NoTickLabels, ImPlotAxisFlags.AutoFit);
			ImPlot.PlotLine(title, ref data[0], data.Length);
			ImPlot.EndPlot();
		}
	}

	private static void PlotPair(string title, string aLabel, float[] a, string bLabel, float[] b)
	{
		Vector2 size = FullWidth(PlotHeight);

		if (a.Length < 2 && b.Length < 2)
		{
			ImGui.TextDisabled($"{title}: (waiting for data)");
			ImGui.Dummy(size);
			return;
		}

		// Both series share the plot's auto-fit Y axis, so they stay on a common scale.
		if (ImPlot.BeginPlot(title, size, ImPlotFlags.NoMouseText))
		{
			ImPlot.SetupAxes(string.Empty, string.Empty,
				ImPlotAxisFlags.AutoFit | ImPlotAxisFlags.NoTickLabels, ImPlotAxisFlags.AutoFit);

			if (a.Length >= 2)
			{
				ImPlot.PlotLine(aLabel, ref a[0], a.Length);
			}
			if (b.Length >= 2)
			{
				ImPlot.PlotLine(bLabel, ref b[0], b.Length);
			}

			ImPlot.EndPlot();
		}
	}

	private static Vector2 FullWidth(float height) =>
		new(ImGui.GetContentRegionAvail().X, height);

	private static ImColor StateColor(DetectorState state)
	{
		Vector4 v = state switch
		{
			DetectorState.Idle      => new Vector4(0.55f, 0.55f, 0.55f, 1f),
			DetectorState.Watching  => new Vector4(0.30f, 0.75f, 0.45f, 1f),
			DetectorState.Warning   => new Vector4(0.95f, 0.75f, 0.20f, 1f),
			DetectorState.Alerting  => new Vector4(0.95f, 0.30f, 0.25f, 1f),
			DetectorState.Cooldown  => new Vector4(0.45f, 0.55f, 0.85f, 1f),
			_                       => new Vector4(0.5f, 0.5f, 0.5f, 1f),
		};
		return new ImColor { Value = v };
	}

	// Detach from the pipeline and stop the backfill timer exactly once. Safe to
	// call from either the UI thread (loop exit) or the owner thread (Dispose).
	private void ReleaseSubscriptions()
	{
		if (Interlocked.Exchange(ref _subscriptionsReleased, 1) != 0)
		{
			return;
		}

		_pipeline.SampleUpdated -= OnSampleUpdated;
		_pipeline.BeatReceived -= OnBeatReceived;
		_historyRefreshAction.Stop();
	}

	public void Dispose()
	{
		Close();
		ReleaseSubscriptions();
	}
}
