using System.Media;
using CommunityToolkit.WinUI.Notifications;
using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.App;

/// <summary>
/// Receives alert payloads and dispatches a soft chime and/or toast notification.
/// Designed to be calm — no modal windows, no sudden loud sounds.
/// </summary>
public class AlertDispatcher
{
	private readonly AppSettings _settings;

	public AlertDispatcher(AppSettings settings)
	{
		_settings = settings;
	}

	public void Dispatch(AlertPayload payload)
	{
		// A jarring chime can deepen a shutdown, so a low-arousal alert stays silent and relies on
		// the quiet toast; the hyperarousal meltdown alert keeps its chime.
		if (_settings.EnableChime && payload.Kind != AlertKind.Hypoarousal)
		{
			PlayChime();
		}

		if (_settings.EnableToast)
		{
			ShowToast(payload);
		}
	}

	private void PlayChime()
	{
		try
		{
			if (_settings.ChimeWavPath is not null && File.Exists(_settings.ChimeWavPath))
			{
				using var player = new SoundPlayer(_settings.ChimeWavPath);
				player.Play();
			}
			else
			{
				SystemSounds.Asterisk.Play();
			}
		}
		catch
		{
			// Never let audio failure crash the app.
		}
	}

	private void ShowToast(AlertPayload payload)
	{
		try
		{
			bool gentle = payload.Kind == AlertKind.Hypoarousal;
			string headline = gentle ? "A low, flat moment" : "Meltdown Monitor";
			string suggestion = gentle
				? "When you're ready, a small movement or a sip of water can help you re-engage."
				: _settings.AlertSuggestion;

			new ToastContentBuilder()
				.AddText(headline)
				.AddText(suggestion)
				.AddText($"RMSSD {payload.RmssdAtTrigger:F1} ms (baseline {payload.BaselineAtTrigger:F1} ms)")
				.SetToastDuration(ToastDuration.Long)
				.Show();
		}
		catch
		{
			// Toast may fail if notification centre is unavailable.
		}
	}
}
