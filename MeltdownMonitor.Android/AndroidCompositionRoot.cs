using Avalonia.Threading;
using MeltdownMonitor.Android.Services;
using MeltdownMonitor.Ble.Android;
using MeltdownMonitor.Core.Diagnostics;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;
using MeltdownMonitor.Mobile.ViewModels;
using AndroidApplication = Android.App.Application;
using Context = Android.Content.Context;

namespace MeltdownMonitor.Android;

/// <summary>
/// Wires the platform-neutral Mobile view models to the Android-specific
/// services (notifications, chime, SharedPreferences, Health Connect, the
/// ongoing-notification status surface, database export) and, once Avalonia is
/// up, composes and starts the live BLE pipeline — the Android counterpart to
/// <c>IosCompositionRoot</c> (design doc §5 / §8 / §13).
///
/// <para>
/// The pipeline is held here as application-scoped static state and kept alive
/// by the foreground <see cref="MonitoringService"/>, so a recreated Activity
/// (rotation, memory pressure) rebinds to the running pipeline instead of
/// restarting it (design doc §5.8).
/// </para>
/// </summary>
public static class AndroidCompositionRoot
{
	private static MobileAlertDispatcher? _alertDispatcher;
	private static LiveActivityPublisher? _liveActivity;
	private static HealthKitEpisodeRecorder? _episodeRecorder;
	private static AndroidNotificationDispatcher? _notifications;
	private static HealthConnectStore? _healthStore;
	private static SharedPreferencesSettingsStore? _store;
	private static MobileSettings? _settings;
	private static NowViewModel? _now;
	private static HistoryViewModel? _history;
	private static MetricsViewModel? _metrics;
	private static AndroidBleSource? _source;
	private static Pipeline? _pipeline;
	private static IDisposable? _crashReporting;

	private static Context AppContext =>
		AndroidApplication.Context;

	/// <summary>The running pipeline once composed, or null before then.</summary>
	public static Pipeline? Pipeline => _pipeline;

	/// <summary>
	/// On-disk location of the SQLite database:
	/// <c>FilesDir/meltdownmonitor/data.db</c> (design doc §5.7). Deterministic,
	/// so it can be resolved before the pipeline is composed (e.g. for export).
	/// </summary>
	public static string DatabasePath()
	{
		string filesDir = AppContext.FilesDir!.AbsolutePath;
		return Path.Combine(filesDir, "meltdownmonitor", "data.db");
	}

	private static void InitializeCrashReporting(string? configuredDsn) =>
		_crashReporting ??= CrashReporting.Initialize(new CrashReportingOptions
		{
			Dsn = configuredDsn,
			Environment = "android",
			Release = typeof(AndroidCompositionRoot).Assembly.GetName().Version?.ToString(),
		});

	public static RootViewModel BuildRootViewModel()
	{
		var context = AppContext;
		_store = new SharedPreferencesSettingsStore(context);
		var settings = _store.Load();
		_settings = settings;

		InitializeCrashReporting(settings.CrashReportingDsn);

		_notifications = new AndroidNotificationDispatcher(context, settings);
		_healthStore = new HealthConnectStore();
		var exporter = new IntentDatabaseExporter(context);

		_now = new NowViewModel(
			onAnnotate: RecordAnnotationAsync,
			trailLengthProvider: () => settings.RegulationTrailLength,
			jitterExaggerationProvider: () => settings.JitterExaggeration,
			lobeThicknessProvider: () => settings.LobeThickness,
			indexBucketsProvider: () => settings.FieldIndexBuckets,
			vagalBucketsProvider: () => settings.FieldVagalBuckets,
			lobeSegmentsProvider: () => settings.LobeSegments,
			heatmapLengthProvider: () => settings.RegulationHeatmapLength,
			lobeOpacityProvider: () => settings.LobeOpacity,
			trailOpacityProvider: () => settings.TrailOpacity,
			histogramOpacityProvider: () => settings.HistogramOpacity,
			heatmapOpacityProvider: () => settings.HeatmapOpacity,
			heatmapPeakOpacityProvider: () => settings.HeatmapPeakOpacity,
			heatmapRegionOpacityProvider: () => settings.HeatmapRegionOpacity,
			heatmapRegionThresholdProvider: () => settings.HeatmapRegionThreshold,
			useLfHfCorroborationProvider: () => settings.Thresholds.UseLfHfCorroboration,
			lfHfHaloAdditiveProvider: () => settings.LfHfHaloAdditive,
			lobesAdditiveProvider: () => settings.LobesAdditive,
			trailAdditiveProvider: () => settings.TrailAdditive,
			heatmapAdditiveProvider: () => settings.HeatmapAdditive,
			markerHaloAdditiveProvider: () => settings.MarkerHaloAdditive,
			histogramAdditiveProvider: () => settings.HistogramAdditive);
		_history = new HistoryViewModel();
		_metrics = new MetricsViewModel(
			windowMinutesProvider: () => settings.SparklineWindowMinutes,
			emitIntervalProvider: () => settings.HrvEmitIntervalSeconds);

		var settingsTab = new SettingsViewModel(
			settings,
			requestNotifications: () => _notifications.RequestAuthorizationAsync(),
			requestHealthKit: () => _healthStore.RequestAuthorizationAsync(),
			exportDatabase: () => exporter.ExportAsync(DatabasePath()),
			onChanged: () => _store.Save(settings));

		return new RootViewModel(settings, _now, _history, settingsTab, _metrics, _store);
	}

	/// <summary>
	/// Opens the repository, constructs the BLE source, warm-starts the baseline,
	/// starts the pipeline and the foreground service, then feeds the live streams
	/// to the view models (design doc §13 Phase 3). Heavy work runs off the UI
	/// thread; view-model wiring is marshalled back onto it. Idempotent.
	/// </summary>
	public static async Task BuildAndStartPipelineAsync()
	{
		if (_pipeline is not null || _settings is null)
		{
			return;
		}

		var settings = _settings;
		var context = AppContext;
		string dbPath = DatabasePath();
		Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

		// WAL is appropriate on Android: file-based encryption keys are available
		// after first unlock and the foreground service keeps the process warm, so
		// the iOS TRUNCATE/fullfsync workaround is not needed (design doc §5.7).
		var repository = new MeltdownRepository(dbPath, MeltdownRepositoryOptions.Default);

		_source = new AndroidBleSource(context, settings.DeviceType);
		var pipeline = new Pipeline(settings, repository, _source);

		// Warm-start must precede Start. Best-effort — no Health Connect data just
		// means a cold baseline, not a failure (design doc §5.3).
		await pipeline.WarmStartAsync(_healthStore, TimeSpan.FromHours(24)).ConfigureAwait(false);
		pipeline.Start();

		// Keep the process — and the GATT connection — alive with the screen off.
		// Not gated on the Live Activity opt-in: background monitoring is the point.
		MonitoringService.Start(context);

		AttachAlertDispatcher(pipeline, settings, new AndroidChimePlayer(context));
		if (_healthStore is not null)
		{
			_episodeRecorder = new HealthKitEpisodeRecorder(pipeline, settings, _healthStore);
		}

		// Mirror state/HR/balance to the ongoing notification, throttled to ≤ 1 Hz
		// (design doc §5.5). Opt-in via settings; the publisher honours the flag.
		_liveActivity = new LiveActivityPublisher(
			pipeline, new OngoingNotificationActivityController(context), settings);

		_pipeline = pipeline;

		if (_metrics is not null)
		{
			pipeline.SampleUpdated += _metrics.OnSampleUpdated;
			pipeline.BeatReceived += _metrics.OnBeatReceived;
			pipeline.BatteryUpdated += _metrics.OnBatteryUpdated;
			_ = _metrics.LoadFromRepositoryAsync(dbPath);
		}

		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			_now?.AttachPipeline(pipeline);
			_history?.UseDatabase(dbPath);
		});
	}

	/// <summary>Keeps the alert dispatcher alive for the process lifetime.</summary>
	public static void AttachAlertDispatcher(Pipeline pipeline, MobileSettings settings, IChimePlayer chime)
	{
		if (_notifications is null)
		{
			return;
		}

		_alertDispatcher?.Dispose();
		_alertDispatcher = new MobileAlertDispatcher(pipeline, settings, _notifications, chime);
	}

	/// <summary>
	/// Persists a Now-screen self check-in (design doc §5) and refreshes History so
	/// it shows immediately. The write goes through a short-lived connection off the
	/// UI thread, mirroring the iOS composition root.
	/// </summary>
	private static async Task RecordAnnotationAsync(AnnotationLabel label, string? notes)
	{
		string dbPath = DatabasePath();
		await Task.Run(() => MeltdownRepository.WriteAnnotation(dbPath, DateTimeOffset.UtcNow, label, notes))
			.ConfigureAwait(true);

		if (_history is not null)
		{
			await _history.LoadAsync().ConfigureAwait(true);
		}
	}

	/// <summary>
	/// Graceful stop: dismiss the live status surface, stop the foreground service,
	/// and stop the pipeline so the repository's last writes flush. Bounded so it
	/// never hangs a lifecycle callback.
	/// </summary>
	public static async Task StopAsync(TimeSpan timeout)
	{
		if (_pipeline is null)
		{
			return;
		}

		if (_liveActivity is not null)
		{
			await _liveActivity.StopAsync().ConfigureAwait(false);
		}

		MonitoringService.Stop(AppContext);

		var stop = _pipeline.StopAsync();
		await Task.WhenAny(stop, Task.Delay(timeout)).ConfigureAwait(false);
	}
}
