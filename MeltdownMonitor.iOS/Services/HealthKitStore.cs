using Foundation;
using HealthKit;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.iOS.Services;

/// <summary>
/// <see cref="IHealthStore"/> backed by HealthKit. Reads recent heart-rate
/// samples for the baseline warm-start (design doc §8) and writes back the live
/// streams so the user owns their data in Apple Health: downsampled heart-rate
/// samples, HRV as <c>HeartRateVariabilitySDNN</c>, the raw beat-to-beat
/// <c>HKHeartbeatSeries</c>, and episode workouts. SDNN is the HealthKit-native
/// HRV metric (what Apple Watch writes), so it is recorded as SDNN rather than
/// mislabelling the pipeline's RMSSD.
/// </summary>
public sealed class HealthKitStore : IHealthStore
{
	private readonly HKHealthStore _store = new();
	private readonly HKQuantityType _heartRateType =
		HKQuantityType.Create(HKQuantityTypeIdentifier.HeartRate)!;
	private readonly HKQuantityType _hrvSdnnType =
		HKQuantityType.Create(HKQuantityTypeIdentifier.HeartRateVariabilitySdnn)!;
	private readonly HKUnit _bpmUnit = HKUnit.FromString("count/min");
	// SDNN is a time metric in HealthKit; "ms" is the canonical millisecond unit.
	private readonly HKUnit _msUnit = HKUnit.FromString("ms");

	public Task<bool> RequestAuthorizationAsync()
	{
		if (!HKHealthStore.IsHealthDataAvailable)
		{
			return Task.FromResult(false);
		}

		var readTypes = new NSSet<HKObjectType>(_heartRateType);
		var writeTypes = new NSSet<HKSampleType>(
			_heartRateType,
			_hrvSdnnType,
			HKSeriesType.HeartbeatSeriesType,
			HKObjectType.WorkoutType);

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

	public Task WriteHrvSampleAsync(HealthHrvSample sample)
	{
		// HealthKit's HRV type is SDNN; never write a non-positive value.
		if (!HKHealthStore.IsHealthDataAvailable || sample.SdnnMs <= 0)
		{
			return Task.CompletedTask;
		}

		var quantity = HKQuantity.FromQuantity(_msUnit, sample.SdnnMs);
		var date = (NSDate)sample.Timestamp.UtcDateTime;
		var hkSample = HKQuantitySample.FromType(_hrvSdnnType, quantity, date, date);

		var tcs = new TaskCompletionSource<bool>();
		_store.SaveObject(hkSample, (_, _) => tcs.TrySetResult(true));
		return tcs.Task;
	}

	public async Task WriteHeartbeatSeriesAsync(IReadOnlyList<RrIntervalSample> beats)
	{
		if (!HKHealthStore.IsHealthDataAvailable || beats.Count == 0)
		{
			return;
		}

		// A heartbeat series is anchored at the first beat; each subsequent beat is
		// added at its offset from that anchor. FinishSeries saves the sample. The
		// completion-handler primitives are bridged to Tasks so beats are added in order.
		var start = beats[0].Timestamp;
		var builder = new HKHeartbeatSeriesBuilder(_store, device: null, startDate: (NSDate)start.UtcDateTime);
		try
		{
			foreach (var beat in beats)
			{
				double offset = (beat.Timestamp - start).TotalSeconds;
				if (offset < 0)
				{
					offset = 0;
				}

				var add = new TaskCompletionSource<bool>();
				builder.AddHeartbeat(offset, precededByGap: false,
					(success, error) => add.TrySetResult(success && error is null));

				if (!await add.Task.ConfigureAwait(false))
				{
					// A rejected beat poisons the series; abandon it rather than save a partial one.
					builder.Discard();
					return;
				}
			}

			var finish = new TaskCompletionSource<bool>();
			builder.FinishSeries((_, error) => finish.TrySetResult(error is null));
			await finish.Task.ConfigureAwait(false);
		}
		catch (Exception)
		{
			// Best-effort: abandon a partially-built series rather than leak the builder.
			builder.Discard();
		}
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
