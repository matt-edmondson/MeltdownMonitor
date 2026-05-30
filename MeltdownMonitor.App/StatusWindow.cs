using Hexa.NET.ImGui;
using Hexa.NET.ImPlot;
using ktsu.Containers;
using ktsu.ImGui.Widgets;
using ktsu.ImGui.App;
using ktsu.IntervalAction;
using MeltdownMonitor.Core.Baseline;
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
	private const int PoincareScatterPoints = 512;
	private const int InitialSparklineCapacity = 2048;

	// Chart layout, tunable from the Settings tab (applied live).
	private float PlotHeight => _settings.ChartTuning.PlotHeight;
	private float OverviewChartWidth => _settings.ChartTuning.OverviewChartWidth;
	private float MaxPlotAspect => _settings.ChartTuning.MaxPlotAspect;
	private float PoincareMaxSide => _settings.ChartTuning.PoincareMaxSide;

	private readonly object _historyLock = new();
	private readonly Pipeline _pipeline;
	private readonly MeltdownRepository _repository;
	private readonly AppSettings _settings;
	private readonly IntervalAction _historyRefreshAction;
	private readonly ImGuiWidgets.TabPanel _tabs;
	private readonly MetricsOverlay _overlay = new();
	private readonly StatusTheme _theme = new();
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
	private readonly RingBuffer<double> _recentRr = new(InitialSparklineCapacity);

	private RingBuffer<float>[] AllSparklines => [
		_rmssd, _baselineRmssd, _pnn50, _sdnn,
		_meanHr, _baselineHr,
		_lfPower, _hfPower, _lfHfRatio, _baselineLfHf,
		_sd1, _sd2, _sd1Sd2,
	];

	public StatusWindow(Pipeline pipeline, MeltdownRepository repository, AppSettings settings)
	{
		_pipeline = pipeline;
		_repository = repository;
		_settings = settings;

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

	/// <summary>
	/// Starts the render loop on a background thread. The window is created hidden and
	/// stays alive for the application lifetime; use <see cref="ToggleVisibility"/> to
	/// show or hide it. Closing the window from its title bar hides it rather than
	/// ending the loop, so it can always be shown again.
	/// </summary>
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
					StartHidden = true,
					HideOnClose = true,
					OnRender = SafeRender,
				});
			}
			catch (Exception ex)
			{
				LogException("ImGuiApp.Start", ex);
			}
			finally
			{
				// Start blocks until the loop ends (only at application shutdown, since
				// the close button hides instead). Detach from the pipeline regardless.
				ReleaseSubscriptions();
			}
		})
		{
			IsBackground = true,
			Name = "MeltdownMonitor-StatusWindow",
		};
		_uiThread.Start();
	}

	/// <summary>Shows the window if hidden, otherwise hides it. Safe to call from any thread.</summary>
	public void ToggleVisibility()
	{
		if (ImGuiApp.IsVisible)
		{
			ImGuiApp.Hide();
		}
		else
		{
			ImGuiApp.Show();
		}
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
		// Re-tint the Catppuccin Macchiato theme to match the live detection state.
		// Applied before any widgets so the accent change takes effect this frame.
		_theme.Apply(_pipeline.CurrentState);

		// Pad the auto-fit so series don't hug the plot edges (fraction of the
		// data range on each axis). Persisted in the ImPlot context; set each
		// frame to stay robust against any style reset.
		ImPlot.GetStyle().FitPadding = new Vector2(0.08f, 0.12f);

		DrawStatusHeader();
		ImGui.Separator();
		_tabs.Draw();

		// Drawn last so it floats above the tab content. Persist context-menu changes,
		// deferring the disk write while a menu interaction is in flight.
		var overlaySample = new OverlaySample(
			_pipeline.CurrentState,
			_pipeline.LatestSample,
			_pipeline.Baseline.WarmUpProgress);
		if (_overlay.Draw(overlaySample, _settings.Overlay))
		{
			_settingsDirty = true;
		}

		if (_settingsDirty && !ImGui.IsAnyItemActive())
		{
			_settings.Save();
			_settingsDirty = false;
		}
	}

	/// <summary>
	/// Shows or hides the transparent metrics overlay. The overlay is drawn inside the
	/// status window, so enabling it also reveals the window if it was hidden. Safe to
	/// call from any thread.
	/// </summary>
	public void ToggleOverlay()
	{
		bool enabled = !_settings.Overlay.Enabled;
		_settings.Overlay.Enabled = enabled;
		_settings.Save();

		if (enabled && !ImGuiApp.IsVisible)
		{
			ImGuiApp.Show();
		}
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

		float[] rmssd, baseRmssd, pnn50, sdnn, hr, baseHr, lf, hf, lfhf, baseLfhf, sd1, sd2, sd1sd2;
		double[] rrsD;
		lock (_historyLock)
		{
			rmssd = SnapshotF(_rmssd);
			baseRmssd = SnapshotF(_baselineRmssd);
			pnn50 = SnapshotF(_pnn50);
			sdnn = SnapshotF(_sdnn);
			hr = SnapshotF(_meanHr);
			baseHr = SnapshotF(_baselineHr);
			lf = SnapshotF(_lfPower);
			hf = SnapshotF(_hfPower);
			lfhf = SnapshotF(_lfHfRatio);
			baseLfhf = SnapshotF(_baselineLfHf);
			sd1 = SnapshotF(_sd1);
			sd2 = SnapshotF(_sd2);
			sd1sd2 = SnapshotF(_sd1Sd2);
			rrsD = SnapshotD(_recentRr);
		}

		float[] rr = new float[rrsD.Length];
		for (int i = 0; i < rrsD.Length; i++)
		{
			rr[i] = (float)rrsD[i];
		}

		// Every metric at full chart size, laid out with the ktsu.ImGui.Widgets grid,
		// which fits as many columns as the window width allows. Baselines overlay
		// where available; the Poincaré scatter is included as a square cell.
		OverviewChart[] charts =
		[
			new("RMSSD vs baseline (ms)", rmssd, baseRmssd),
			new("Heart rate vs baseline (bpm)", hr, baseHr),
			new("LF/HF ratio (sympathovagal balance)", lfhf, baseLfhf),
			new("pNN50 (%)", pnn50, null),
			new("SDNN (ms)", sdnn, null),
			new("LF power (ms²)", lf, null),
			new("HF power (ms²)", hf, null),
			new("SD1 (ms)", sd1, null),
			new("SD2 (ms)", sd2, null),
			new("SD1/SD2 ratio (parasympathetic index)", sd1sd2, null),
			new("RR intervals (ms)", rr, null),
			new("Poincaré (RR[i] vs RR[i+1])", rr, null, IsScatter: true),
		];

		ImGuiWidgets.RowMajorGrid("overview-charts", charts,
			_ => new Vector2(OverviewChartWidth, PlotHeight),
			static (chart, cellSize, itemSize) => DrawOverviewChart(chart, itemSize));
	}

	private sealed record OverviewChart(string Title, float[] Data, float[]? Baseline, bool IsScatter = false);

	private static void DrawOverviewChart(OverviewChart chart, Vector2 size)
	{
		if (chart.IsScatter)
		{
			DrawScatterPlot(chart.Title, chart.Data, size);
			return;
		}

		ImPlotFlags flags = chart.Baseline is null
			? ImPlotFlags.NoMouseText | ImPlotFlags.NoLegend
			: ImPlotFlags.NoMouseText;

		if (ImPlot.BeginPlot(chart.Title, size, flags))
		{
			ImPlot.SetupAxes(string.Empty, string.Empty,
				ImPlotAxisFlags.AutoFit | ImPlotAxisFlags.NoTickLabels, ImPlotAxisFlags.AutoFit);

			if (chart.Baseline is { Length: >= 2 } baseline)
			{
				ImPlot.PlotLine("baseline", ref baseline[0], baseline.Length);
			}
			if (chart.Data.Length >= 2)
			{
				ImPlot.PlotLine(chart.Title, ref chart.Data[0], chart.Data.Length);
			}

			ImPlot.EndPlot();
		}
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

		PlotRow(PlotHeight, ("RR intervals (ms, last received beats)", rrs));
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
		PlotRow(PlotHeight, ("pNN50 (%)", pnn50), ("SDNN (ms)", sdnn));
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
		PlotRow(PlotHeight,
			("LF power (ms², 0.04–0.15 Hz)", lf),
			("HF power (ms², 0.15–0.40 Hz)", hf));

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

		PlotRow(PlotHeight,
			("SD1 (short-term variability, ms)", sd1),
			("SD2 (long-term variability, ms)", sd2));
		PlotRow(PlotHeight, ("SD1/SD2 ratio (parasympathetic index)", ratio));
	}

	private void DrawPoincareScatter(float[] rrs)
	{
		// Keep the scatter square and centred — Equal axes plus a wide region would
		// otherwise spread the cloud thin and unreadable.
		float avail = ImGui.GetContentRegionAvail().X;
		float side = MathF.Min(avail, PoincareMaxSide);
		Indent((avail - side) * 0.5f);
		DrawScatterPlot("Poincaré (RR[i] vs RR[i+1])", rrs, new Vector2(side, side));
	}

	// Equal axes keep the cloud square so the identity line reads at 45°.
	private static void DrawScatterPlot(string id, float[] rrs, Vector2 size)
	{
		if (ImPlot.BeginPlot(id, size,
				ImPlotFlags.Equal | ImPlotFlags.NoLegend | ImPlotFlags.NoMouseText))
		{
			ImPlot.SetupAxes("RR[i] (ms)", "RR[i+1] (ms)",
				ImPlotAxisFlags.AutoFit, ImPlotAxisFlags.AutoFit);

			if (rrs.Length >= 2)
			{
				// Identity line RR[i] = RR[i+1] — the cloud clusters along it.
				float[] diagonal = [rrs.Min(), rrs.Max()];
				ImPlot.SetNextLineStyle(new Vector4(0.55f, 0.55f, 0.55f, 0.40f), 1f);
				ImPlot.PlotLine("identity", ref diagonal[0], ref diagonal[0], diagonal.Length);

				// Offset trick: consecutive pairs (rrs[k], rrs[k+1]) without a second array.
				ImPlot.SetNextMarkerStyle(ImPlotMarker.Circle, 2.5f,
					new Vector4(0.40f, 0.80f, 1.00f, 0.85f), 1f);
				ImPlot.PlotScatter("RR", ref rrs[0], ref rrs[1], rrs.Length - 1);
			}

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
		ImGui.TextDisabled("Changes apply live; persisted to disk when you release the control. Hover (?) for tuning help.");
		ImGui.Spacing();

		DrawRestoreDefaultsButton();
		ImGui.Spacing();

		// ── Refresh ──────────────────────────────────────────────────────
		ImGui.SeparatorText("Refresh");

		float emit = (float)_settings.HrvEmitIntervalSeconds;
		if (ImGuiWidgets.Knob("HRV emit (s)", ref emit, 0.5f, 30f, format: "%.1f s", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.HrvEmitIntervalSeconds = emit;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("How often a new HRV sample is emitted. Lower = smoother live graphs but noisier per-sample metrics; higher = steadier metrics, coarser graphs.");
		ImGui.SameLine();

		int window = _settings.SparklineWindowMinutes;
		if (ImGuiWidgets.Knob("History (min)", ref window, 1, 360, format: "%d min", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.SparklineWindowMinutes = window;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("How much history the live charts show. Higher = longer trends visible; lower = more recent detail.");

		// ── Detection thresholds ─────────────────────────────────────────
		// Fraction knobs work in percent (0..100) and divide on assign so the
		// %% formatter renders sane values; the underlying field stays a fraction.
		ImGui.SeparatorText("Detection thresholds");

		var t = _settings.Thresholds;
		float rmssdWarnPct = (float)(t.RmssdWarningDropFraction * 100.0);
		float hrRisePct = (float)(t.HrWarningRiseFraction * 100.0);
		float rmssdAlertPct = (float)(t.RmssdAlertingDropFraction * 100.0);

		if (ImGuiWidgets.Knob("RMSSD warn drop", ref rmssdWarnPct, 5f, 90f, format: "%.0f%%", flags: ImGuiKnobOptions.ValueTooltip))
		{
			t = t with { RmssdWarningDropFraction = rmssdWarnPct / 100.0 };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("RMSSD drop below baseline that triggers a Warning. Lower = more sensitive (warns on small dips); higher = only large drops warn.");
		ImGui.SameLine();

		if (ImGuiWidgets.Knob("HR rise", ref hrRisePct, 5f, 80f, format: "%.0f%%", flags: ImGuiKnobOptions.ValueTooltip))
		{
			t = t with { HrWarningRiseFraction = hrRisePct / 100.0 };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("HR rise above baseline contributing to a Warning. Lower = more sensitive; higher = only large HR rises count.");
		ImGui.SameLine();

		if (ImGuiWidgets.Knob("RMSSD alert drop", ref rmssdAlertPct, 5f, 95f, format: "%.0f%%", flags: ImGuiKnobOptions.ValueTooltip))
		{
			t = t with { RmssdAlertingDropFraction = rmssdAlertPct / 100.0 };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("RMSSD drop that escalates to an Alert. Lower = escalates more readily; higher = reserves alerts for severe drops.");

		float holdSec = (float)t.WarningHoldDuration.TotalSeconds;
		float escalateSec = (float)t.AlertingEscalationDuration.TotalSeconds;
		float cooldownMin = (float)t.CooldownDuration.TotalMinutes;

		if (ImGuiWidgets.Knob("Warning hold (s)", ref holdSec, 5f, 300f, format: "%.0f s", flags: ImGuiKnobOptions.ValueTooltip))
		{
			t = t with { WarningHoldDuration = TimeSpan.FromSeconds(holdSec) };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("How long Warning conditions must persist before the state holds. Higher = fewer transient warnings; lower = reacts faster.");
		ImGui.SameLine();

		if (ImGuiWidgets.Knob("Escalate (s)", ref escalateSec, 10f, 600f, format: "%.0f s", flags: ImGuiKnobOptions.ValueTooltip))
		{
			t = t with { AlertingEscalationDuration = TimeSpan.FromSeconds(escalateSec) };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("How long a Warning must persist before escalating to an Alert. Higher = more patient; lower = alerts sooner.");
		ImGui.SameLine();

		if (ImGuiWidgets.Knob("Cooldown (min)", ref cooldownMin, 1f, 60f, format: "%.0f min", flags: ImGuiKnobOptions.ValueTooltip))
		{
			t = t with { CooldownDuration = TimeSpan.FromMinutes(cooldownMin) };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Quiet period after an episode before the detector re-arms. Higher = fewer repeat alerts; lower = re-arms quickly.");

		// ── Physiological recovery (ends an alert) ───────────────────────
		ImGui.SeparatorText("Recovery (ends an alert)");

		float rmssdRecoverPct = (float)(t.RmssdRecoveryDropFraction * 100.0);
		float hrRecoverPct = (float)(t.HrRecoveryRiseFraction * 100.0);
		float recoverHoldSec = (float)t.RecoveryHoldDuration.TotalSeconds;

		if (ImGuiWidgets.Knob("RMSSD recovered", ref rmssdRecoverPct, 0f, 50f, format: "%.0f%%", flags: ImGuiKnobOptions.ValueTooltip))
		{
			t = t with { RmssdRecoveryDropFraction = rmssdRecoverPct / 100.0 };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("How close RMSSD must climb back to baseline to count as a genuine vagal rebound (not just leaving the Warning zone). Lower = stricter recovery.");
		ImGui.SameLine();

		if (ImGuiWidgets.Knob("HR settled", ref hrRecoverPct, 0f, 30f, format: "%.0f%%", flags: ImGuiKnobOptions.ValueTooltip))
		{
			t = t with { HrRecoveryRiseFraction = hrRecoverPct / 100.0 };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("How close HR must settle back to baseline to count toward recovery. Lower = stricter.");
		ImGui.SameLine();

		if (ImGuiWidgets.Knob("Recovery hold (s)", ref recoverHoldSec, 5f, 600f, format: "%.0f s", flags: ImGuiKnobOptions.ValueTooltip))
		{
			t = t with { RecoveryHoldDuration = TimeSpan.FromSeconds(recoverHoldSec) };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("How long recovery must be sustained before the alert ends — distinguishes real recovery from a transient return to baseline. Higher = surer; lower = ends alerts sooner.");

		// ── LF/HF corroboration ─────────────────────────────────────────
		ImGui.SeparatorText("LF/HF corroboration (optional)");

		bool useLfHf = t.UseLfHfCorroboration;
		if (ImGui.Checkbox("Require LF/HF to also rise before Warning", ref useLfHf))
		{
			t = t with { UseLfHfCorroboration = useLfHf };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("When on, a Warning also requires the LF/HF balance to rise — fewer false positives, but may miss some episodes.");

		using (new ScopedDisable(!useLfHf))
		{
			float lfHfRisePct = (float)(t.LfHfWarningRiseFraction * 100.0);
			if (ImGuiWidgets.Knob("LF/HF rise", ref lfHfRisePct, 5f, 200f, format: "%.0f%%", flags: ImGuiKnobOptions.ValueTooltip))
			{
				t = t with { LfHfWarningRiseFraction = lfHfRisePct / 100.0 };
				_settingsDirty = true;
			}
			ImGui.SameLine();
			HelpMarker("How much LF/HF must rise to corroborate a Warning. Lower = easier to corroborate; higher = stricter.");
		}

		_settings.Thresholds = t;

		// ── Baseline seeding ─────────────────────────────────────────────
		ImGui.SeparatorText("Baseline seeding (applies on re-seed / next launch)");

		var bt = _settings.BaselineTuning;
		int anchorDays = bt.AnchorWindowDays;
		if (ImGuiWidgets.Knob("Anchor (days)", ref anchorDays, 1, 30, format: "%d d", flags: ImGuiKnobOptions.ValueTooltip))
		{
			bt = bt with { AnchorWindowDays = anchorDays };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Days of history feeding your long-term 'normal' (anchor). Higher = more stable, slower to reflect lifestyle change; lower = adapts faster but noisier.");
		ImGui.SameLine();

		float warmStartMin = (float)bt.WarmStartWindowMinutes;
		if (ImGuiWidgets.Knob("Warm-start (min)", ref warmStartMin, 5f, 240f, format: "%.0f min", flags: ImGuiKnobOptions.ValueTooltip))
		{
			bt = bt with { WarmStartWindowMinutes = warmStartMin };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Recent window whose median seeds the live baseline at startup. Higher = smoother seed; lower = reflects the last few minutes.");
		ImGui.SameLine();

		int minSamples = bt.MinWarmStartSamples;
		if (ImGuiWidgets.Knob("Min samples", ref minSamples, 1, 120, format: "%d", flags: ImGuiKnobOptions.ValueTooltip))
		{
			bt = bt with { MinWarmStartSamples = minSamples };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Clean recent samples required to skip the cold warm-up. Higher = only warm-start with solid recent data; lower = warm-start more eagerly.");

		if (ImGui.Button("Re-seed baseline now"))
		{
			_settings.BaselineTuning = bt;
			_pipeline.ReseedBaseline();
		}
		ImGui.SameLine();
		HelpMarker("Re-read history and re-apply the seeding values immediately, without restarting.");

		// ── Baseline responsiveness ──────────────────────────────────────
		ImGui.SeparatorText("Baseline responsiveness (applies live)");

		float driftPct = (float)(bt.MaxAnchorDrift * 100.0);
		if (ImGuiWidgets.Knob("Guardrail (%)", ref driftPct, 10f, 100f, format: "%.0f%%", flags: ImGuiKnobOptions.ValueTooltip))
		{
			bt = bt with { MaxAnchorDrift = driftPct / 100.0 };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("How far the live baseline may stray from your anchor. Lower = tightly tethered to normal (catches slow declines, may clip real shifts); higher = freer to follow recent data.");
		ImGui.SameLine();

		float rmssdWin = (float)bt.RmssdHrWindowMinutes;
		if (ImGuiWidgets.Knob("RMSSD/HR (min)", ref rmssdWin, 1f, 120f, format: "%.0f min", flags: ImGuiKnobOptions.ValueTooltip))
		{
			bt = bt with { RmssdHrWindowMinutes = rmssdWin };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Memory of the live RMSSD/HR baseline. Higher = smoother, slower baseline (deviations read larger); lower = chases recent values.");
		ImGui.SameLine();

		float lfhfWin = (float)bt.LfHfWindowMinutes;
		if (ImGuiWidgets.Knob("LF/HF (min)", ref lfhfWin, 1f, 120f, format: "%.0f min", flags: ImGuiKnobOptions.ValueTooltip))
		{
			bt = bt with { LfHfWindowMinutes = lfhfWin };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Memory of the live LF/HF baseline. Same trade-off as RMSSD/HR, for sympathovagal balance.");
		ImGui.SameLine();

		float warmUpMin = (float)bt.WarmUpMinutes;
		if (ImGuiWidgets.Knob("Warm-up (min)", ref warmUpMin, 0f, 60f, format: "%.0f min", flags: ImGuiKnobOptions.ValueTooltip))
		{
			bt = bt with { WarmUpMinutes = warmUpMin };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Cold-start delay before the detector arms (only when not warm-started from history). Higher = more cautious; lower = arms sooner.");

		_settings.BaselineTuning = bt;

		// ── Charts ───────────────────────────────────────────────────────
		ImGui.SeparatorText("Charts (applies live)");

		var ct = _settings.ChartTuning;
		float plotH = ct.PlotHeight;
		if (ImGuiWidgets.Knob("Plot height", ref plotH, 80f, 500f, format: "%.0f px", flags: ImGuiKnobOptions.ValueTooltip))
		{
			ct = ct with { PlotHeight = plotH };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Height of each chart. Taller = more vertical detail; shorter = more charts fit without scrolling.");
		ImGui.SameLine();

		float cellW = ct.OverviewChartWidth;
		if (ImGuiWidgets.Knob("Cell width", ref cellW, 200f, 1200f, format: "%.0f px", flags: ImGuiKnobOptions.ValueTooltip))
		{
			ct = ct with { OverviewChartWidth = cellW };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Target width of each Overview grid cell. Smaller = more columns (denser); larger = fewer, wider charts.");
		ImGui.SameLine();

		float aspect = ct.MaxPlotAspect;
		if (ImGuiWidgets.Knob("Max aspect", ref aspect, 1f, 8f, format: "%.1f", flags: ImGuiKnobOptions.ValueTooltip))
		{
			ct = ct with { MaxPlotAspect = aspect };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Cap on a chart's width:height before it stops widening — prevents unreadable ribbons on wide windows.");
		ImGui.SameLine();

		float poincare = ct.PoincareMaxSide;
		if (ImGuiWidgets.Knob("Poincaré size", ref poincare, 200f, 900f, format: "%.0f px", flags: ImGuiKnobOptions.ValueTooltip))
		{
			ct = ct with { PoincareMaxSide = poincare };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Maximum size of the square Poincaré scatter.");

		_settings.ChartTuning = ct;

		// ── Metrics overlay ──────────────────────────────────────────────
		ImGui.SeparatorText("Metrics overlay (applies live)");

		var ov = _settings.Overlay;

		bool overlayEnabled = ov.Enabled;
		if (ImGui.Checkbox("Show transparent overlay", ref overlayEnabled))
		{
			ov.Enabled = overlayEnabled;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("A small, transparent heads-up panel pinned to a corner showing your chosen metrics. Right-click the panel to pick metrics, corner, and click-through.");

		bool clickThrough = ov.ClickThrough;
		if (ImGui.Checkbox("Click-through (ignore mouse)", ref clickThrough))
		{
			ov.ClickThrough = clickThrough;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("When on, the overlay ignores the mouse so clicks reach the charts beneath. The right-click metric menu is disabled while this is on.");

		float alpha = ov.BackgroundAlpha;
		if (ImGui.SliderFloat("Background opacity", ref alpha, 0f, 1f, "%.2f"))
		{
			ov.BackgroundAlpha = alpha;
			_settingsDirty = true;
		}

		ImGui.Text("Corner:");
		ImGui.SameLine();
		foreach (var corner in Enum.GetValues<OverlayCorner>())
		{
			if (ImGui.RadioButton(corner.ToString(), ov.Corner == corner))
			{
				ov.Corner = corner;
				_settingsDirty = true;
			}
			ImGui.SameLine();
		}
		ImGui.NewLine();

		ImGui.TextDisabled("Metrics shown:");
		foreach (var metric in OverlayMetrics.All)
		{
			bool shown = ov.Metrics.Contains(metric);
			if (ImGui.Checkbox(OverlayMetrics.Label(metric), ref shown))
			{
				if (shown)
				{
					ov.Metrics.Add(metric);
				}
				else
				{
					ov.Metrics.Remove(metric);
				}

				_settingsDirty = true;
			}
		}

		// ── Advanced HRV windows ─────────────────────────────────────────
		ImGui.SeparatorText("Advanced HRV windows (changes metric definitions)");

		var ht = _settings.HrvTuning;
		float shortWin = (float)ht.ShortWindowSeconds;
		if (ImGuiWidgets.Knob("Short (s)", ref shortWin, 30f, 120f, format: "%.0f s", flags: ImGuiKnobOptions.ValueTooltip))
		{
			ht = ht with { ShortWindowSeconds = shortWin };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("⚠ NN window for RMSSD/pNN50/mean-HR. 60 s is a common standard — changing it alters the metrics and comparability with references.");
		ImGui.SameLine();

		float extWin = (float)ht.ExtendedWindowSeconds;
		if (ImGuiWidgets.Knob("Extended (s)", ref extWin, 120f, 600f, format: "%.0f s", flags: ImGuiKnobOptions.ValueTooltip))
		{
			ht = ht with { ExtendedWindowSeconds = extWin };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("⚠ Window for frequency-domain & Poincaré metrics. 300 s (5 min) is the clinical standard — changing it breaks comparability with norms.");
		ImGui.SameLine();

		float extCompute = (float)ht.ExtendedComputeIntervalSeconds;
		if (ImGuiWidgets.Knob("Recompute (s)", ref extCompute, 5f, 120f, format: "%.0f s", flags: ImGuiKnobOptions.ValueTooltip))
		{
			ht = ht with { ExtendedComputeIntervalSeconds = extCompute };
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("How often the extended (frequency/Poincaré) metrics recompute. Lower = fresher, more CPU.");

		_settings.HrvTuning = ht;

		// Apply on every frame (live), but defer the disk write until no widget is
		// being dragged — otherwise we'd rewrite the settings file 30+ times a second.
		if (_settingsDirty && !ImGui.IsAnyItemActive())
		{
			_settings.Save();
			_settingsDirty = false;
		}
	}

	// A small "(?)" hint that shows tuning guidance on hover.
	private static void HelpMarker(string text)
	{
		ImGui.TextDisabled("(?)");
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip(text);
		}
	}

	// Resets every tunable to its recommended default (behind a confirmation), then
	// persists and re-seeds the baseline. Device/alert/data-path settings are left alone.
	private void DrawRestoreDefaultsButton()
	{
		if (ImGui.Button("Restore best-practice defaults"))
		{
			ImGui.OpenPopup("restore-defaults");
		}
		ImGui.SameLine();
		HelpMarker("Resets all tuning — detection thresholds, baseline, charts, HRV windows, and refresh — to the recommended defaults, then re-seeds the baseline. Your device, alert, and data-path settings are untouched.");

		if (ImGui.BeginPopup("restore-defaults"))
		{
			ImGui.Text("Reset all tuning to recommended defaults?");
			if (ImGui.Button("Yes, reset"))
			{
				_settings.Thresholds = new DetectionThresholds();
				_settings.BaselineTuning = new BaselineTuning();
				_settings.ChartTuning = new ChartTuning();
				_settings.HrvTuning = new HrvTuning();
				_settings.HrvEmitIntervalSeconds = 5.0;
				_settings.SparklineWindowMinutes = 60;
				_settings.Save();
				_pipeline.ReseedBaseline();
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();
			if (ImGui.Button("Cancel"))
			{
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}
	}

	private static void Plot(string title, float[] data, Vector2 size)
	{
		// Always draw the frame (even with no data) so rows stay aligned.
		// X is just the sample index, so hide its tick labels; ImPlot auto-fits Y.
		if (ImPlot.BeginPlot(title, size, ImPlotFlags.NoLegend | ImPlotFlags.NoMouseText))
		{
			ImPlot.SetupAxes(string.Empty, string.Empty,
				ImPlotAxisFlags.AutoFit | ImPlotAxisFlags.NoTickLabels, ImPlotAxisFlags.AutoFit);
			if (data.Length >= 2)
			{
				ImPlot.PlotLine(title, ref data[0], data.Length);
			}
			ImPlot.EndPlot();
		}
	}

	// One comparison chart (a series plus its baseline) sharing a single auto-fit
	// Y axis, capped to MaxPlotAspect and centred in the available width.
	private void PlotPair(string title, string aLabel, float[] a, string bLabel, float[] b)
	{
		(Vector2 size, float indent) = CenteredCell(ImGui.GetContentRegionAvail().X, PlotHeight);
		Indent(indent);

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

	// Lay out N plots in a single row, each sharing the width equally (capped to
	// MaxPlotAspect) and the group centred. Handles a single plot too.
	private void PlotRow(float height, params (string label, float[] data)[] plots)
	{
		int n = plots.Length;
		if (n == 0)
		{
			return;
		}

		float spacing = ImGui.GetStyle().ItemSpacing.X;
		float avail = ImGui.GetContentRegionAvail().X;
		float cell = MathF.Min((avail - (spacing * (n - 1))) / n, height * MaxPlotAspect);
		float used = (cell * n) + (spacing * (n - 1));
		Indent((avail - used) * 0.5f);

		Vector2 size = new(cell, height);
		for (int i = 0; i < n; i++)
		{
			Plot(plots[i].label, plots[i].data, size);
			if (i < n - 1)
			{
				ImGui.SameLine();
			}
		}
	}

	// Width capped to MaxPlotAspect, plus the horizontal slack to centre it.
	private (Vector2 size, float indent) CenteredCell(float availWidth, float height)
	{
		float w = MathF.Min(availWidth, height * MaxPlotAspect);
		return (new Vector2(w, height), MathF.Max(0f, (availWidth - w) * 0.5f));
	}

	private static void Indent(float amount)
	{
		if (amount > 0f)
		{
			ImGui.SetCursorPosX(ImGui.GetCursorPosX() + amount);
		}
	}

	private static ImColor StateColor(DetectorState state) =>
		new() { Value = StateColors.For(state) };

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
