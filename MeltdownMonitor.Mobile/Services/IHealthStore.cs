using MeltdownMonitor.Core.Motion;

namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// One heart-rate reading bound for (or read from) the platform health store.
/// <paramref name="MotionContext"/> says whether it was a resting or an in-motion
/// reading: iOS writes it as HealthKit's heart-rate motion context metadata, Android
/// expresses a sedentary reading as a <c>RestingHeartRateRecord</c> (Health Connect's
/// <c>HeartRateRecord</c> has no context field). <see cref="HrMotionContext.Unknown"/>
/// (the default, and what warm-start reads carry) writes no claim at all.
/// </summary>
public record HrSample(
	DateTimeOffset Timestamp,
	double HeartRateBpm,
	HrMotionContext MotionContext = HrMotionContext.Unknown);

/// <summary>
/// A heart-rate-variability reading destined for the platform health store.
/// Carries both metrics so each platform writes the one its API exposes:
/// HealthKit stores SDNN (<c>HeartRateVariabilitySDNN</c>), Health Connect
/// stores RMSSD (<c>HeartRateVariabilityRmssdRecord</c>). Both are computed
/// over the same window, so neither is fabricated from the other.
/// </summary>
public record HealthHrvSample(DateTimeOffset Timestamp, double RmssdMs, double SdnnMs);

/// <summary>One beat's RR interval, for the iOS beat-to-beat heartbeat series.</summary>
public record RrIntervalSample(DateTimeOffset Timestamp, double RrMs);

public record EpisodeRecord(
	DateTimeOffset Start,
	DateTimeOffset End,
	string Label,
	string? Notes);

/// <summary>
/// Platform-neutral facade over HealthKit (iOS) or Health Connect (Android).
/// The streaming write methods default to no-ops so a platform that lacks an
/// equivalent data type (and the off-device test stubs) needn't implement them;
/// each head overrides the ones it can honour. Every method is best-effort —
/// the pipeline tolerates a store that reads/writes nothing.
/// </summary>
public interface IHealthStore
{
	Task<bool> RequestAuthorizationAsync();

	IAsyncEnumerable<HrSample> ReadRecentHeartRateAsync(TimeSpan lookback);

	Task WriteHrSampleAsync(HrSample sample);

	/// <summary>Writes one HRV reading (SDNN on iOS, RMSSD on Android). No-op by default.</summary>
	Task WriteHrvSampleAsync(HealthHrvSample sample) => Task.CompletedTask;

	/// <summary>
	/// Writes a batch of consecutive beats as a single beat-to-beat series
	/// (HealthKit <c>HKHeartbeatSeries</c>). Health Connect has no beat-to-beat
	/// record type, so the Android store leaves this a no-op. No-op by default.
	/// </summary>
	Task WriteHeartbeatSeriesAsync(IReadOnlyList<RrIntervalSample> beats) => Task.CompletedTask;

	Task WriteEpisodeAsync(EpisodeRecord episode);

	/// <summary>
	/// Revokes the app's health-store grants where the platform allows it
	/// programmatically (Health Connect does; HealthKit does not, so the iOS
	/// store leaves this a no-op and the head deep-links to the Health app
	/// instead). No-op by default.
	/// </summary>
	Task RevokeAuthorizationAsync() => Task.CompletedTask;
}
