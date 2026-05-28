using AudioToolbox;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.iOS.Services;

/// <summary>
/// Plays a soft system chime via <see cref="SystemSound"/>. We deliberately
/// pick a calm system sound (id 1057 — the short three-note "TweetSent"
/// chime) rather than an alert tone, in keeping with the design doc's
/// "calm, never sudden" alert posture.
/// </summary>
public sealed class AudioChimePlayer : IChimePlayer
{
	private const uint CalmChimeSoundId = 1057;

	private readonly SystemSound _sound = new(CalmChimeSoundId);

	public void PlayAlertChime() => _sound.PlaySystemSound();
}
