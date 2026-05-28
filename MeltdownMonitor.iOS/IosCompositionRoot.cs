using Foundation;
using MeltdownMonitor.Ble.Apple;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.iOS.Services;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.iOS;

/// <summary>
/// Wires the platform-neutral Mobile view models to the iOS-specific
/// services (notifications, audio chime, NSUserDefaults, HealthKit) and
/// starts the live BLE pipeline. Lives in the head project so the Mobile
/// assembly never takes a CoreBluetooth / UserNotifications / AVFoundation
/// / HealthKit dependency.
/// </summary>
public static class IosCompositionRoot
{
	private static MobileSettings? _settings;
	private static NSUserDefaultsSettingsStore? _store;
	private static NotificationDispatcher? _notifications;
	private static HealthKitStore? _healthStore;
	private static AudioChimePlayer? _chime;
	private static MeltdownRepository? _repository;
	private static PolarHrSource? _source;
	private static Pipeline? _pipeline;
	private static MobileAlertDispatcher? _alertDispatcher;
	private static HealthKitEpisodeRecorder? _episodeRecorder;
	private static NowViewModel? _nowViewModel;
	private static string? _databasePath;

	public static MobileSettings? Settings => _settings;
	public static Pipeline? Pipeline => _pipeline;
	public static string? DatabasePath => _databasePath;

	/// <summary>
	/// Composes the view-model tree synchronously so Avalonia's
	/// <c>OnFrameworkInitializationCompleted</c> has something to bind to
	/// immediately. The live BLE pipeline + HealthKit warm-start are kicked
	/// off asynchronously by <see cref="StartPipelineAsync"/> after the UI
	/// is up — splitting these means the disclaimer screen and an empty
	/// Now tab render instantly instead of waiting on HealthKit auth.
	/// </summary>
	public static RootViewModel BuildRootViewModel()
	{
		_store = new NSUserDefaultsSettingsStore();
		_settings = _store.LoadSettings();

		_notifications = new NotificationDispatcher(_settings);
		_healthStore = new HealthKitStore();
		_chime = new AudioChimePlayer();
		_databasePath = ResolveDatabasePath();

		var settingsTab = new SettingsViewModel(
			_settings,
			requestNotifications: () => _notifications.RequestAuthorizationAsync(),
			requestHealthKit: () => _healthStore.RequestAuthorizationAsync(),
			store: _store);

		_nowViewModel = new NowViewModel();

		return new RootViewModel(
			_settings,
			_nowViewModel,
			new HistoryViewModel(_databasePath),
			settingsTab,
			_store,
			onDisclaimerAccepted: () => _ = StartPipelineAsync());
	}

	/// <summary>
	/// Opens the repository, warm-starts the baseline from HealthKit, then
	/// boots the BLE pipeline. Returns the pipeline so the iOS head can
	/// later stop/restart it across lifecycle events. Idempotent — calling
	/// twice on the same app launch is a no-op after the first call.
	/// </summary>
	public static async Task<Pipeline?> StartPipelineAsync()
	{
		if (_pipeline is not null || _settings is null || _databasePath is null)
		{
			return _pipeline;
		}

		_repository = new MeltdownRepository(
			_databasePath,
			MeltdownRepositoryOptions.MobileSafeDefaults);

		ApplyFileProtection(_databasePath);

		_source = new PolarHrSource(_settings.DeviceType);
		_pipeline = new Pipeline(_settings, _repository, _source);

		await _pipeline.WarmStartAsync(_healthStore, lookback: TimeSpan.FromHours(24))
			.ConfigureAwait(false);

		_pipeline.Start();

		if (_notifications is not null)
		{
			_alertDispatcher = new MobileAlertDispatcher(_pipeline, _settings, _notifications, _chime);
		}

		if (_healthStore is not null)
		{
			_episodeRecorder = new HealthKitEpisodeRecorder(_pipeline, _settings, _healthStore);
		}

		_nowViewModel?.AttachPipeline(_pipeline);

		return _pipeline;
	}

	/// <summary>
	/// Flush and tear down for app termination. Bounded so the OS doesn't
	/// SIGKILL us mid-cleanup.
	/// </summary>
	public static async Task ShutdownPipelineAsync(TimeSpan? timeout = null)
	{
		var window = timeout ?? TimeSpan.FromSeconds(1);
		if (_pipeline is null)
		{
			return;
		}

		var stop = _pipeline.StopAsync();
		await Task.WhenAny(stop, Task.Delay(window)).ConfigureAwait(false);

		_alertDispatcher?.Dispose();
		_episodeRecorder?.Dispose();
		_pipeline.Dispose();
		_repository?.Dispose();

		_alertDispatcher = null;
		_episodeRecorder = null;
		_pipeline = null;
		_repository = null;
		_source = null;
	}

	private static string ResolveDatabasePath()
	{
		// Library/Application Support is the right home for non-user-visible
		// app data on iOS (design doc §5 / §4.7). Files there are backed up
		// to iCloud by default — we want that for episode history.
		string library = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		string dir = Path.Combine(library, "MeltdownMonitor");
		Directory.CreateDirectory(dir);
		return Path.Combine(dir, "data.db");
	}

	private static void ApplyFileProtection(string path)
	{
		// Background BLE callbacks can fire after the phone is locked but
		// before the user has unlocked it once since boot, so
		// CompleteUntilFirstUserAuthentication is the strongest protection
		// that still lets writes through (design doc §4.7).
		var attrs = new NSFileAttributes
		{
			ProtectionKey = NSFileProtection.CompleteUntilFirstUserAuthentication,
		};
		NSFileManager.DefaultManager.SetAttributes(attrs, path);
	}
}
