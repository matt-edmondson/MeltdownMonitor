namespace MeltdownMonitor.Android.Services;

/// <summary>
/// Bridges the Activity-scoped Health Connect permission UI to the
/// platform-neutral <see cref="HealthConnectStore.RequestAuthorizationAsync"/>
/// call (design doc §5.3 / Phase 4 — the launcher carried over from Phase 5).
///
/// <para>
/// Health Connect grants are made through its own permission screen, launched
/// from an <c>ActivityResultContract</c>, not through the standard Android
/// runtime-permission dialog. That contract can only be launched from a live
/// Activity, so <see cref="MainActivity"/> installs <see cref="Launcher"/> while
/// it is foregrounded and the store calls <see cref="RequestAsync"/> when the
/// user taps "Connect Health" in Settings. With no foregrounded Activity (the
/// foreground service is the only thing running) the launcher is null and the
/// request degrades to a no-op — the store then just re-reports the existing
/// grant, exactly as it did before the launcher landed.
/// </para>
/// </summary>
public static class HealthConnectPermissions
{
	/// <summary>
	/// The string <c>HealthPermission.getReadPermission(HeartRateRecord::class)</c>
	/// resolves to. Declared in <c>AndroidManifest.xml</c> so it can be granted
	/// through Health Connect's UI; read for the warm-start baseline (§5.3 / Phase 5).
	/// </summary>
	public const string ReadHeartRate = "android.permission.health.READ_HEART_RATE";

	/// <summary>
	/// The string <c>HealthPermission.getWritePermission(ExerciseSessionRecord::class)</c>
	/// resolves to. Declared in the manifest; write for the opt-in episode
	/// write-back (§5.3 / Phase 8).
	/// </summary>
	public const string WriteExercise = "android.permission.health.WRITE_EXERCISE";

	/// <summary>
	/// Write permission for <c>HeartRateRecord</c> — the opt-in continuous heart-rate
	/// write-back (<c>RecordToHealth</c>). Declared in the manifest; granted through
	/// Health Connect's UI.
	/// </summary>
	public const string WriteHeartRate = "android.permission.health.WRITE_HEART_RATE";

	/// <summary>
	/// Write permission for <c>HeartRateVariabilityRmssdRecord</c> — the opt-in HRV
	/// write-back (<c>RecordToHealth</c>). Declared in the manifest; granted through
	/// Health Connect's UI.
	/// </summary>
	public const string WriteHeartRateVariability = "android.permission.health.WRITE_HEART_RATE_VARIABILITY";

	/// <summary>
	/// Write permission for <c>RestingHeartRateRecord</c> — written when the motion
	/// stream vouches a continuous-HR reading was taken at rest (<c>RecordToHealth</c>).
	/// Declared in the manifest; granted through Health Connect's UI.
	/// </summary>
	public const string WriteRestingHeartRate = "android.permission.health.WRITE_RESTING_HEART_RATE";

	/// <summary>
	/// Every Health Connect permission the app uses. Requested together so the user
	/// grants read and the writes on a single Health Connect screen; each write grant
	/// sits unused until the user enables the matching opt-in
	/// (<c>WriteEpisodesToHealthKit</c> / <c>RecordToHealth</c>).
	/// </summary>
	public static IReadOnlyList<string> All { get; } =
		[ReadHeartRate, WriteExercise, WriteHeartRate, WriteHeartRateVariability, WriteRestingHeartRate];

	/// <summary>
	/// Installed by <see cref="MainActivity"/> while it is foregrounded; launches
	/// the Health Connect permission screen for the requested permissions and
	/// completes once the screen is dismissed. The returned bool is advisory — the
	/// store re-reads the authoritative grant state afterwards — so a coarse
	/// "anything granted" answer is sufficient. Null when no Activity is live.
	/// </summary>
	public static Func<IReadOnlyCollection<string>, Task<bool>>? Launcher { get; set; }

	/// <summary>
	/// Launches the Health Connect permission screen through the live Activity,
	/// or returns <c>false</c> immediately when no Activity is foregrounded.
	/// </summary>
	public static Task<bool> RequestAsync(IReadOnlyCollection<string> permissions) =>
		Launcher?.Invoke(permissions) ?? Task.FromResult(false);
}
