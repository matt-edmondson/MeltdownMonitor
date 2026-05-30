using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.App;

/// <summary>
/// System tray icon. Colour changes with detector state and the context menu
/// exposes the primary user actions.
/// </summary>
public sealed class TrayIcon : IDisposable
{
	private readonly NotifyIcon _notifyIcon;
	private readonly Pipeline _pipeline;
	private readonly MeltdownRepository _repository;
	private readonly AppSettings _settings;
	private readonly Action _toggleStatusWindow;
	private readonly Action _toggleOverlay;
	private readonly Action _quit;
	private readonly Dictionary<DetectorState, Icon> _stateIcons;

	// Regulation Lemniscate tray glyphs (Catppuccin Macchiato), embedded per detector state.
	// Logical names match the EmbeddedResource entries in MeltdownMonitor.App.csproj.
	private static readonly IReadOnlyDictionary<DetectorState, string> StateIconResources = new Dictionary<DetectorState, string>
	{
		[DetectorState.Idle] = "MeltdownMonitor.App.Tray.Idle.ico",
		[DetectorState.Watching] = "MeltdownMonitor.App.Tray.Watching.ico",
		[DetectorState.Warning] = "MeltdownMonitor.App.Tray.Warning.ico",
		[DetectorState.Alerting] = "MeltdownMonitor.App.Tray.Alerting.ico",
		[DetectorState.Cooldown] = "MeltdownMonitor.App.Tray.Cooldown.ico",
	};

	public TrayIcon(
		Pipeline pipeline,
		MeltdownRepository repository,
		AppSettings settings,
		Action toggleStatusWindow,
		Action toggleOverlay,
		Action quit)
	{
		_pipeline = pipeline;
		_repository = repository;
		_settings = settings;
		_toggleStatusWindow = toggleStatusWindow;
		_toggleOverlay = toggleOverlay;
		_quit = quit;

		_stateIcons = StateIconResources.ToDictionary(
			static entry => entry.Key,
			static entry => LoadIcon(entry.Value));

		_notifyIcon = new NotifyIcon
		{
			Visible = true,
			Text = "Meltdown Monitor",
			Icon = IconFor(DetectorState.Idle),
			ContextMenuStrip = BuildContextMenu(),
		};

		_notifyIcon.DoubleClick += (_, _) => _toggleStatusWindow();
		_pipeline.SampleUpdated += s => UpdateIcon(s.State);
	}

	private void UpdateIcon(DetectorState state)
	{
		// Icons are cached for the tray's lifetime, so assign without disposing.
		_notifyIcon.Icon = IconFor(state);
		_notifyIcon.Text = $"Meltdown Monitor — {state}";
	}

	private Icon IconFor(DetectorState state) =>
		_stateIcons.TryGetValue(state, out var icon) ? icon : _stateIcons[DetectorState.Idle];

	private static Icon LoadIcon(string resourceName)
	{
		var assembly = typeof(TrayIcon).Assembly;
		using var stream = assembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException($"Embedded tray icon '{resourceName}' was not found.");
		// Pick the embedded size that matches the current DPI's small-icon size.
		return new Icon(stream, SystemInformation.SmallIconSize);
	}

	private ContextMenuStrip BuildContextMenu()
	{
		var menu = new ContextMenuStrip();

		var stateItem = new ToolStripMenuItem("State: Idle") { Enabled = false };
		_pipeline.SampleUpdated += s => stateItem.Text = $"State: {s.State}  RMSSD: {s.Rmssd:F1}  HR: {s.MeanHr:F0}";

		menu.Items.Add(stateItem);
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add("Log how I'm feeling...", null, OnLogFeeling);
		menu.Items.Add("Pause for 1 hour", null, OnPause);
		menu.Items.Add("Show/hide status window", null, (_, _) => _toggleStatusWindow());
		menu.Items.Add("Show/hide metrics overlay", null, (_, _) => _toggleOverlay());
		menu.Items.Add("Open log folder", null, OnOpenLogFolder);
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add("Quit", null, (_, _) => _quit());

		return menu;
	}

	private void OnLogFeeling(object? sender, EventArgs e)
	{
		using var dlg = new AnnotationDialog();
		if (dlg.ShowDialog() == DialogResult.OK)
		{
			_repository.InsertAnnotation(DateTimeOffset.UtcNow, dlg.SelectedLabel, dlg.Notes);
		}
	}

	private void OnPause(object? sender, EventArgs e)
	{
		_settings.PausedUntil = DateTimeOffset.UtcNow.AddHours(1);
		_settings.Save();
	}

	private void OnOpenLogFolder(object? sender, EventArgs e)
	{
		string folder = Path.GetDirectoryName(_settings.DatabasePath) ?? string.Empty;
		if (Directory.Exists(folder))
		{
			System.Diagnostics.Process.Start("explorer.exe", folder);
		}
	}

	public void Dispose()
	{
		_notifyIcon.Dispose();
		foreach (var icon in _stateIcons.Values)
		{
			icon.Dispose();
		}
	}
}
