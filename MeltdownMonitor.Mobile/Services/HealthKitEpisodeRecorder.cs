using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// Bridges <see cref="Pipeline.AlertFired"/> to <see cref="IHealthStore.WriteEpisodeAsync"/>
/// so each dysregulation episode lands in Apple Health as a "Mind &amp; Body"
/// workout — gives the user a durable record that survives an app uninstall
/// and integrates with their existing health history.
///
/// Gated on <see cref="MobileSettings.WriteEpisodesToHealthKit"/> so it
/// stays off by default (Apple's wellness-not-medical guidance, design doc
/// §11). Window the recorder writes is from the alert timestamp back to
/// the most recent <see cref="DetectorState.Warning"/> transition (rough
/// proxy for "the episode") with a 5-minute floor when the warning window
/// can't be determined.
/// </summary>
public sealed class HealthKitEpisodeRecorder : IDisposable
{
	private static readonly TimeSpan FallbackEpisodeWindow = TimeSpan.FromMinutes(5);

	private readonly Pipeline _pipeline;
	private readonly MobileSettings _settings;
	private readonly IHealthStore _healthStore;

	private DateTimeOffset? _warningStart;

	public HealthKitEpisodeRecorder(
		Pipeline pipeline,
		MobileSettings settings,
		IHealthStore healthStore)
	{
		_pipeline = pipeline;
		_settings = settings;
		_healthStore = healthStore;

		_pipeline.StateChanged += OnStateChanged;
		_pipeline.AlertFired += OnAlertFired;
	}

	private void OnStateChanged(DetectorState state)
	{
		if (state == DetectorState.Warning)
		{
			_warningStart = DateTimeOffset.UtcNow;
		}
		else if (state is DetectorState.Idle or DetectorState.Watching)
		{
			_warningStart = null;
		}
	}

	private void OnAlertFired(AlertPayload payload)
	{
		if (!_settings.WriteEpisodesToHealthKit)
		{
			return;
		}

		var end = payload.Timestamp;
		var start = _warningStart ?? end - FallbackEpisodeWindow;
		if (start >= end)
		{
			start = end - FallbackEpisodeWindow;
		}

		var episode = new EpisodeRecord(
			Start: start,
			End: end,
			Label: "Dysregulation episode",
			Notes: payload.TriggerReason);

		_ = _healthStore.WriteEpisodeAsync(episode);
	}

	public void Dispose()
	{
		_pipeline.StateChanged -= OnStateChanged;
		_pipeline.AlertFired -= OnAlertFired;
	}
}
