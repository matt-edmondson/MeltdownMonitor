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
	private readonly Action _showStatusWindow;
	private readonly Action _quit;

	private static readonly Dictionary<DetectorState, Color> StateColors = new()
	{
		[DetectorState.Idle] = Color.Gray,
		[DetectorState.Watching] = Color.Green,
		[DetectorState.Warning] = Color.Orange,
		[DetectorState.Alerting] = Color.Red,
		[DetectorState.Cooldown] = Color.DodgerBlue,
	};

	public TrayIcon(
		Pipeline pipeline,
		MeltdownRepository repository,
		AppSettings settings,
		Action showStatusWindow,
		Action quit)
	{
		_pipeline = pipeline;
		_repository = repository;
		_settings = settings;
		_showStatusWindow = showStatusWindow;
		_quit = quit;

		_notifyIcon = new NotifyIcon
		{
			Visible = true,
			Text = "Meltdown Monitor",
			Icon = BuildIcon(Color.Gray),
			ContextMenuStrip = BuildContextMenu(),
		};

		_notifyIcon.DoubleClick += (_, _) => _showStatusWindow();
		_pipeline.SampleUpdated += s => UpdateIcon(s.State);
	}

	private void UpdateIcon(DetectorState state)
	{
		var color = StateColors.GetValueOrDefault(state, Color.Gray);
		_notifyIcon.Icon?.Dispose();
		_notifyIcon.Icon = BuildIcon(color);
		_notifyIcon.Text = $"Meltdown Monitor — {state}";
	}

	private static Icon BuildIcon(Color color)
	{
		var bmp = new Bitmap(16, 16);
		using var g = Graphics.FromImage(bmp);
		g.Clear(color);
		return Icon.FromHandle(bmp.GetHicon());
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
		menu.Items.Add("Show status window", null, (_, _) => _showStatusWindow());
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
	}
}
