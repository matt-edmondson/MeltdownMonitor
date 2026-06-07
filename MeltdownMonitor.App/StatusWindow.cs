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
using MeltdownMonitor.Core.Motion;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Core.Regulation;
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

	// Side of the square resize grip drawn in the overlay's bottom-right corner.
	private const float OverlayGripSize = 16f;

	// Chart layout, tunable from the Settings tab (applied live).
	private float OverviewChartWidth => _settings.ChartTuning.OverviewChartWidth;
	private float MaxPlotAspect => _settings.ChartTuning.MaxPlotAspect;

	private readonly object _historyLock = new();
	private readonly Pipeline _pipeline;
	private readonly MeltdownRepository _repository;
	private readonly AppSettings _settings;
	private readonly IntervalAction _historyRefreshAction;
	private readonly Regulation.RegulationFieldView _regulationField;
	private readonly ImGuiWidgets.TabPanel _tabs;
	private readonly StatusTheme _theme = new();
	private Thread? _uiThread;
	private int _appliedCapacity = InitialSparklineCapacity;
	private int _subscriptionsReleased;

	// Auto-fitted height (px) of the compact HUD, measured each frame and applied on the
	// next so the overlay grows/shrinks vertically with the selected metrics.
	private int _compactHeight;
	private bool _settingsDirty;

	private readonly TimedSeries _rmssd = new(InitialSparklineCapacity);
	private readonly TimedSeries _baselineRmssd = new(InitialSparklineCapacity);
	private readonly TimedSeries _pnn50 = new(InitialSparklineCapacity);
	private readonly TimedSeries _sdnn = new(InitialSparklineCapacity);
	private readonly TimedSeries _meanHr = new(InitialSparklineCapacity);
	private readonly TimedSeries _baselineHr = new(InitialSparklineCapacity);
	private readonly TimedSeries _lfPower = new(InitialSparklineCapacity);
	private readonly TimedSeries _hfPower = new(InitialSparklineCapacity);
	private readonly TimedSeries _lfHfRatio = new(InitialSparklineCapacity);
	private readonly TimedSeries _baselineLfHf = new(InitialSparklineCapacity);
	private readonly TimedSeries _sd1 = new(InitialSparklineCapacity);
	private readonly TimedSeries _sd2 = new(InitialSparklineCapacity);
	private readonly TimedSeries _sd1Sd2 = new(InitialSparklineCapacity);
	private readonly TimedSeries _contact = new(InitialSparklineCapacity);

	// Raw per-beat RR intervals (ms). No usable per-beat timestamp (batched beats share
	// one arrival time), so its x axis is reconstructed via RrTimeAxis at snapshot time.
	private readonly RingBuffer<double> _recentRr = new(InitialSparklineCapacity);

	// Battery is updated on its own slow cadence (a read on connect plus occasional
	// notifications), so it lives outside AllSparklines and isn't resampled with them.
	private readonly TimedSeries _battery = new(InitialSparklineCapacity);

	private TimedSeries[] AllSparklines => [
		_rmssd, _baselineRmssd, _pnn50, _sdnn,
		_meanHr, _baselineHr,
		_lfPower, _hfPower, _lfHfRatio, _baselineLfHf,
		_sd1, _sd2, _sd1Sd2,
		_contact,
	];

	public StatusWindow(Pipeline pipeline, MeltdownRepository repository, AppSettings settings)
	{
		_pipeline = pipeline;
		_repository = repository;
		_settings = settings;

		_pipeline.SampleUpdated += OnSampleUpdated;
		_pipeline.BeatReceived += OnBeatReceived;
		_pipeline.BatteryUpdated += OnBatteryUpdated;

		_regulationField = new Regulation.RegulationFieldView(_pipeline);

		// Each tab's body is drawn inside its own scrollable child so the tab bar
		// (and the status header above it) stay pinned on screen when a tab's
		// content overflows — only the body scrolls, not the whole window.
		_tabs = new ImGuiWidgets.TabPanel("status-tabs");
		_tabs.AddTab("Regulation Field", () => DrawScrollableTab("regulation-field", _regulationField.Draw));
		_tabs.AddTab("Overview", () => DrawScrollableTab("overview", DrawOverviewTab));
		_tabs.AddTab("Heart Rate", () => DrawScrollableTab("heart-rate", DrawHeartRateTab));
		_tabs.AddTab("Time-Domain HRV", () => DrawScrollableTab("time-domain", DrawTimeDomainTab));
		_tabs.AddTab("Frequency-Domain", () => DrawScrollableTab("frequency-domain", DrawFrequencyTab));
		_tabs.AddTab("Poincaré", () => DrawScrollableTab("poincare", DrawPoincareTab));
		_tabs.AddTab("Annotations", () => DrawScrollableTab("annotations", DrawAnnotationsTab));
		_tabs.AddTab("Settings", () => DrawScrollableTab("settings", DrawSettingsTab));

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
					// In overlay mode the window is always-on-top (and therefore usually
					// unfocused), but the Regulation Field animates and the HUD shows live
					// data — so keep it at a smooth rate instead of the unfocused throttle.
					PerformanceSettings = new ImGuiAppPerformanceSettings
					{
						OverlayFps = 60.0,
					},
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

			_rmssd.PushBack(sample.Timestamp, (float)sample.Rmssd);
			_baselineRmssd.PushBack(sample.Timestamp, (float)sample.BaselineRmssd);
			_pnn50.PushBack(sample.Timestamp, (float)sample.Pnn50);
			_meanHr.PushBack(sample.Timestamp, (float)sample.MeanHr);
			_baselineHr.PushBack(sample.Timestamp, (float)sample.BaselineHr);
			_baselineLfHf.PushBack(sample.Timestamp, (float)sample.BaselineLfHfRatio);
			_contact.PushBack(sample.Timestamp, ContactToValue(sample.SensorContact));

			if (sample.Extended is { } ext)
			{
				_sdnn.PushBack(sample.Timestamp, (float)ext.Sdnn);
				_lfPower.PushBack(sample.Timestamp, (float)ext.LfPowerMs2);
				_hfPower.PushBack(sample.Timestamp, (float)ext.HfPowerMs2);
				_lfHfRatio.PushBack(sample.Timestamp, (float)ext.LfHfRatio);
				_sd1.PushBack(sample.Timestamp, (float)ext.SD1);
				_sd2.PushBack(sample.Timestamp, (float)ext.SD2);
				_sd1Sd2.PushBack(sample.Timestamp, (float)ext.SD1SD2Ratio);
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

	private void OnBatteryUpdated(BatteryReading reading)
	{
		lock (_historyLock)
		{
			_battery.PushBack(reading.Timestamp, reading.Percent);
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

		IReadOnlyList<BatteryReading> batteries;
		try
		{
			batteries = MeltdownRepository.ReadBatteryHistory(_settings.DatabasePath, from, to);
		}
		catch
		{
			batteries = [];
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
				_rmssd.PushBack(s.Timestamp, (float)s.Rmssd);
				_baselineRmssd.PushBack(s.Timestamp, (float)s.BaselineRmssd);
				_pnn50.PushBack(s.Timestamp, (float)s.Pnn50);
				_meanHr.PushBack(s.Timestamp, (float)s.MeanHr);
				_baselineHr.PushBack(s.Timestamp, (float)s.BaselineHr);
				_baselineLfHf.PushBack(s.Timestamp, (float)s.BaselineLfHfRatio);
				_contact.PushBack(s.Timestamp, ContactToValue(s.SensorContact));

				if (s.Extended is { } ext)
				{
					_sdnn.PushBack(s.Timestamp, (float)ext.Sdnn);
					_lfPower.PushBack(s.Timestamp, (float)ext.LfPowerMs2);
					_hfPower.PushBack(s.Timestamp, (float)ext.HfPowerMs2);
					_lfHfRatio.PushBack(s.Timestamp, (float)ext.LfHfRatio);
					_sd1.PushBack(s.Timestamp, (float)ext.SD1);
					_sd2.PushBack(s.Timestamp, (float)ext.SD2);
					_sd1Sd2.PushBack(s.Timestamp, (float)ext.SD1SD2Ratio);
				}
			}

			_battery.Resize(desired);
			foreach (var b in batteries.TakeLast(desired))
			{
				_battery.PushBack(b.Timestamp, b.Percent);
			}
		}
	}

	// 1 = signal trustworthy (Detected or NotSupported), 0 = NotDetected (readings gated).
	private static float ContactToValue(SensorContactStatus contact) =>
		contact == SensorContactStatus.NotDetected ? 0f : 1f;

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

		if (_settings.Overlay.Enabled)
		{
			DrawOverlayMode();
		}
		else
		{
			// Leaving overlay mode (or never in it) restores the decorated window.
			ImGuiApp.DisableOverlay();
			DrawStatusHeader();
			ImGui.Separator();
			_tabs.Draw();
		}

		// Persist any setting touched this frame, deferring the disk write while a
		// control is being dragged so we don't rewrite the file every frame.
		if (_settingsDirty && !ImGui.IsAnyItemActive())
		{
			_settings.Save();
			_settingsDirty = false;
		}
	}

	// Draw a tab's body inside a child that fills the remaining content region. The child
	// owns the scrolling, so the tab bar (and the status header above it) stay fixed while
	// only the body scrolls when its content is taller than the window.
	private static void DrawScrollableTab(string id, Action drawBody)
	{
		if (ImGui.BeginChild($"##tab-{id}", new Vector2(0f, 0f), ImGuiChildFlags.None))
		{
			drawBody();
		}

		ImGui.EndChild();
	}

	// Render the window as a borderless, translucent, always-on-top overlay: a compact
	// metrics HUD (with the Regulation Field) by default, or the full tabbed UI when expanded.
	private void DrawOverlayMode()
	{
		var ov = _settings.Overlay;
		ImGuiApp.EnableOverlay(ov.Opacity, ov.ClickThrough);

		// Width is user-specified; the compact HUD's height auto-fits the selected metrics
		// (measured on the previous frame) so it never shows a vertical scrollbar. The
		// expanded tabbed UI keeps its own configurable, resizable height.
		int height = ov.Expanded || _compactHeight <= 0 ? ov.Height : _compactHeight;
		ImGuiApp.SetOverlayGeometry(MapCorner(ov.Corner), ov.OffsetX, ov.OffsetY, ov.Width, height);

		DrawOverlayToolbar(ov);

		if (ov.Expanded)
		{
			DrawStatusHeader();
			ImGui.Separator();
			_tabs.Draw();
			DrawResizeGrip(ov);
		}
		else
		{
			DrawCompactHud(ov);

			// Measure the content extent (the next-item Y plus the bottom window padding)
			// so next frame's window is exactly tall enough to hold the HUD. The width grip
			// is placed at that bottom edge and folded into the measurement so it has room
			// without forcing a scrollbar. A row of item spacing is added as slack so DPI
			// rounding never leaves the window a hair short of its content (which is all it
			// takes for ImGui to show a vertical scrollbar).
			float contentBottom = ImGui.GetCursorPosY();
			DrawWidthGrip(ov, contentBottom);
			var style = ImGui.GetStyle();
			_compactHeight = (int)MathF.Ceiling(contentBottom + OverlayGripSize + style.WindowPadding.Y + style.ItemSpacing.Y);
		}
	}

	// Map our persisted overlay corner onto ImGuiApp's canonical OverlayCorner. Both enums share
	// the same members; the mapping keeps us decoupled from the library enum's underlying values.
	private static ktsu.ImGui.App.OverlayCorner MapCorner(OverlayCorner corner) => corner switch
	{
		OverlayCorner.TopLeft => ktsu.ImGui.App.OverlayCorner.TopLeft,
		OverlayCorner.TopRight => ktsu.ImGui.App.OverlayCorner.TopRight,
		OverlayCorner.BottomLeft => ktsu.ImGui.App.OverlayCorner.BottomLeft,
		OverlayCorner.BottomRight => ktsu.ImGui.App.OverlayCorner.BottomRight,
		_ => ktsu.ImGui.App.OverlayCorner.TopRight,
	};

	// A slim control strip pinned to the top of the overlay: a drag handle that nudges the
	// corner offset, plus expand / opacity / click-through / exit controls.
	private void DrawOverlayToolbar(OverlaySettings ov)
	{
		// Drag handle — adjusts the offset from the locked corner while held. The sign
		// depends on the corner so dragging always moves the window the way the mouse goes.
		ImGui.Button(":::");
		if (ImGui.IsItemActive())
		{
			Vector2 delta = ImGui.GetIO().MouseDelta;
			bool right = ov.Corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
			bool bottom = ov.Corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight;
			int newX = ov.OffsetX + (int)(right ? -delta.X : delta.X);
			int newY = ov.OffsetY + (int)(bottom ? -delta.Y : delta.Y);
			if (newX != ov.OffsetX || newY != ov.OffsetY)
			{
				ov.OffsetX = Math.Max(0, newX);
				ov.OffsetY = Math.Max(0, newY);
				_settingsDirty = true;
			}
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Drag to move the overlay (adjusts the corner offset)");
		}

		ImGui.SameLine();
		if (ImGui.Button(ov.Expanded ? "Compact" : "Expand"))
		{
			ov.Expanded = !ov.Expanded;
			_settingsDirty = true;
		}

		ImGui.SameLine();
		ImGui.SetNextItemWidth(110f);
		float opacity = ov.Opacity;
		if (ImGui.SliderFloat("##overlay-opacity", ref opacity, 0.2f, 1.0f, "opacity %.2f"))
		{
			ov.Opacity = opacity;
			_settingsDirty = true;
		}

		ImGui.SameLine();
		bool clickThrough = ov.ClickThrough;
		if (ImGui.Checkbox("Click-through", ref clickThrough))
		{
			ov.ClickThrough = clickThrough;
			_settingsDirty = true;
		}

		ImGui.SameLine();
		if (ImGui.Button("Exit overlay"))
		{
			ov.Enabled = false;
			_settingsDirty = true;
		}

		ImGui.Separator();
	}

	// The default overlay content: the Regulation Field figure-8 plus the selected metrics.
	private void DrawCompactHud(OverlaySettings ov)
	{
		if (ov.ShowRegulationField)
		{
			// Reuse the signature Regulation Field instrument so the overlay and the tab
			// stay identical (animation, palette, trail all live in one place). It fills its
			// available height, so in the auto-sized HUD we pin it to a fixed-height child
			// (proportional to the width) — otherwise it would keep growing every frame.
			//
			// Size the field from the *configured* width rather than the live available width:
			// the latter shrinks by a scrollbar's width the instant one appears, which would
			// shorten the field, shorten the measured HUD height, and so flip the scrollbar
			// off again — a flip-flop that leaves the scrollbar flickering on. Deriving the
			// height from the fixed width keeps it stable whether or not a scrollbar is present.
			float fieldWidth = ov.Width - (2f * ImGui.GetStyle().WindowPadding.X);
			float fieldHeight = MathF.Max(120f, fieldWidth * 0.5f);
			if (ImGui.BeginChild("##overlay-regfield", new Vector2(0f, fieldHeight), ImGuiChildFlags.None,
				ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
			{
				_regulationField.Draw();
			}

			ImGui.EndChild();
			ImGui.Spacing();
		}

		var sample = new OverlaySample(
			_pipeline.CurrentState,
			_pipeline.LatestSample,
			_pipeline.Baseline.WarmUpProgress,
			_pipeline.LatestReading,
			_pipeline.LatestBatteryPercent,
			_pipeline.LatestContact);

		if (ov.Metrics.Count == 0)
		{
			ImGui.TextDisabled("No metrics selected — see Settings.");
			return;
		}

		foreach (var metric in ov.Metrics)
		{
			ImGui.TextDisabled($"{OverlayMetrics.Label(metric)}:");
			ImGui.SameLine();

			string value = OverlayMetrics.Format(metric, sample);
			if (metric == OverlayMetric.State)
			{
				ImGui.TextColored(StateColors.For(sample.State), value);
			}
			else
			{
				ImGui.Text(value);
			}
		}
	}

	// A small grip in the bottom-right corner of the expanded overlay; dragging it resizes
	// the window on both axes. The window stays anchored to its corner, so it grows or
	// shrinks away from that corner.
	private void DrawResizeGrip(OverlaySettings ov)
	{
		Vector2 windowSize = ImGui.GetWindowSize();
		ImGui.SetCursorPos(new Vector2(windowSize.X - OverlayGripSize - 4f, windowSize.Y - OverlayGripSize - 4f));
		ImGui.Button("##overlay-resize", new Vector2(OverlayGripSize, OverlayGripSize));
		if (ImGui.IsItemActive())
		{
			Vector2 delta = ImGui.GetIO().MouseDelta;
			int newW = ov.Width + (int)delta.X;
			int newH = ov.Height + (int)delta.Y;
			if (newW != ov.Width || newH != ov.Height)
			{
				ov.Width = Math.Max(200, newW);
				ov.Height = Math.Max(140, newH);
				_settingsDirty = true;
			}
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Drag to resize");
		}
	}

	// The compact HUD's grip, pinned to the right edge just below the metrics. Its height is
	// auto-fitted, so this only adjusts the width; dragging vertically does nothing.
	private void DrawWidthGrip(OverlaySettings ov, float y)
	{
		float x = ImGui.GetWindowSize().X - OverlayGripSize - ImGui.GetStyle().WindowPadding.X;
		ImGui.SetCursorPos(new Vector2(Math.Max(0f, x), y));
		ImGui.Button("##overlay-resize-w", new Vector2(OverlayGripSize, OverlayGripSize));
		if (ImGui.IsItemActive())
		{
			int newW = ov.Width + (int)ImGui.GetIO().MouseDelta.X;
			if (newW != ov.Width)
			{
				ov.Width = Math.Max(200, newW);
				_settingsDirty = true;
			}
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Drag to resize width (height auto-fits the metrics)");
		}
	}

	/// <summary>
	/// Turns overlay mode on or off. Enabling it reveals the window if it was hidden, since
	/// the overlay is the window itself. Safe to call from any thread.
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

	/// <summary>Toggles click-through for overlay mode (needed when the in-overlay controls are unclickable).</summary>
	public void ToggleOverlayClickThrough()
	{
		_settings.Overlay.ClickThrough = !_settings.Overlay.ClickThrough;
		_settings.Save();
	}

	private void DrawStatusHeader()
	{
		var state = _pipeline.CurrentState;
		ImGuiWidgets.ColorIndicator(StateColor(state), enabled: true);
		ImGui.SameLine();
		ImGui.Text($"State: {state}");
		ImGui.SameLine();
		ImGui.TextDisabled($"(for {FormatStateDuration(DateTimeOffset.UtcNow - _pipeline.StateEnteredAt)})");

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

		if (_pipeline.LatestBatteryPercent is { } battery)
		{
			ImGui.SameLine();
			ImGui.TextDisabled($"   Battery {battery}%");
		}

		// Flag lost skin/electrode contact — RR data is unreliable until it returns.
		if (_pipeline.LatestContact == SensorContactStatus.NotDetected)
		{
			ImGui.SameLine();
			ImGui.TextColored(StateColors.For(DetectorState.Warning), "   No sensor contact");
		}

		// Motion corroboration: show the live movement level + intensity (g) — handy for tuning the
		// gate thresholds — and flag in warning colour when it's high enough to defer alerts.
		var movement = _pipeline.CurrentMovement;
		if (movement != MovementLevel.Unknown)
		{
			var thresholds = _pipeline.LatestThresholds;
			bool gating = thresholds.UseMovementGating && movement >= thresholds.MovementGateLevel;
			string label = $"   Movement {movement} ({_pipeline.CurrentMovementIntensity:F2}g){(gating ? " — gating" : string.Empty)}";
			ImGui.SameLine();
			if (gating)
			{
				ImGui.TextColored(StateColors.For(DetectorState.Warning), label);
			}
			else
			{
				ImGui.TextDisabled(label);
			}
		}
	}

	// Compact "how long in the current state" label for the status header, e.g.
	// "8s", "3m 12s", "1h 4m". Negative spans (clock skew) clamp to zero.
	private static string FormatStateDuration(TimeSpan span)
	{
		if (span < TimeSpan.Zero)
		{
			span = TimeSpan.Zero;
		}

		if (span.TotalHours >= 1)
		{
			return $"{(int)span.TotalHours}h {span.Minutes}m";
		}

		if (span.TotalMinutes >= 1)
		{
			return $"{(int)span.TotalMinutes}m {span.Seconds}s";
		}

		return $"{span.Seconds}s";
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

		if (_pipeline.LatestDeviceInfo is { } device)
		{
			ImGui.Spacing();
			ImGui.TextDisabled($"Device: {device.Summary}");
			if (!string.IsNullOrWhiteSpace(device.SerialNumber))
			{
				ImGui.SameLine();
				ImGui.TextDisabled($"· SN {device.SerialNumber}");
			}
		}

		ImGui.Separator();

		double now = NowEpochSeconds();
		ChartSeries rmssd, baseRmssd, pnn50, sdnn, hr, baseHr, lf, hf, lfhf,
			baseLfhf, sd1, sd2, sd1sd2, battery, contact;
		double[] rrsD;
		lock (_historyLock)
		{
			rmssd = _rmssd.Snapshot(now);
			baseRmssd = _baselineRmssd.Snapshot(now);
			pnn50 = _pnn50.Snapshot(now);
			sdnn = _sdnn.Snapshot(now);
			hr = _meanHr.Snapshot(now);
			baseHr = _baselineHr.Snapshot(now);
			lf = _lfPower.Snapshot(now);
			hf = _hfPower.Snapshot(now);
			lfhf = _lfHfRatio.Snapshot(now);
			baseLfhf = _baselineLfHf.Snapshot(now);
			sd1 = _sd1.Snapshot(now);
			sd2 = _sd2.Snapshot(now);
			sd1sd2 = _sd1Sd2.Snapshot(now);
			battery = _battery.Snapshot(now);
			contact = _contact.Snapshot(now);
			rrsD = SnapshotD(_recentRr);
		}

		(float[] rrX, float[] rrY) = RrSeries(rrsD);

		// Every metric at full chart size, laid out with the ktsu.ImGui.Widgets grid,
		// which fits as many columns as the window width allows. Baselines overlay
		// where available; the Poincaré scatter is included as a square cell.
		OverviewChart[] charts =
		[
			new("RMSSD vs baseline (ms)", rmssd.Xs, rmssd.Ys, baseRmssd.Xs, baseRmssd.Ys),
			new("Heart rate vs baseline (bpm)", hr.Xs, hr.Ys, baseHr.Xs, baseHr.Ys),
			new("LF/HF ratio (sympathovagal balance)", lfhf.Xs, lfhf.Ys, baseLfhf.Xs, baseLfhf.Ys),
			new("pNN50 (%)", pnn50.Xs, pnn50.Ys, null, null),
			new("SDNN (ms)", sdnn.Xs, sdnn.Ys, null, null),
			new("LF power (ms²)", lf.Xs, lf.Ys, null, null),
			new("HF power (ms²)", hf.Xs, hf.Ys, null, null),
			new("SD1 (ms)", sd1.Xs, sd1.Ys, null, null),
			new("SD2 (ms)", sd2.Xs, sd2.Ys, null, null),
			new("SD1/SD2 ratio (parasympathetic index)", sd1sd2.Xs, sd1sd2.Ys, null, null),
			new("RR intervals (ms)", rrX, rrY, null, null),
			new("Battery (%)", battery.Xs, battery.Ys, null, null),
			new("Poincaré (RR[i] vs RR[i+1])", rrX, rrY, null, null, IsScatter: true),
		];

		// Fit as many columns as the preferred width allows, then stretch the cells to fill
		// the row exactly (no trailing gap); height scales with the cell width.
		float availX = ImGui.GetContentRegionAvail().X;
		float gridSpacing = ImGui.GetStyle().ItemSpacing.X;
		int cols = Math.Max(1, (int)MathF.Floor((availX + gridSpacing) / (OverviewChartWidth + gridSpacing)));
		float cellW = (availX - (gridSpacing * (cols - 1))) / cols;
		float cellH = cellW * 0.55f;
		ImGuiWidgets.RowMajorGrid("overview-charts", charts,
			_ => new Vector2(cellW, cellH),
			(chart, cellSize, itemSize) => DrawOverviewChart(chart, itemSize));

		// Contact step-strip: binary 0/1 signal, fixed Y range so a single value
		// of 1 fills the bar and a single value of 0 is clearly at the floor.
		ImGui.Spacing();
		float contactH = ImGui.GetTextLineHeightWithSpacing() * 2f;
		float contactW = ImGui.GetContentRegionAvail().X;
		if (ImPlot.BeginPlot("Sensor contact", new Vector2(contactW, contactH),
				ImPlotFlags.NoLegend | ImPlotFlags.NoMouseText))
		{
			SetupTimeAxis(ImPlotAxisFlags.Lock | ImPlotAxisFlags.NoTickLabels);
			ImPlot.SetupAxisLimits(ImAxis.Y1, 0.0, 1.0, ImPlotCond.Always);
			if (contact.Ys.Length >= 2)
			{
				ImPlot.PlotStairs("Sensor contact (1=OK 0=no contact)", ref contact.Xs[0], ref contact.Ys[0], contact.Ys.Length);
			}

			ImPlot.EndPlot();
		}
	}

	private sealed record OverviewChart(
		string Title, float[] DataXs, float[] DataYs,
		float[]? BaselineXs, float[]? BaselineYs, bool IsScatter = false);

	private void DrawOverviewChart(OverviewChart chart, Vector2 size)
	{
		if (chart.IsScatter)
		{
			DrawScatterPlot(chart.Title, chart.DataYs, size);
			return;
		}

		ImPlotFlags flags = chart.BaselineYs is null
			? ImPlotFlags.NoMouseText | ImPlotFlags.NoLegend
			: ImPlotFlags.NoMouseText;

		if (ImPlot.BeginPlot(chart.Title, size, flags))
		{
			SetupTimeAxis();

			if (chart.BaselineYs is { Length: >= 2 } by && chart.BaselineXs is { } bx)
			{
				ImPlot.PlotLine("baseline", ref bx[0], ref by[0], by.Length);
			}
			if (chart.DataYs.Length >= 2)
			{
				ImPlot.PlotLine(chart.Title, ref chart.DataXs[0], ref chart.DataYs[0], chart.DataYs.Length);
			}

			ImPlot.EndPlot();
		}
	}

	private void DrawHeartRateTab()
	{
		double now = NowEpochSeconds();
		ChartSeries hr, baseHr;
		double[] rrsD;
		lock (_historyLock)
		{
			hr = _meanHr.Snapshot(now);
			baseHr = _baselineHr.Snapshot(now);
			rrsD = SnapshotD(_recentRr);
		}

		(float[] rrX, float[] rrY) = RrSeries(rrsD);

		float h = FillRowHeight(2);
		PlotPair(h, "Heart rate vs baseline (bpm)", "HR", hr.Xs, hr.Ys, "Baseline HR", baseHr.Xs, baseHr.Ys);
		PlotRow(h, ("RR intervals (ms, last received beats)", rrX, rrY));
	}

	private void DrawTimeDomainTab()
	{
		double now = NowEpochSeconds();
		ChartSeries rmssd, baseRmssd, pnn50, sdnn;
		lock (_historyLock)
		{
			rmssd = _rmssd.Snapshot(now);
			baseRmssd = _baselineRmssd.Snapshot(now);
			pnn50 = _pnn50.Snapshot(now);
			sdnn = _sdnn.Snapshot(now);
		}

		float h = FillRowHeight(2);
		PlotPair(h, "RMSSD (ms)", "RMSSD", rmssd.Xs, rmssd.Ys, "Baseline", baseRmssd.Xs, baseRmssd.Ys);
		PlotRow(h, ("pNN50 (%)", pnn50.Xs, pnn50.Ys), ("SDNN (ms)", sdnn.Xs, sdnn.Ys));
	}

	private void DrawFrequencyTab()
	{
		double now = NowEpochSeconds();
		ChartSeries lf, hf, ratio, baseLfhf;
		lock (_historyLock)
		{
			lf = _lfPower.Snapshot(now);
			hf = _hfPower.Snapshot(now);
			ratio = _lfHfRatio.Snapshot(now);
			baseLfhf = _baselineLfHf.Snapshot(now);
		}

		float h = FillRowHeight(2, ImGui.GetTextLineHeightWithSpacing());
		PlotPair(h, "LF/HF ratio (sympathovagal balance)", "LF/HF", ratio.Xs, ratio.Ys, "Baseline LF/HF", baseLfhf.Xs, baseLfhf.Ys);
		PlotRow(h,
			("LF power (ms², 0.04–0.15 Hz)", lf.Xs, lf.Ys),
			("HF power (ms², 0.15–0.40 Hz)", hf.Xs, hf.Ys));

		if (ratio.Ys.Length < 2)
		{
			ImGui.TextDisabled("Frequency metrics need ≥2 minutes of clean beats to populate.");
		}
	}

	private void DrawPoincareTab()
	{
		double now = NowEpochSeconds();
		ChartSeries sd1, sd2, ratio;
		double[] rrsD;
		lock (_historyLock)
		{
			sd1 = _sd1.Snapshot(now);
			sd2 = _sd2.Snapshot(now);
			ratio = _sd1Sd2.Snapshot(now);
			rrsD = SnapshotD(_recentRr);
		}

		float[] rrs = ToFloat(rrsD);

		float h = FillRowHeight(3);
		DrawPoincareScatter(rrs, h); // unchanged: scatter, not a time series

		PlotRow(h,
			("SD1 (short-term variability, ms)", sd1.Xs, sd1.Ys),
			("SD2 (long-term variability, ms)", sd2.Xs, sd2.Ys));
		PlotRow(h, ("SD1/SD2 ratio (parasympathetic index)", ratio.Xs, ratio.Ys));
	}

	private static void DrawPoincareScatter(float[] rrs, float maxSide)
	{
		// Keep the scatter square and centred — Equal axes plus a wide region would
		// otherwise spread the cloud thin and unreadable. Sized to the row height.
		float avail = ImGui.GetContentRegionAvail().X;
		float side = MathF.Min(avail, maxSide);
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

		int trail = _settings.RegulationTrailLength;
		if (ImGuiWidgets.Knob("Trail (pts)", ref trail, 12, 2160, format: "%d pts", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.RegulationTrailLength = trail;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("How many recent readings the Regulation Field comet trail shows. Higher = longer tail; lower = shorter.");
		ImGui.SameLine();

		float trailOpacityPct = (float)(_settings.TrailOpacity * 100.0);
		if (ImGuiWidgets.Knob("Trail opacity", ref trailOpacityPct, 0f, 100f, format: "%.0f%%", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.TrailOpacity = trailOpacityPct / 100.0;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Opacity of the Regulation Field comet trail. It draws additively and blooms where the tail overlaps itself and the marker, so lower this if it saturates to white; default 70%.");
		ImGui.SameLine();

		float jitter = (float)_settings.JitterExaggeration;
		if (ImGuiWidgets.Knob("Jitter", ref jitter, 0f, 3f, format: "%.1fx", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.JitterExaggeration = jitter;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("How much the Regulation Field's live trace exaggerates beat-to-beat variability. 1.0x is tuned default; 0 flattens it; higher amplifies the undulation.");
		ImGui.SameLine();

		float lobeThick = (float)_settings.LobeThickness;
		if (ImGuiWidgets.Knob("Lobe thickness", ref lobeThick, 0.5f, 3f, format: "%.1fx", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.LobeThickness = lobeThick;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Stroke thickness of the Regulation Field's live trace. 1.0x is tuned default; higher = bolder lobes, lower = finer.");
		ImGui.SameLine();

		int segments = _settings.LobeSegments;
		if (ImGuiWidgets.Knob("Resolution", ref segments,
				LemniscateGeometry.MinSegments, LemniscateGeometry.MaxSegments,
				format: "%d pts", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.LobeSegments = segments;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("How finely the figure-8 outline is sampled. Higher = smoother curve; lower = more faceted.");
		ImGui.SameLine();

		float lobeOpacityPct = (float)(_settings.LobeOpacity * 100.0);
		if (ImGuiWidgets.Knob("Lobe opacity", ref lobeOpacityPct, 0f, 100f, format: "%.0f%%", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.LobeOpacity = lobeOpacityPct / 100.0;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Opacity of the Regulation Field's live-trace lobes. They draw additively and bloom where strokes overlap, so lower this if they saturate to white; default 60%.");
		ImGui.SameLine();

		int heatWindow = _settings.RegulationHeatmapLength;
		if (ImGuiWidgets.Knob("Heatmap window", ref heatWindow, 60, 518400, format: "%d pts", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.RegulationHeatmapLength = heatWindow;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("How many recent readings the Regulation Field dwell heatmap accumulates over — where you tend to sit, distinct from the shorter comet trail. Higher = longer dwell history (≈1 h at 720, up to ≈30 days).");
		ImGui.SameLine();

		float heatOpacityPct = (float)(_settings.HeatmapOpacity * 100.0);
		if (ImGuiWidgets.Knob("Heatmap opacity", ref heatOpacityPct, 0f, 100f, format: "%.0f%%", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.HeatmapOpacity = heatOpacityPct / 100.0;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Overall opacity of the dwell heatmap. 0 hides it; default 35% keeps it a faint underlay beneath the comet and marker.");
		ImGui.SameLine();

		float heatPeakOpacityPct = (float)(_settings.HeatmapPeakOpacity * 100.0);
		if (ImGuiWidgets.Knob("Peak crosshair", ref heatPeakOpacityPct, 0f, 100f, format: "%.0f%%", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.HeatmapPeakOpacity = heatPeakOpacityPct / 100.0;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Opacity of the crosshair pinning the dwell heatmap's busiest bucket — where regulation has settled most over the window. 0 hides it; default 70%.");
		ImGui.SameLine();

		float heatRegionOpacityPct = (float)(_settings.HeatmapRegionOpacity * 100.0);
		if (ImGuiWidgets.Knob("Dwell region", ref heatRegionOpacityPct, 0f, 100f, format: "%.0f%%", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.HeatmapRegionOpacity = heatRegionOpacityPct / 100.0;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Opacity of the dashed box framing the dwell heatmap's high-concentration region — the block of busy buckets around the peak crosshair. 0 hides it; default 55%.");
		ImGui.SameLine();

		float heatRegionThresholdPct = (float)(_settings.HeatmapRegionThreshold * 100.0);
		if (ImGuiWidgets.Knob("Region threshold", ref heatRegionThresholdPct, 0f, 100f, format: "%.0f%%", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.HeatmapRegionThreshold = heatRegionThresholdPct / 100.0;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("How busy a bucket must be — as a share of the peak bucket — to sit inside the dashed dwell region. Lower widens the box toward every visited bucket; higher tightens it around the peak. Default 50%.");
		ImGui.SameLine();

		float histOpacityPct = (float)(_settings.HistogramOpacity * 100.0);
		if (ImGuiWidgets.Knob("Histogram opacity", ref histOpacityPct, 0f, 100f, format: "%.0f%%", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.HistogramOpacity = histOpacityPct / 100.0;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Opacity of the Regulation Field axis histograms (the arousal and vagal-tone bars). They draw additively, so lower this if they saturate; default 60%.");
		ImGui.SameLine();

		int indexBuckets = _settings.FieldIndexBuckets;
		if (ImGuiWidgets.Knob("Arousal buckets", ref indexBuckets, 6, 64, format: "%d", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.FieldIndexBuckets = indexBuckets;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Bucket resolution of the arousal-index (X) axis — the number of arousal-histogram bars and dwell-heatmap columns. Higher = finer detail; default 24.");
		ImGui.SameLine();

		int vagalBuckets = _settings.FieldVagalBuckets;
		if (ImGuiWidgets.Knob("Tone buckets", ref vagalBuckets, 6, 64, format: "%d", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.FieldVagalBuckets = vagalBuckets;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Bucket resolution of the vagal-tone (Y) axis — the number of vagal-tone histogram bars and dwell-heatmap rows. Higher = finer detail; default 16.");
		ImGui.SameLine();

		float arrowSpeed = (float)_settings.RecoveryArrowSpeed;
		if (ImGuiWidgets.Knob("Recovery arrow speed", ref arrowSpeed, 0.1f, 3f, format: "%.1fx", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.RecoveryArrowSpeed = arrowSpeed;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("How fast the recovery arrows pulse inward toward the centre during a Warning/Alert. 0.7x is the tuned default; lower drifts in slowly, higher pulses in faster.");
		ImGui.SameLine();

		int arrowCount = _settings.RecoveryArrowCount;
		if (ImGuiWidgets.Knob("Recovery arrows", ref arrowCount, 1, 6, format: "%d", flags: ImGuiKnobOptions.ValueTooltip))
		{
			_settings.RecoveryArrowCount = arrowCount;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("How many arrows ride the inward-pulling recovery train. Default 3.");

		// ── Regulation Field blend modes ─────────────────────────────────
		// Each glow layer can bloom additively (overlaps add toward white — the signature look)
		// or composite with plain alpha (overlaps layer over, no bloom). On by default.
		ImGui.SeparatorText("Regulation Field blend modes");
		ImGui.TextDisabled("Checked = additive glow bloom; unchecked = flat alpha compositing.");

		BlendToggle("LF/HF halo (additive)", _settings.LfHfHaloAdditive, v => _settings.LfHfHaloAdditive = v,
			"How the LF/HF balance halo blends. Additive blooms the overlapping discs into a soft glow; unchecked composites them flat.");
		BlendToggle("Lobes (additive)", _settings.LobesAdditive, v => _settings.LobesAdditive = v,
			"How the live-trace lobes blend. Additive blooms overlapping strokes toward white; unchecked composites them flat.");
		BlendToggle("Trail (additive)", _settings.TrailAdditive, v => _settings.TrailAdditive = v,
			"How the comet trail blends. Additive blooms where the tail overlaps itself and the marker; unchecked composites it flat.");
		BlendToggle("Heatmap (additive)", _settings.HeatmapAdditive, v => _settings.HeatmapAdditive = v,
			"How the dwell-heatmap cells blend. Additive makes the busy buckets glow; unchecked composites them as flat tiles.");
		BlendToggle("Marker halo (additive)", _settings.MarkerHaloAdditive, v => _settings.MarkerHaloAdditive = v,
			"How the marker halos blend. Additive blooms them toward white where they overlap the trail head; unchecked composites them flat.");
		BlendToggle("Histogram (additive)", _settings.HistogramAdditive, v => _settings.HistogramAdditive = v,
			"How the axis histogram bars blend. Additive makes the bars glow; unchecked composites them flat.");

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

		// ── Motion corroboration ────────────────────────────────────────
		ImGui.SeparatorText("Motion corroboration (optional)");

		bool enableMotion = _settings.EnableMotionCorroboration;
		if (ImGui.Checkbox("Use Polar strap motion to corroborate (applies on next start)", ref enableMotion))
		{
			_settings.EnableMotionCorroboration = enableMotion;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Streams the Polar strap accelerometer and defers alerts / freezes the baseline while you're moving, so exercise isn't mistaken for dysregulation. No effect on non-Polar sensors. Takes effect when monitoring next starts.");

		using (new ScopedDisable(!enableMotion))
		{
			bool useGate = t.UseMovementGating;
			if (ImGui.Checkbox("Gate detection on movement", ref useGate))
			{
				t = t with { UseMovementGating = useGate };
				_settingsDirty = true;
			}
			ImGui.SameLine();
			HelpMarker("When on, movement at or above the chosen level defers Warning/Alerting and freezes the baseline. When off, motion is streamed but never gates.");
		}

		_settings.Thresholds = t;

		// ── Interval source ─────────────────────────────────────────────
		ImGui.SeparatorText("Interval source (applies on next start)");
		ImGui.TextDisabled("Which stream supplies beat-to-beat intervals:");
		foreach (IntervalSource src in Enum.GetValues<IntervalSource>())
		{
			if (ImGui.RadioButton(IntervalSourceLabel(src), _settings.PreferredIntervalSource == src))
			{
				_settings.PreferredIntervalSource = src;
				_settingsDirty = true;
			}

			ImGui.SameLine();
			HelpMarker(IntervalSourceHelp(src));
		}

		ImGui.NewLine();

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

		_settings.ChartTuning = ct;

		// ── Overlay mode ──────────────────────────────────────────────────
		ImGui.SeparatorText("Overlay mode (applies live)");

		var ov = _settings.Overlay;

		bool overlayEnabled = ov.Enabled;
		if (ImGui.Checkbox("Overlay mode", ref overlayEnabled))
		{
			ov.Enabled = overlayEnabled;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Turns the whole window into a borderless, translucent, always-on-top overlay. Defaults to a compact metrics HUD (with the Regulation Field); use Expand in the overlay's toolbar for the full UI. Drag the ':::' handle to reposition it.");

		bool expanded = ov.Expanded;
		if (ImGui.Checkbox("Expanded (full UI)", ref expanded))
		{
			ov.Expanded = expanded;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("In overlay mode, show the full tabbed UI instead of the compact HUD.");

		bool clickThrough = ov.ClickThrough;
		if (ImGui.Checkbox("Click-through (ignore mouse)", ref clickThrough))
		{
			ov.ClickThrough = clickThrough;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("When on, the overlay ignores the mouse so clicks reach the windows beneath. Toggle it back off from the tray menu, since the overlay's own controls are unclickable while it's on.");

		bool showField = ov.ShowRegulationField;
		if (ImGui.Checkbox("Show Regulation Field", ref showField))
		{
			ov.ShowRegulationField = showField;
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Show the figure-8 Regulation Field at the top of the compact HUD.");

		float opacity = ov.Opacity;
		if (ImGui.SliderFloat("Opacity", ref opacity, 0.2f, 1.0f, "%.2f"))
		{
			ov.Opacity = opacity;
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
		ImGui.SameLine();
		HelpMarker("Which screen corner the overlay locks to. Drag the ':::' handle in the overlay to nudge the offset from that corner.");

		int offsetX = ov.OffsetX;
		ImGui.SetNextItemWidth(120f);
		if (ImGui.InputInt("Offset X", ref offsetX))
		{
			ov.OffsetX = Math.Max(0, offsetX);
			_settingsDirty = true;
		}
		ImGui.SameLine();
		int offsetY = ov.OffsetY;
		ImGui.SetNextItemWidth(120f);
		if (ImGui.InputInt("Offset Y", ref offsetY))
		{
			ov.OffsetY = Math.Max(0, offsetY);
			_settingsDirty = true;
		}

		int overlayW = ov.Width;
		ImGui.SetNextItemWidth(120f);
		if (ImGui.InputInt("Width", ref overlayW))
		{
			ov.Width = Math.Max(200, overlayW);
			_settingsDirty = true;
		}
		ImGui.SameLine();
		int overlayH = ov.Height;
		ImGui.SetNextItemWidth(120f);
		if (ImGui.InputInt("Height (expanded)", ref overlayH))
		{
			ov.Height = Math.Max(140, overlayH);
			_settingsDirty = true;
		}
		ImGui.SameLine();
		HelpMarker("Overlay width is fixed at this value (drag the grip in the overlay to resize it). In the compact HUD the height auto-fits the selected metrics with no scrollbar; the Height value only applies to the expanded view.");

		ImGui.TextDisabled("HUD metrics:");
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

	private static string IntervalSourceLabel(IntervalSource source) => source switch
	{
		IntervalSource.PolarPpi => "Polar PPI",
		IntervalSource.PolarEcg => "Polar ECG",
		_ => "Heart-rate service",
	};

	private static string IntervalSourceHelp(IntervalSource source) => source switch
	{
		IntervalSource.PolarPpi => "Polar Verity Sense PMD peak-to-peak intervals — optical, with per-beat quality flags that sharpen artifact rejection. Falls back to HRS RR on devices without it.",
		IntervalSource.PolarEcg => "RR from the Polar H10's raw ECG R-peaks — gold-standard fidelity. Falls back to HRS RR on devices without it.",
		_ => "Standard Bluetooth Heart Rate RR intervals. Works with every supported sensor.",
	};

	// A labelled blend-mode checkbox with a trailing help marker. Properties can't be passed by
	// ref, so the current value is read in and the setter is invoked on change.
	private void BlendToggle(string label, bool current, Action<bool> set, string help)
	{
		bool value = current;
		if (ImGui.Checkbox(label, ref value))
		{
			set(value);
			_settingsDirty = true;
		}

		ImGui.SameLine();
		HelpMarker(help);
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
				_settings.RegulationTrailLength = 48;
				_settings.RegulationHeatmapLength = 720;
				_settings.HeatmapOpacity = 0.35;
				_settings.HeatmapPeakOpacity = 0.7;
				_settings.HeatmapRegionOpacity = 0.55;
				_settings.HeatmapRegionThreshold = 0.5;
				_settings.JitterExaggeration = 1.0;
				_settings.LobeThickness = 1.0;
				_settings.LobeSegments = LemniscateGeometry.DefaultSegments;
				_settings.LobeOpacity = 0.6;
				_settings.TrailOpacity = 0.7;
				_settings.HistogramOpacity = 0.6;
				_settings.FieldIndexBuckets = 24;
				_settings.FieldVagalBuckets = 16;
				_settings.RecoveryArrowSpeed = 0.7;
				_settings.RecoveryArrowCount = 3;
				_settings.LfHfHaloAdditive = true;
				_settings.LobesAdditive = true;
				_settings.TrailAdditive = true;
				_settings.HeatmapAdditive = true;
				_settings.MarkerHaloAdditive = true;
				_settings.HistogramAdditive = true;
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

	// Seconds of history the live charts span — the fixed, scrolling x-window width.
	private double WindowSeconds => Math.Max(1.0, _settings.SparklineWindowMinutes * 60.0);

	private static double NowEpochSeconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

	// Configure the shared time-relative x-axis: a fixed window [-window, +pad] re-asserted
	// every frame (so it scrolls with 'now' even when no new data arrives), with relative
	// tick labels. The Y axis flags are caller-chosen (auto-fit for value charts; locked
	// 0..1 with no labels for the contact strip).
	private void SetupTimeAxis(ImPlotAxisFlags yFlags = ImPlotAxisFlags.AutoFit)
	{
		double window = WindowSeconds;
		double rightPad = window * 0.02; // headroom so the newest point doesn't hug the edge

		ImPlot.SetupAxis(ImAxis.X1, string.Empty, ImPlotAxisFlags.None);
		ImPlot.SetupAxis(ImAxis.Y1, string.Empty, yFlags);
		ImPlot.SetupAxisLimits(ImAxis.X1, -window, rightPad, ImPlotCond.Always);

		(double[] positions, string[] labels) = RelativeTimeAxis.Ticks(window);
		if (positions.Length > 0)
		{
			ImPlot.SetupAxisTicks(ImAxis.X1, ref positions[0], positions.Length, labels);
		}
	}

	private static float[] ToFloat(double[] values)
	{
		var result = new float[values.Length];
		for (int i = 0; i < values.Length; i++)
		{
			result[i] = (float)values[i];
		}

		return result;
	}

	// Build the RR plot's (x, y): x is the cumulative-RR time axis (newest beat at 0,
	// seconds), y is the RR interval in ms. Reconstructed because batched beats share a
	// timestamp — see RrTimeAxis.
	private static (float[] xs, float[] ys) RrSeries(double[] rrMs)
	{
		double[] secs = RrTimeAxis.CumulativeSeconds(rrMs);
		var xs = ToFloat(secs);
		float[] ys = ToFloat(rrMs);

		return (xs, ys);
	}

	private void Plot(string title, float[] xs, float[] ys, Vector2 size)
	{
		// Always draw the frame (even with no data) so rows stay aligned.
		if (ImPlot.BeginPlot(title, size, ImPlotFlags.NoLegend | ImPlotFlags.NoMouseText))
		{
			SetupTimeAxis();
			if (ys.Length >= 2)
			{
				ImPlot.PlotLine(title, ref xs[0], ref ys[0], ys.Length);
			}

			ImPlot.EndPlot();
		}
	}

	// One comparison chart (a series plus its baseline) sharing a single auto-fit Y axis,
	// capped to MaxPlotAspect and centred in the available width.
	private void PlotPair(float height, string title,
		string aLabel, float[] aXs, float[] aYs,
		string bLabel, float[] bXs, float[] bYs)
	{
		(Vector2 size, float indent) = CenteredCell(ImGui.GetContentRegionAvail().X, height);
		Indent(indent);

		if (ImPlot.BeginPlot(title, size, ImPlotFlags.NoMouseText))
		{
			SetupTimeAxis();

			if (aYs.Length >= 2)
			{
				ImPlot.PlotLine(aLabel, ref aXs[0], ref aYs[0], aYs.Length);
			}
			if (bYs.Length >= 2)
			{
				ImPlot.PlotLine(bLabel, ref bXs[0], ref bYs[0], bYs.Length);
			}

			ImPlot.EndPlot();
		}
	}

	// Lay out N plots in a single row, each sharing the width equally (capped to
	// MaxPlotAspect) and the group centred. Handles a single plot too.
	private void PlotRow(float height, params (string label, float[] xs, float[] ys)[] plots)
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
			Plot(plots[i].label, plots[i].xs, plots[i].ys, size);
			if (i < n - 1)
			{
				ImGui.SameLine();
			}
		}
	}

	// Height for each of `rows` stacked chart rows so they fill the remaining tab height
	// (small safety margin so a fractional pixel never spawns a scrollbar).
	private static float FillRowHeight(int rows, float reservePx = 0f)
	{
		float avail = ImGui.GetContentRegionAvail().Y;
		float sp = ImGui.GetStyle().ItemSpacing.Y;
		float h = (avail - (sp * (rows - 1)) - reservePx - 4f) / rows;
		return MathF.Max(80f, h);
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
		_pipeline.BatteryUpdated -= OnBatteryUpdated;
		_historyRefreshAction.Stop();
	}

	public void Dispose()
	{
		Close();
		ReleaseSubscriptions();
		_regulationField.Dispose();
	}
}
