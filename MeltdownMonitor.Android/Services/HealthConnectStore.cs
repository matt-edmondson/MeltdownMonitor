using Android.Runtime;
using AndroidX.Health.Connect.Client;
using AndroidX.Health.Connect.Client.Records;
using AndroidX.Health.Connect.Client.Records.Metadata;
using AndroidX.Health.Connect.Client.Request;
using AndroidX.Health.Connect.Client.Response;
using AndroidX.Health.Connect.Client.Time;
using Java.Time;
using Kotlin.Coroutines;
using Kotlin.Coroutines.Intrinsics;
using Kotlin.Jvm;
using MeltdownMonitor.Mobile.Services;
using AndroidContext = Android.Content.Context;

namespace MeltdownMonitor.Android.Services;

/// <summary>
/// <see cref="IHealthStore"/> backed by Health Connect (<c>androidx.health.connect</c>),
/// the platform successor to the deprecated Google Fit APIs and the Android analog
/// of iOS HealthKit (design doc §5.3 / Phase 5). Reads the last 24 h of
/// <c>HeartRateRecord</c> to warm the EWMA HR baseline through
/// <see cref="MeltdownMonitor.Mobile.Pipeline.WarmStartAsync"/>, removing the
/// cold-start calibration on relaunch — exactly as <c>HealthKitStore</c> feeds the
/// iOS pipeline.
///
/// <para>
/// Health Connect's Kotlin client exposes <c>readRecords</c> / <c>getGrantedPermissions</c>
/// as <c>suspend</c> functions. The managed binding surfaces those with a trailing
/// <see cref="IContinuation"/> and returns the awaited value through
/// <see cref="IContinuation.ResumeWith"/>; <see cref="AwaitSuspendAsync"/> bridges that
/// to a <see cref="Task"/>. The §11.1 binding question is resolved in favour of the
/// managed <c>Xamarin.AndroidX.Health.Connect.ConnectClient</c> binding rather than a
/// JNI read-path wrapper.
/// </para>
///
/// <para>
/// Every path degrades to a cold start when Health Connect is absent, the read
/// permission is not granted, or the IPC read fails — the pipeline already tolerates
/// an <see cref="IHealthStore"/> that yields nothing, and live beats then warm the
/// baseline as usual (design doc §5.3).
/// </para>
///
/// <para>
/// <see cref="WriteEpisodeAsync"/> implements the Phase 8 write-back: when the user
/// opts in (the <c>WriteEpisodesToHealthKit</c> flag, gated upstream by
/// <c>HealthKitEpisodeRecorder</c>), each dysregulation alert is recorded as an
/// <c>ExerciseSessionRecord</c> of type "other workout" — the closest honest Health
/// Connect fit for a wellness annotation, the analog of the iOS "Mind &amp; Body"
/// workout. It is best-effort: a missing write grant (the write permission is declared
/// in the manifest but granted through Health Connect's UI, the Phase 4 follow-up) or
/// an IPC fault surfaces as a Kotlin <c>Result.Failure</c> and is swallowed, never
/// taking down monitoring. Per-beat HR write-back (<see cref="WriteHrSampleAsync"/>)
/// stays an intentional no-op — the pipeline never calls it, and streaming every beat
/// over IPC is not worth the chatter.
/// </para>
/// </summary>
public sealed class HealthConnectStore : IHealthStore
{
	// The string HealthPermission.getReadPermission(HeartRateRecord::class) resolves to.
	// Declared in AndroidManifest.xml so it can be granted through Health Connect's UI.
	private const string ReadHeartRatePermission = HealthConnectPermissions.ReadHeartRate;

	// A single read page is capped at this many records; 24 h of HR is paged through
	// PageToken below. The ceiling bounds a pathological provider that never returns a
	// null token.
	private const int PageSize = 1000;
	private const int MaxPages = 50;

	// Warm-start is best-effort and runs before Start, with no caller cancellation, so a
	// wedged provider must never hang launch — the suspend bridge is bounded by this.
	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);

	private readonly AndroidContext _context;

	public HealthConnectStore(AndroidContext context) =>
		_context = context ?? throw new ArgumentNullException(nameof(context));

	/// <summary>
	/// Grants the heart-rate read permission by launching Health Connect's own permission
	/// screen (design doc §5.3 / Phase 4). Health Connect grants are made through that UI
	/// (an <c>ActivityResultContract</c>), not a manifest runtime grant, and the screen can
	/// only be launched from a live Activity — <see cref="HealthConnectPermissions.Launcher"/>
	/// is installed by <see cref="MainActivity"/> while it is foregrounded, and this method
	/// drives it. Returns the authoritative grant state read back from Health Connect after
	/// the screen is dismissed, not the launcher's advisory result.
	///
	/// <para>
	/// Short-circuits when the grant already exists (no need to re-prompt), when Health
	/// Connect is absent (nothing to grant, start cold), and when no Activity is foregrounded
	/// to launch from (the launcher is null and the request is a no-op, leaving the grant
	/// state unchanged) — the same graceful-degradation posture as the warm-start read.
	/// </para>
	/// </summary>
	public async Task<bool> RequestAuthorizationAsync()
	{
		var client = TryGetClient();
		if (client is null)
		{
			return false;
		}

		// Already granted? Don't pop the permission screen again.
		if (await HasReadPermissionAsync(client).ConfigureAwait(false))
		{
			return true;
		}

		// Launch Health Connect's permission screen through the live Activity. Request read
		// and write together so the user grants both on one screen; the write grant sits
		// unused until episode write-back is enabled (gated separately). With no foregrounded
		// Activity the launcher is null and this is a no-op, so re-read the grant either way.
		await HealthConnectPermissions.RequestAsync(HealthConnectPermissions.All).ConfigureAwait(false);

		return await HasReadPermissionAsync(client).ConfigureAwait(false);
	}

	/// <summary>
	/// Reports whether Health Connect has granted the heart-rate read permission, reading
	/// the authoritative state through the same Kotlin <c>suspend</c>→<see cref="Task"/>
	/// bridge as the warm-start read. Any fault (denied, IPC error) degrades to "not granted".
	/// </summary>
	private static async Task<bool> HasReadPermissionAsync(IHealthConnectClient client)
	{
		try
		{
			// client is non-null here, but the capture reverts to its nullable declared
			// type inside the lambda, so assert it.
			var result = await AwaitSuspendAsync(c => client!.PermissionController.GetGrantedPermissions(c))
				.ConfigureAwait(false);

			// The resume value is marshalled to the ResumeWith parameter's declared type
			// (Java.Lang.Object), so re-wrap the handle as the Kotlin Set<String> with
			// JavaCast rather than a managed pattern match, which would never match.
			var permissions = result?.JavaCast<Java.Util.ICollection>();
			return permissions is not null && permissions.Contains(new Java.Lang.String(ReadHeartRatePermission));
		}
		catch (Java.Lang.Exception)
		{
			return false;
		}
		catch (InvalidCastException)
		{
			return false;
		}
	}

	public async IAsyncEnumerable<HrSample> ReadRecentHeartRateAsync(TimeSpan lookback)
	{
		var client = TryGetClient();
		if (client is null)
		{
			yield break;
		}

		var end = Instant.Now()!;
		var start = end.MinusMillis((long)lookback.TotalMilliseconds)!;
		var timeRange = TimeRangeFilter.Between(start, end)!;
		var recordType = JvmClassMappingKt.GetKotlinClass(Java.Lang.Class.FromType(typeof(HeartRateRecord))!)!;
		var allOrigins = new List<DataOrigin>();

		string? pageToken = null;
		int pages = 0;
		do
		{
			// pageToken is null for the first page; the `!` keeps the call compiling
			// whether or not the binding annotates the parameter nullable (it only ever
			// affects compile-time flow analysis — the provider expects null here).
			var request = new ReadRecordsRequest(
				recordType, timeRange, allOrigins,
				ascendingOrder: true, pageSize: PageSize, pageToken: pageToken!);

			// Collect the page off the suspend bridge inside the try; yield outside it,
			// since C# forbids `yield return` from a try with a catch clause.
			List<HrSample>? page = null;
			string? nextToken = null;
			try
			{
				// client is non-null past the guard above; the lambda capture needs the assert.
				var result = await AwaitSuspendAsync(c => client!.ReadRecords(request, c)).ConfigureAwait(false);

				// The resume value arrives typed as the ResumeWith parameter (Java.Lang.Object),
				// so re-wrap the handle as the concrete response with JavaCast. A null result is
				// the read timeout; a Kotlin Result.Failure (denied permission, provider fault)
				// is not assignable and throws InvalidCastException — both degrade to a cold
				// start (design doc §5.3).
				var response = result?.JavaCast<ReadRecordsResponse>();
				if (response is not null)
				{
					page = ExtractSamples(response);
					nextToken = response.PageToken;
				}
			}
			catch (Java.Lang.Exception)
			{
				yield break;
			}
			catch (InvalidCastException)
			{
				yield break;
			}

			if (page is null)
			{
				yield break;
			}

			foreach (var sample in page)
			{
				yield return sample;
			}

			pageToken = nextToken;
		}
		while (!string.IsNullOrEmpty(pageToken) && ++pages < MaxPages);
	}

	// Per-beat HR write-back is an intentional no-op: nothing in the pipeline calls it
	// (only WriteEpisodeAsync is wired, through HealthKitEpisodeRecorder), and streaming
	// every beat over Health Connect IPC is not worth the chatter (design doc §5.3 / §8).
	public Task WriteHrSampleAsync(HrSample sample) => Task.CompletedTask;

	/// <summary>
	/// Records a dysregulation episode as a Health Connect <c>ExerciseSessionRecord</c>
	/// of type "other workout" — the closest honest fit for a wellness annotation, the
	/// Android analog of the iOS "Mind &amp; Body" workout (design doc §5.3 / Phase 8).
	/// Called only when the user opts in (gated upstream by <c>HealthKitEpisodeRecorder</c>
	/// on the <c>WriteEpisodesToHealthKit</c> flag). Best-effort: a missing write grant or
	/// an IPC fault is swallowed so a denied/unavailable store can never take down
	/// monitoring — the same posture as the warm-start read above.
	/// </summary>
	public async Task WriteEpisodeAsync(EpisodeRecord episode)
	{
		var client = TryGetClient();
		if (client is null)
		{
			return;
		}

		try
		{
			// Health Connect treats a null zone offset as "unknown", which is fine for a
			// wellness annotation — we record the instants, not a local wall-clock claim.
			var start = Instant.OfEpochMilli(episode.Start.ToUnixTimeMilliseconds())!;
			var end = Instant.OfEpochMilli(episode.End.ToUnixTimeMilliseconds())!;

			// Positional, not named: the binding does not guarantee the Kotlin parameter
			// names survive. Order is (startTime, startZoneOffset, endTime, endZoneOffset,
			// metadata, exerciseType, title, notes); the null zone offsets are "unknown".
			var record = new ExerciseSessionRecord(
				start, null,
				end, null,
				Metadata.ManualEntry(),
				ExerciseSessionRecord.ExerciseTypeOtherWorkout,
				episode.Label,
				// `!` mirrors the read path: it keeps the call compiling whether or not the
				// binding annotates `notes` nullable. Notes is genuinely optional and a null
				// is fine at runtime — Health Connect accepts a session with no notes.
				episode.Notes!);

			// The binding's record classes extend Java.Lang.Object without surfacing the
			// IRecord interface in managed metadata, so re-wrap the Java peer (which *is* a
			// Record at runtime) with JavaCast to satisfy InsertRecords' IList<IRecord> —
			// the same handle-rewrap the read path uses for the response.
			var records = new List<IRecord> { record.JavaCast<IRecord>()! };

			// InsertRecords is itself a Kotlin suspend function; await it through the same
			// bridge as the read. We ignore the inserted-id response — a Result.Failure
			// (no write grant, provider fault) comes back as a non-null handle we never
			// cast, so it degrades silently rather than throwing.
			_ = await AwaitSuspendAsync(c => client!.InsertRecords(records, c)).ConfigureAwait(false);
		}
		catch (Java.Lang.Exception)
		{
			// Best-effort: a write fault must never escape into the alert path.
		}
	}

	private static List<HrSample> ExtractSamples(ReadRecordsResponse response)
	{
		var samples = new List<HrSample>();
		foreach (var record in response.Records)
		{
			if (record is not HeartRateRecord heartRate || heartRate.Samples is null)
			{
				continue;
			}

			foreach (var sample in heartRate.Samples)
			{
				double bpm = sample.BeatsPerMinute;
				var time = sample.Time;
				if (bpm <= 0 || time is null)
				{
					continue;
				}

				var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time.ToEpochMilli());
				samples.Add(new HrSample(timestamp, bpm));
			}
		}

		return samples;
	}

	private IHealthConnectClient? TryGetClient()
	{
		try
		{
			// SdkAvailable is the only status that supports reads; SdkUnavailable and
			// SdkUnavailableProviderUpdateRequired both mean "start cold" (design doc §5.3).
			return HealthConnectClient.GetSdkStatus(_context) == HealthConnectClient.SdkAvailable
				? HealthConnectClient.GetOrCreate(_context)
				: null;
		}
		catch (Java.Lang.Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// Invokes a Kotlin <c>suspend</c> function and awaits its result. The bound method
	/// takes a trailing <see cref="IContinuation"/> and either returns its result
	/// synchronously or, more usually for an IPC-backed call, returns the
	/// <c>COROUTINE_SUSPENDED</c> sentinel and resumes the continuation later. A timeout
	/// guards against a provider that never resumes, so a best-effort warm-start can
	/// never hang launch.
	/// </summary>
	private static async Task<Java.Lang.Object?> AwaitSuspendAsync(Func<IContinuation, Java.Lang.Object?> call)
	{
		var continuation = new SuspendContinuation();
		var immediate = call(continuation);
		if (immediate is not null && !IsSuspendSentinel(immediate))
		{
			return immediate;
		}

		var finished = await Task.WhenAny(continuation.Task, Task.Delay(ReadTimeout)).ConfigureAwait(false);
		return finished == continuation.Task ? continuation.Task.Result : null;
	}

	private static bool IsSuspendSentinel(Java.Lang.Object value)
	{
		try
		{
			return value.Equals(IntrinsicsKt.COROUTINE_SUSPENDED);
		}
		catch (Java.Lang.Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// Bridges a Kotlin continuation to a <see cref="Task"/>. <see cref="EmptyCoroutineContext"/>
	/// is the correct empty context for a top-level resume, and the success value (or a
	/// boxed <c>kotlin.Result.Failure</c>, which casts to neither response type and so
	/// degrades to a cold start) arrives through <see cref="ResumeWith"/>.
	/// </summary>
	private sealed class SuspendContinuation : Java.Lang.Object, IContinuation
	{
		private readonly TaskCompletionSource<Java.Lang.Object?> _completion =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<Java.Lang.Object?> Task => _completion.Task;

		public ICoroutineContext Context => EmptyCoroutineContext.Instance!;

		public void ResumeWith(Java.Lang.Object? result) => _completion.TrySetResult(result);
	}
}
