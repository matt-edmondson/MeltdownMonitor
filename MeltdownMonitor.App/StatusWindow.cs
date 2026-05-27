using ImGuiNET;
using ktsu.ImGuiApp;
using ktsu.IntervalAction;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;
using System.Numerics;

namespace MeltdownMonitor.App;

/// <summary>
/// Optional status window that shows a live RMSSD vs. baseline sparkline
/// and the current detector state. Toggled from the tray menu.
/// </summary>
public class StatusWindow : ImGuiApp, IDisposable
{
	private const int SparklineMaxPoints = 720; // 1 hour at 5s cadence

	private readonly Queue<float> _rmssdHistory = new();
	private readonly Queue<float> _baselineHistory = new();
	private readonly object _historyLock = new();
	private readonly Pipeline _pipeline;
	private readonly MeltdownRepository _repository;
	private readonly AppSettings _settings;
	private readonly IntervalAction _historyRefreshAction;

	public StatusWindow(Pipeline pipeline, MeltdownRepository repository, AppSettings settings)
		: base(nameof(StatusWindow), new ImGuiAppWindowConfig
		{
			Title = "Meltdown Monitor — Status",
			InitialSize = new Vector2(800, 400),
		})
	{
		_pipeline = pipeline;
		_repository = repository;
		_settings = settings;

		_pipeline.SampleUpdated += OnSampleUpdated;

		// Fires immediately on first poll, then every 5 minutes —
		// backfills the sparkline with history so it isn't empty on open.
		_historyRefreshAction = IntervalAction.Start(new IntervalActionOptions
		{
			Action = BackfillSparklineFromRepository,
			ActionInterval = TimeSpan.FromMinutes(5),
			PollingInterval = TimeSpan.FromSeconds(10),
		});
	}

	private void OnSampleUpdated(HrvSample sample)
	{
		lock (_historyLock)
		{
			_rmssdHistory.Enqueue((float)sample.Rmssd);
			_baselineHistory.Enqueue((float)sample.BaselineRmssd);

			while (_rmssdHistory.Count > SparklineMaxPoints)
			{
				_rmssdHistory.Dequeue();
				_baselineHistory.Dequeue();
			}
		}
	}

	private void BackfillSparklineFromRepository()
	{
		var to = DateTimeOffset.UtcNow;
		var from = to.AddHours(-1);

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
			_rmssdHistory.Clear();
			_baselineHistory.Clear();

			foreach (var s in samples.TakeLast(SparklineMaxPoints))
			{
				_rmssdHistory.Enqueue((float)s.Rmssd);
				_baselineHistory.Enqueue((float)s.BaselineRmssd);
			}
		}
	}

	protected override void OnUpdate()
	{
		var latest = _pipeline.LatestSample;

		ImGui.Text($"State: {_pipeline.CurrentState}");

		if (latest is not null)
		{
			ImGui.SameLine();
			ImGui.Text($"   RMSSD: {latest.Rmssd:F1} ms   Baseline: {latest.BaselineRmssd:F1} ms   HR: {latest.MeanHr:F0} bpm");
		}

		ImGui.Separator();

		float[] rmssdArr;
		float[] baselineArr;
		lock (_historyLock)
		{
			rmssdArr = [.. _rmssdHistory];
			baselineArr = [.. _baselineHistory];
		}

		if (rmssdArr.Length > 1)
		{
			var size = new Vector2(ImGui.GetContentRegionAvail().X, 200);
			ImGui.PlotLines("##rmssd", ref rmssdArr[0], rmssdArr.Length, 0,
				"RMSSD (ms)", 0, Math.Max(rmssdArr.Max() * 1.2f, 100), size);

			ImGui.PlotLines("##baseline", ref baselineArr[0], baselineArr.Length, 0,
				"Baseline RMSSD (ms)", 0, Math.Max(baselineArr.Max() * 1.2f, 100), size);
		}
		else
		{
			ImGui.TextDisabled("Waiting for data (≥10 min warm-up required)...");
		}

		ImGui.Separator();
		ImGui.Text("Log annotation:");
		ImGui.SameLine();

		foreach (AnnotationLabel label in Enum.GetValues<AnnotationLabel>())
		{
			if (ImGui.Button(label.ToString()))
			{
				_repository.InsertAnnotation(DateTimeOffset.UtcNow, label);
			}

			ImGui.SameLine();
		}

		ImGui.NewLine();
	}

	public void Dispose()
	{
		_pipeline.SampleUpdated -= OnSampleUpdated;
		_historyRefreshAction.Dispose();
		GC.SuppressFinalize(this);
	}
}
