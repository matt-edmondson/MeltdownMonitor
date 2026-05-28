namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// Plays the soft alert chime. On iOS this requires an
/// <c>AVAudioSession</c> configured for the <c>playback</c> category with
/// <c>MixWithOthers</c> so the chime can sound from a backgrounded BLE
/// callback without stomping on music (design doc §4.6).
/// </summary>
public interface IChimePlayer
{
	void PlayAlertChime();
}
