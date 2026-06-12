using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Motion;

namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// Streams the live physiological data the pipeline produces into the platform
/// health store (HealthKit / Health Connect), gated on the opt-in
/// <see cref="MobileSettings.RecordToHealth"/> flag. Three streams:
/// <list type="bullet">
/// <item>heart rate — one downsampled <see cref="IHealthStore.WriteHrSampleAsync"/>
/// per <see cref="HrWriteInterval"/>, tagged with the <see cref="HrMotionContext"/>
/// (resting / active / unknown) the movement stream supports at that moment, so the
/// health store knows whether it was a resting or an in-motion reading;</item>
/// <item>HRV (RMSSD + SDNN) — one <see cref="IHealthStore.WriteHrvSampleAsync"/>
/// per <see cref="HrvWriteInterval"/>, only once the 5-minute extended window is warm;</item>
/// <item>raw beat-to-beat RR — buffered and flushed as a heartbeat series via
/// <see cref="IHealthStore.WriteHeartbeatSeriesAsync"/>.</item>
/// </list>
///
/// <para>
/// Writing every beat as its own Health sample would flood the user's Health
/// database and is discouraged by Apple, so HR/HRV are downsampled and the raw
/// RR stream goes into batched heartbeat series instead. Downsampling is keyed
/// off the <em>sample</em> timestamps (not the wall clock) so replay/warm-start
/// behaves deterministically.
/// </para>
///
/// <para>
/// Episode write-back stays in <see cref="HealthKitEpisodeRecorder"/>; this type
/// owns only the continuous streams. All writes are fire-and-forget and swallow
/// faults — a denied or unavailable store must never disturb monitoring.
/// </para>
/// </summary>
public sealed class HealthDataRecorder : IDisposable
{
	/// <summary>Minimum gap between heart-rate sample writes. Apple/Health Connect both
	/// discourage flooding; one per minute is plenty for a baseline/trend record.</summary>
	public static readonly TimeSpan HrWriteInterval = TimeSpan.FromMinutes(1);

	/// <summary>Minimum gap between HRV (SDNN/RMSSD) writes.</summary>
	public static readonly TimeSpan HrvWriteInterval = TimeSpan.FromMinutes(1);

	/// <summary>A buffered run of beats is flushed as one heartbeat series after this span…</summary>
	public static readonly TimeSpan SeriesFlushInterval = TimeSpan.FromMinutes(2);

	/// <summary>…or once this many beats have accumulated, whichever comes first.</summary>
	public const int SeriesFlushCount = 240;

	private readonly Pipeline _pipeline;
	private readonly MobileSettings _settings;
	private readonly IHealthStore _healthStore;

	private readonly TimeSpan _hrWriteInterval;
	private readonly TimeSpan _hrvWriteInterval;
	private readonly TimeSpan _seriesFlushInterval;
	private readonly int _seriesFlushCount;

	private readonly object _bufferLock = new();
	private readonly List<RrIntervalSample> _rrBuffer = [];
	private readonly HrMotionContextClassifier _motionContext = new();

	private DateTimeOffset _lastHrWrite = DateTimeOffset.MinValue;
	private DateTimeOffset _lastHrvWrite = DateTimeOffset.MinValue;
	private MovementLevel _latestMovement = MovementLevel.Unknown;

	public HealthDataRecorder(
		Pipeline pipeline,
		MobileSettings settings,
		IHealthStore healthStore,
		TimeSpan? hrWriteInterval = null,
		TimeSpan? hrvWriteInterval = null,
		TimeSpan? seriesFlushInterval = null,
		int? seriesFlushCount = null)
	{
		_pipeline = pipeline;
		_settings = settings;
		_healthStore = healthStore;
		_hrWriteInterval = hrWriteInterval ?? HrWriteInterval;
		_hrvWriteInterval = hrvWriteInterval ?? HrvWriteInterval;
		_seriesFlushInterval = seriesFlushInterval ?? SeriesFlushInterval;
		_seriesFlushCount = seriesFlushCount ?? SeriesFlushCount;

		_pipeline.SampleUpdated += OnSampleUpdated;
		_pipeline.BeatReceived += OnBeatReceived;
		_pipeline.MovementUpdated += OnMovementUpdated;
	}

	// MovementUpdated carries no timestamp; the pipeline raises it in the same beat-loop
	// turn as the sample that follows, so the sample's timestamp is the clock (replay-safe).
	private void OnMovementUpdated(MovementSnapshot snapshot) => _latestMovement = snapshot.Level;

	private void OnSampleUpdated(HrvSample sample)
	{
		// Fed even while opted out so the sedentary run is already warm if the user
		// enables recording mid-session.
		_motionContext.Update(sample.Timestamp, _latestMovement);

		if (!_settings.RecordToHealth)
		{
			return;
		}

		if (sample.MeanHr > 0 && sample.Timestamp - _lastHrWrite >= _hrWriteInterval)
		{
			_lastHrWrite = sample.Timestamp;
			FireAndForget(() => _healthStore.WriteHrSampleAsync(new HrSample(
				sample.Timestamp, sample.MeanHr, _motionContext.ContextAt(sample.Timestamp))));
		}

		// SDNN only exists once the 5-minute extended window is warm; never fabricate it.
		var extended = sample.Extended;
		if (extended is not null && extended.Sdnn > 0 && sample.Rmssd > 0
			&& sample.Timestamp - _lastHrvWrite >= _hrvWriteInterval)
		{
			_lastHrvWrite = sample.Timestamp;
			FireAndForget(() => _healthStore.WriteHrvSampleAsync(
				new HealthHrvSample(sample.Timestamp, sample.Rmssd, extended.Sdnn)));
		}
	}

	private void OnBeatReceived(Beat beat)
	{
		if (!_settings.RecordToHealth || beat.IsArtifact || beat.RrMs <= 0)
		{
			return;
		}

		List<RrIntervalSample>? toFlush = null;
		lock (_bufferLock)
		{
			_rrBuffer.Add(new RrIntervalSample(beat.Timestamp, beat.RrMs));
			bool full = _rrBuffer.Count >= _seriesFlushCount;
			bool aged = _rrBuffer.Count > 1
				&& beat.Timestamp - _rrBuffer[0].Timestamp >= _seriesFlushInterval;
			if (full || aged)
			{
				toFlush = [.. _rrBuffer];
				_rrBuffer.Clear();
			}
		}

		if (toFlush is not null)
		{
			FireAndForget(() => _healthStore.WriteHeartbeatSeriesAsync(toFlush));
		}
	}

	// Fire-and-forget so a HealthKit write never blocks or crashes the BLE callback
	// path; faults are best-effort and swallowed (a denied/unavailable store must not
	// take down monitoring).
	private static void FireAndForget(Func<Task> write) => _ = SafelyAsync(write);

	private static async Task SafelyAsync(Func<Task> write)
	{
		try
		{
			await write().ConfigureAwait(false);
		}
		catch
		{
			// Best-effort: swallow.
		}
	}

	public void Dispose()
	{
		_pipeline.SampleUpdated -= OnSampleUpdated;
		_pipeline.BeatReceived -= OnBeatReceived;
		_pipeline.MovementUpdated -= OnMovementUpdated;

		List<RrIntervalSample>? remaining = null;
		lock (_bufferLock)
		{
			if (_settings.RecordToHealth && _rrBuffer.Count > 1)
			{
				remaining = [.. _rrBuffer];
			}

			_rrBuffer.Clear();
		}

		if (remaining is not null)
		{
			FireAndForget(() => _healthStore.WriteHeartbeatSeriesAsync(remaining));
		}
	}
}
