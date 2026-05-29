using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// Records a dysregulation episode to the user's health store whenever the
/// pipeline fires an alert, gated on the opt-in
/// <see cref="MobileSettings.WriteEpisodesToHealthKit"/> flag (design doc
/// §6.3). Stays platform-neutral by writing through <see cref="IHealthStore"/>
/// so the iOS head supplies the real HealthKit-backed implementation.
/// </summary>
public sealed class HealthKitEpisodeRecorder : IDisposable
{
	/// <summary>
	/// How far before the alert the episode window is anchored. The detector
	/// only fires after its hold/escalation windows elapse, so the dysregulation
	/// itself began roughly a minute earlier — backdating the start makes the
	/// HealthKit annotation line up with what the user actually felt.
	/// </summary>
	private static readonly TimeSpan EpisodeLookback = TimeSpan.FromMinutes(1);

	private readonly Pipeline _pipeline;
	private readonly MobileSettings _settings;
	private readonly IHealthStore _healthStore;

	public HealthKitEpisodeRecorder(Pipeline pipeline, MobileSettings settings, IHealthStore healthStore)
	{
		_pipeline = pipeline;
		_settings = settings;
		_healthStore = healthStore;
		_pipeline.AlertFired += OnAlertFired;
	}

	private void OnAlertFired(AlertPayload payload)
	{
		if (!_settings.WriteEpisodesToHealthKit)
		{
			return;
		}

		var episode = new EpisodeRecord(
			Start: payload.Timestamp - EpisodeLookback,
			End: payload.Timestamp,
			Label: "Dysregulation episode",
			Notes: payload.TriggerReason);

		// Fire-and-forget: a HealthKit write must never block or crash the
		// BLE callback path that raised the alert.
		_ = WriteSafelyAsync(episode);
	}

	private async Task WriteSafelyAsync(EpisodeRecord episode)
	{
		try
		{
			await _healthStore.WriteEpisodeAsync(episode).ConfigureAwait(false);
		}
		catch
		{
			// Health write-back is best-effort; swallow so a denied/unavailable
			// store can't take down monitoring.
		}
	}

	public void Dispose() => _pipeline.AlertFired -= OnAlertFired;
}
