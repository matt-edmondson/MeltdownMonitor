using Android.Content;
using Android.Media;
using MeltdownMonitor.Mobile.Services;
using Uri = Android.Net.Uri;

namespace MeltdownMonitor.Android.Services;

/// <summary>
/// <see cref="IChimePlayer"/> that sounds a short, calm cue for the hyperarousal
/// alert. Plays the platform default notification tone through a
/// <see cref="Ringtone"/> tagged with the Notification usage, and briefly takes
/// transient-may-duck audio focus so it lowers the user's music rather than
/// stopping it (design doc §5.6 — the Android analog of the iOS
/// <c>mixWithOthers</c> choice). Playing from the foreground service while
/// backgrounded is allowed because the service is running, so no special
/// background-audio capability is needed.
/// </summary>
public sealed class AndroidChimePlayer : IChimePlayer
{
	private readonly Context _context;
	private readonly AudioManager? _audioManager;
	private readonly AudioAttributes? _attributes;

	public AndroidChimePlayer(Context context)
	{
		_context = context ?? throw new ArgumentNullException(nameof(context));
		_audioManager = (AudioManager?)context.GetSystemService(Context.AudioService);
		_attributes = new AudioAttributes.Builder()!
			.SetUsage(AudioUsageKind.Notification)!
			.SetContentType(AudioContentType.Sonification)!
			.Build();
	}

	public void PlayAlertChime()
	{
		try
		{
			var toneUri = RingtoneManager.GetDefaultUri(RingtoneType.Notification);
			if (toneUri is null)
			{
				return;
			}

			DuckMusic();
			PlayTone(toneUri);
		}
		catch (global::Java.Lang.Exception)
		{
			// A failed chime must never take down the BLE callback path that asked
			// for it — the notification still fires.
		}
	}

	private void DuckMusic()
	{
		if (_audioManager is null || _attributes is null || !OperatingSystem.IsAndroidVersionAtLeast(26))
		{
			return;
		}

		var request = new AudioFocusRequestClass.Builder(AudioFocus.GainTransientMayDuck)!
			.SetAudioAttributes(_attributes)!
			.Build();
		if (request is not null)
		{
			_audioManager.RequestAudioFocus(request);
			// Release shortly after the short tone; the system restores the duck.
			_ = ReleaseFocusSoonAsync(request);
		}
	}

	private async Task ReleaseFocusSoonAsync(AudioFocusRequestClass request)
	{
		await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
		if (OperatingSystem.IsAndroidVersionAtLeast(26))
		{
			_audioManager?.AbandonAudioFocusRequest(request);
		}
	}

	private void PlayTone(Uri toneUri)
	{
		var ringtone = RingtoneManager.GetRingtone(_context, toneUri);
		if (ringtone is null)
		{
			return;
		}

		// AudioAttributes is API 21; the app's floor is API 26, so the deprecated
		// StreamType fallback is unreachable and intentionally omitted.
		if (_attributes is not null)
		{
			ringtone.AudioAttributes = _attributes;
		}

		ringtone.Play();
	}
}
