namespace MeltdownMonitor.Mobile.Services;

public record HrSample(DateTimeOffset Timestamp, double HeartRateBpm);

public record EpisodeRecord(
	DateTimeOffset Start,
	DateTimeOffset End,
	string Label,
	string? Notes);

/// <summary>
/// Platform-neutral facade over HealthKit (iOS) or an equivalent store on
/// other platforms. The iOS implementation lives in MeltdownMonitor.iOS.
/// </summary>
public interface IHealthStore
{
	Task<bool> RequestAuthorizationAsync();

	IAsyncEnumerable<HrSample> ReadRecentHeartRateAsync(TimeSpan lookback);

	Task WriteHrSampleAsync(HrSample sample);

	Task WriteEpisodeAsync(EpisodeRecord episode);
}
