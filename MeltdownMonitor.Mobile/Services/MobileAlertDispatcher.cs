using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// Mobile mirror of <c>MeltdownMonitor.App.AlertDispatcher</c>. Subscribes
/// to <see cref="Pipeline"/> events and fans them out to the platform's
/// notification centre and optional audio chime, respecting the user's
/// settings. Stays platform-neutral by talking only through
/// <see cref="INotificationDispatcher"/> and <see cref="IChimePlayer"/>.
/// </summary>
public sealed class MobileAlertDispatcher : IDisposable
{
	private readonly MobileSettings _settings;
	private readonly INotificationDispatcher _notifications;
	private readonly IChimePlayer? _chime;
	private readonly Pipeline _pipeline;

	public MobileAlertDispatcher(
		Pipeline pipeline,
		MobileSettings settings,
		INotificationDispatcher notifications,
		IChimePlayer? chime = null)
	{
		_pipeline = pipeline;
		_settings = settings;
		_notifications = notifications;
		_chime = chime;

		_pipeline.AlertFired += OnAlertFired;
		_pipeline.StateChanged += OnStateChanged;
	}

	private void OnAlertFired(AlertPayload payload)
	{
		if (_settings.EnableChime)
		{
			try
			{
				_chime?.PlayAlertChime();
			}
			catch
			{
				// Never let audio failure crash the BLE callback path.
			}
		}

		if (_settings.EnableNotifications)
		{
			_ = _notifications.PostAlertAsync(payload);
		}
	}

	private void OnStateChanged(DetectorState state)
	{
		if (!_settings.EnableNotifications)
		{
			return;
		}

		_ = _notifications.PostStatusAsync(state);
	}

	public void Dispose()
	{
		_pipeline.AlertFired -= OnAlertFired;
		_pipeline.StateChanged -= OnStateChanged;
	}
}
