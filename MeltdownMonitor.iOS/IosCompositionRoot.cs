using Avalonia.Threading;
using Foundation;
using MeltdownMonitor.Ble.Apple;
using MeltdownMonitor.Core.Diagnostics;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.iOS.Services;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.iOS;

/// <summary>
/// Wires the platform-neutral Mobile view models to the iOS-specific
/// services (notifications, audio chime, NSUserDefaults, HealthKit) and, once
/// Avalonia is up, composes and starts the live BLE pipeline (design doc
/// §6.1). Lives in the head project so the Mobile assembly never takes a
/// CoreBluetooth / UserNotifications / AVFoundation / HealthKit dependency.
/// </summary>
public static class IosCompositionRoot
{
	private static MobileAlertDispatcher? _alertDispatcher;
	private static LiveActivityPublisher? _liveActivity;
	private static HealthKitEpisodeRecorder? _episodeRecorder;
	private static NotificationDispatcher? _notifications;
	private static HealthKitStore? _healthStore;
	private static NSUserDefaultsSettingsStore? _store;
	private static MobileSettings? _settings;
	private static NowViewModel? _now;
	private static HistoryViewModel? _history;
	private static PolarHrSource? _source;
	private static Pipeline? _pipeline;

	// Sentry SDK handle, kept alive for the app's lifetime so queued crash
	// reports flush. Null when no DSN is configured (crash reporting off).
	private static IDisposable? _crashReporting;

	/// <summary>HealthKit facade kept alive for the app's lifetime so the
	/// live pipeline can warm-start from it on every relaunch.</summary>
	public static IHealthStore? HealthStore => _healthStore;

	/// <summary>The running pipeline once <see cref="BuildAndStartPipelineAsync"/>
	/// has composed it, or null before then. Used by the app delegate for
	/// background-lifecycle bookkeeping (design doc §6.2).</summary>
	public static Pipeline? Pipeline => _pipeline;

	/// <summary>
	/// Sandbox location of the SQLite database:
	/// <c>Library/Application Support/MeltdownMonitor/data.db</c>
	/// (design doc §10). Deterministic, so it can be resolved before the
	/// pipeline is composed (e.g. for the export command).
	/// </summary>
	public static string DatabasePath()
	{
		string appSupport = NSFileManager.DefaultManager
			.GetUrls(NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User)[0]
			.Path!;
		return Path.Combine(appSupport, "MeltdownMonitor", "data.db");
	}

	public static RootViewModel BuildRootViewModel()
	{
		_store = new NSUserDefaultsSettingsStore();
		var settings = _store.Load();
		_settings = settings;

		// Initialize crash reporting before anything heavy spins up. No-op (and
		// no network) unless a DSN is configured in settings or the
		// MELTDOWN_CRASH_REPORTING_DSN environment variable.
		_crashReporting ??= CrashReporting.Initialize(new CrashReportingOptions
		{
			Dsn = settings.CrashReportingDsn,
			Environment = "ios",
			Release = typeof(IosCompositionRoot).Assembly.GetName().Version?.ToString(),
		});

		_notifications = new NotificationDispatcher(settings);
		_healthStore = new HealthKitStore();
		var exporter = new ShareSheetDatabaseExporter();

		_now = new NowViewModel(
			onAnnotate: RecordAnnotationAsync,
			trailLengthProvider: () => settings.RegulationTrailLength,
			jitterExaggerationProvider: () => settings.JitterExaggeration,
			lobeThicknessProvider: () => settings.LobeThickness,
			indexBucketsProvider: () => settings.FieldIndexBuckets,
			vagalBucketsProvider: () => settings.FieldVagalBuckets,
			lobeSegmentsProvider: () => settings.LobeSegments);
		_history = new HistoryViewModel();

		var settingsTab = new SettingsViewModel(
			settings,
			requestNotifications: () => _notifications.RequestAuthorizationAsync(),
			requestHealthKit: () => _healthStore.RequestAuthorizationAsync(),
			exportDatabase: () => exporter.ExportAsync(DatabasePath()),
			onChanged: () => _store.Save(settings));

		return new RootViewModel(settings, _now, _history, settingsTab, _store);
	}

	/// <summary>
	/// Opens the repository, constructs the BLE source, warm-starts the
	/// baseline from HealthKit, then starts the pipeline and feeds it to the
	/// view models (design doc §6.1). Heavy work runs off the UI thread; the
	/// final view-model wiring is marshalled back onto it. Idempotent — a
	/// second call while a pipeline is already running is a no-op.
	/// </summary>
	public static async Task BuildAndStartPipelineAsync()
	{
		if (_pipeline is not null || _settings is null)
		{
			return;
		}

		var settings = _settings;
		string dbPath = DatabasePath();
		Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

		// TRUNCATE + fullfsync so the DB survives data-protection lock-outs
		// while the app is backgrounded (design doc §4.7).
		var repository = new MeltdownRepository(dbPath, MeltdownRepositoryOptions.IosSandbox);
		ProtectDatabaseFile(dbPath);

		_source = new PolarHrSource(settings.DeviceType);
		var pipeline = new Pipeline(settings, repository, _source);

		// Warm-start must precede Start (the existing contract). Best-effort —
		// no HealthKit auth just means a cold baseline, not a failure.
		await pipeline.WarmStartAsync(_healthStore, TimeSpan.FromHours(24)).ConfigureAwait(false);
		pipeline.Start();

		AttachAlertDispatcher(pipeline, settings, new AudioChimePlayer());
		if (_healthStore is not null)
		{
			_episodeRecorder = new HealthKitEpisodeRecorder(pipeline, settings, _healthStore);
		}

		// Mirror state/HR/balance to the Lock Screen and Dynamic Island, throttled
		// to ≤ 1 Hz (design doc §4.5 / Phase 8). Opt-in via settings; the publisher
		// itself honours the flag, starting the activity only when enabled.
		_liveActivity = new LiveActivityPublisher(pipeline, new LiveActivityController(), settings);

		_pipeline = pipeline;

		// ObservableCollection mutations and pill updates must land on the UI
		// thread, regardless of which thread the warm-start finished on.
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			_now?.AttachPipeline(pipeline);
			_history?.UseDatabase(dbPath);
		});
	}

	/// <summary>
	/// Hook for when the live pipeline is composed — keeps the alert
	/// dispatcher alive for the app's lifetime. Safe to call before or
	/// after <see cref="BuildRootViewModel"/>.
	/// </summary>
	public static void AttachAlertDispatcher(
		Pipeline pipeline,
		MobileSettings settings,
		IChimePlayer chime)
	{
		if (_notifications is null)
		{
			return;
		}

		_alertDispatcher?.Dispose();
		_alertDispatcher = new MobileAlertDispatcher(pipeline, settings, _notifications, chime);
	}

	/// <summary>
	/// Persists a Now-screen self check-in (design doc §5) and refreshes the
	/// History tab so it shows up immediately. The write goes through a
	/// short-lived connection of its own (<see cref="MeltdownRepository.WriteAnnotation"/>)
	/// rather than the pipeline's live connection, and runs off the UI thread.
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
	/// Foreground return (design doc §6.2): the Avalonia views may have torn
	/// down while backgrounded, so refresh the state pill from the live
	/// pipeline and reload the history list.
	/// </summary>
	public static void OnEnterForeground()
	{
		if (_pipeline is null)
		{
			return;
		}

		Dispatcher.UIThread.Post(() =>
		{
			_now?.OnStateChanged(_pipeline.CurrentState);
			_ = _history?.LoadAsync();
		});
	}

	/// <summary>
	/// Graceful shutdown (design doc §6.2 — <c>WillTerminate</c>): stop the
	/// pipeline so the repository's last writes flush before iOS reclaims us.
	/// Bounded so we never hang the terminate callback.
	/// </summary>
	public static async Task StopAsync(TimeSpan timeout)
	{
		if (_pipeline is null)
		{
			return;
		}

		// Dismiss the Live Activity so the Lock Screen doesn't keep a stale card
		// after we're gone (design doc §6.2 — graceful terminate).
		if (_liveActivity is not null)
		{
			await _liveActivity.StopAsync().ConfigureAwait(false);
		}

		var stop = _pipeline.StopAsync();
		await Task.WhenAny(stop, Task.Delay(timeout)).ConfigureAwait(false);
	}

	private static void ProtectDatabaseFile(string dbPath)
	{
		// Background BLE callbacks need to write while the device is locked, so
		// CompleteUntilFirstUserAuthentication (not Complete) is the right class
		// (design doc §4.7 / §10).
		var attributes = new NSFileAttributes
		{
			ProtectionKey = NSFileProtection.CompleteUntilFirstUserAuthentication,
		};
		NSFileManager.DefaultManager.SetAttributes(attributes, dbPath);
	}
}
