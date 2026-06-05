using Android.Content;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.Android.Services;

/// <summary>
/// <see cref="ILiveActivityController"/> backed by the foreground service's
/// ongoing notification (design doc §5.5). On iOS the Live Activity is an extra
/// Lock Screen surface treated as a fast-follow; on Android the equivalent has
/// to exist on day one as the foreground service's mandatory notification, so
/// this is the natural home for the live state rather than a deferred nicety.
///
/// <para>
/// Driven by the shared <see cref="LiveActivityPublisher"/>, which already
/// throttles updates to ≤ 1 Hz and bypasses the throttle on state changes — the
/// exact refresh budget an ongoing notification wants. <see cref="StartAsync"/>
/// and <see cref="UpdateAsync"/> rebuild the notification with the current state
/// colour, heart rate, and RMSSD-vs-baseline ratio.
/// </para>
///
/// <para>
/// <see cref="EndAsync"/> does <i>not</i> stop the service: on Android the
/// foreground service is the monitoring lifecycle itself (owned by
/// <see cref="AndroidCompositionRoot"/>), so ending the "activity" only resets
/// the notification to its generic monitoring line. The service is torn down
/// when monitoring stops, via <c>AndroidCompositionRoot.StopAsync</c>.
/// </para>
/// </summary>
public sealed class OngoingNotificationActivityController : ILiveActivityController
{
	private readonly Context _context;

	public OngoingNotificationActivityController(Context context) =>
		_context = context ?? throw new ArgumentNullException(nameof(context));

	public bool IsActive { get; private set; }

	public Task StartAsync(LiveActivityContent content)
	{
		IsActive = true;
		MonitoringService.UpdateContent(_context, content);
		return Task.CompletedTask;
	}

	public Task UpdateAsync(LiveActivityContent content)
	{
		IsActive = true;
		MonitoringService.UpdateContent(_context, content);
		return Task.CompletedTask;
	}

	public Task EndAsync()
	{
		IsActive = false;
		// Leave the foreground service running (it is what keeps monitoring alive);
		// the next service (re)start renders the generic monitoring line.
		return Task.CompletedTask;
	}
}
