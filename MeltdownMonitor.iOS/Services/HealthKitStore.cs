using Foundation;
using HealthKit;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.iOS.Services;

/// <summary>
/// <see cref="IHealthStore"/> backed by HealthKit. Reads recent heart-rate
/// samples for the baseline warm-start (design doc §8) and writes back
/// per-beat HR samples plus episode workouts so the user owns their data
/// in Apple Health.
/// </summary>
public sealed class HealthKitStore : IHealthStore
{
	private readonly HKHealthStore _store = new();
	private readonly HKQuantityType _heartRateType =
		HKQuantityType.Create(HKQuantityTypeIdentifier.HeartRate)!;
	private readonly HKUnit _bpmUnit = HKUnit.FromString("count/min");

	public Task<bool> RequestAuthorizationAsync()
	{
		if (!HKHealthStore.IsHealthDataAvailable)
		{
			return Task.FromResult(false);
		}

		var readTypes = new NSSet<HKObjectType>(_heartRateType);
		var writeTypes = new NSSet<HKSampleType>(_heartRateType, HKWorkoutType.Create());

		var tcs = new TaskCompletionSource<bool>();
		_store.RequestAuthorizationToShare(writeTypes, readTypes, (success, error) =>
		{
			tcs.TrySetResult(success && error is null);
		});
		return tcs.Task;
	}

	public async IAsyncEnumerable<HrSample> ReadRecentHeartRateAsync(TimeSpan lookback)
	{
		if (!HKHealthStore.IsHealthDataAvailable)
		{
			yield break;
		}

		var end = (NSDate)DateTime.UtcNow;
		var start = (NSDate)DateTime.UtcNow.Subtract(lookback);
		var predicate = HKQuery.GetPredicateForSamples(start, end, HKQueryOptions.StrictStartDate);
		var sortByStart = new NSSortDescriptor(HKSample.SortIdentifierStartDate, ascending: true);

		var tcs = new TaskCompletionSource<HKSample[]>();
		// HKObjectQueryNoLimit == 0; 24h of HR samples is bounded anyway.
		var query = new HKSampleQuery(
			_heartRateType,
			predicate,
			limit: (nuint)0,
			sortDescriptors: new[] { sortByStart },
			(_, results, error) =>
			{
				tcs.TrySetResult(error is null && results is not null ? results : Array.Empty<HKSample>());
			});

		_store.ExecuteQuery(query);
		var samples = await tcs.Task.ConfigureAwait(false);

		foreach (var sample in samples)
		{
			if (sample is not HKQuantitySample qs)
			{
				continue;
			}

			double bpm = qs.Quantity.GetDoubleValue(_bpmUnit);
			if (bpm <= 0)
			{
				continue;
			}

			var ts = (DateTimeOffset)(DateTime)qs.StartDate;
			yield return new HrSample(ts, bpm);
		}
	}

	public Task WriteHrSampleAsync(HrSample sample)
	{
		if (!HKHealthStore.IsHealthDataAvailable)
		{
			return Task.CompletedTask;
		}

		var quantity = HKQuantity.FromQuantity(_bpmUnit, sample.HeartRateBpm);
		var date = (NSDate)sample.Timestamp.UtcDateTime;
		var hkSample = HKQuantitySample.FromType(_heartRateType, quantity, date, date);

		var tcs = new TaskCompletionSource<bool>();
		_store.SaveObject(hkSample, (_, _) => tcs.TrySetResult(true));
		return tcs.Task;
	}

	public Task WriteEpisodeAsync(EpisodeRecord episode)
	{
		if (!HKHealthStore.IsHealthDataAvailable)
		{
			return Task.CompletedTask;
		}

		// "Mind & Body" is the closest HealthKit fit for a dysregulation
		// episode — it's a wellness annotation, not a workout in the
		// exercise sense. Apple's wellness-vs-medical rules (design doc
		// §11) make this safer than the exercise activity types.
		var metadata = new NSMutableDictionary();
		metadata[HKMetadataKey.WorkoutBrandName] = new NSString(episode.Label);
		if (!string.IsNullOrWhiteSpace(episode.Notes))
		{
			metadata[HKMetadataKey.ExternalUuid] = new NSString(episode.Notes);
		}

		var workout = HKWorkout.Create(
			HKWorkoutActivityType.MindAndBody,
			(NSDate)episode.Start.UtcDateTime,
			(NSDate)episode.End.UtcDateTime,
			Array.Empty<HKWorkoutEvent>(),
			totalEnergyBurned: null,
			totalDistance: null,
			metadata);

		var tcs = new TaskCompletionSource<bool>();
		_store.SaveObject(workout, (_, _) => tcs.TrySetResult(true));
		return tcs.Task;
	}
}
