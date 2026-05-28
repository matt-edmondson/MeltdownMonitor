using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// Platform-neutral facade over the OS notification system. On iOS this
/// wraps UNUserNotificationCenter; see design doc §4.2.
/// </summary>
public interface INotificationDispatcher
{
	Task<bool> RequestAuthorizationAsync();

	Task PostAlertAsync(AlertPayload payload);

	Task PostStatusAsync(DetectorState state);
}
