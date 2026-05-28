using AVFoundation;
using Foundation;

namespace MeltdownMonitor.iOS.Services;

/// <summary>
/// Configures the shared <c>AVAudioSession</c> for the alert chime. Per
/// design doc §4.6 the session uses the <c>Playback</c> category with
/// <c>MixWithOthers</c> so a chime played from a backgrounded BLE callback
/// doesn't stop the user's music. The <c>audio</c> background mode in
/// Info.plist combined with this session is what keeps the chime path
/// alive while the screen is locked.
/// </summary>
public static class AudioSessionConfigurator
{
	public static void Configure()
	{
		var session = AVAudioSession.SharedInstance();

		NSError? setCategoryError = session.SetCategory(
			AVAudioSessionCategory.Playback,
			AVAudioSessionCategoryOptions.MixWithOthers);

		if (setCategoryError is not null)
		{
			// Fall back to ambient if the device rejects Playback for some
			// reason (e.g. a managed profile) — better silent than crashing.
			session.SetCategory(AVAudioSessionCategory.Ambient);
		}

		session.SetActive(true, out _);
	}
}
