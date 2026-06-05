using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.Android.Services;

/// <summary>
/// <see cref="IHealthStore"/> placeholder for the Health Connect warm-start
/// (design doc §5.3 / Phase 5). Health Connect (<c>androidx.health.connect</c>)
/// is the platform successor to the deprecated Google Fit APIs and the Android
/// analog of iOS HealthKit; reading the last 24 h of <c>HeartRateRecord</c> will
/// warm the EWMA baseline through the existing <see cref="Pipeline.WarmStartAsync"/>.
///
/// <para>
/// The managed binding choice (<c>Xamarin.AndroidX.Health.Connect.Client</c> vs.
/// a thin JNI read-path wrapper) is the open question logged in design doc §11.1
/// and is deliberately deferred. Until it is resolved this implementation reports
/// no authorization and reads no samples, so the pipeline simply starts cold —
/// exactly the graceful degradation §5.3 calls for when Health Connect is absent.
/// Live beats then warm the baseline as usual; nothing else in the head depends
/// on this returning data.
/// </para>
/// </summary>
public sealed class HealthConnectStore : IHealthStore
{
	// No Health Connect client yet (§11.1) — report unavailable so callers fall back to a cold start.
	public Task<bool> RequestAuthorizationAsync() => Task.FromResult(false);

	public async IAsyncEnumerable<HrSample> ReadRecentHeartRateAsync(TimeSpan lookback)
	{
		// Cold start: yield nothing until the Health Connect read path lands (Phase 5).
		await Task.CompletedTask.ConfigureAwait(false);
		yield break;
	}

	public Task WriteHrSampleAsync(HrSample sample) => Task.CompletedTask;

	// Episode write-back is the Phase 8 fast-follow (design doc §5.3 / §13).
	public Task WriteEpisodeAsync(EpisodeRecord episode) => Task.CompletedTask;
}
