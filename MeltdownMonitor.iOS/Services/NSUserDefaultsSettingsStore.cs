using System.Text.Json;
using Foundation;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.iOS.Services;

/// <summary>
/// Persists <see cref="MobileSettings"/> in <c>NSUserDefaults</c> (design
/// doc §13(2)). The full settings blob is stored as a single JSON string so
/// adding new fields doesn't require introducing a new key; the disclaimer
/// flag keeps its own scalar key so it can be read before the JSON blob is
/// deserialized (the disclaimer screen blocks the rest of composition).
/// </summary>
public sealed class NSUserDefaultsSettingsStore : IMobileSettingsStore
{
	private const string DisclaimerKey = "com.thethreethousands.meltdownmonitor.disclaimerAccepted";
	private const string SettingsKey = "com.thethreethousands.meltdownmonitor.settings.v1";

	public bool LoadDisclaimerAccepted() =>
		NSUserDefaults.StandardUserDefaults.BoolForKey(DisclaimerKey);

	public void SaveDisclaimerAccepted(bool accepted)
	{
		NSUserDefaults.StandardUserDefaults.SetBool(accepted, DisclaimerKey);
	}

	public MobileSettings LoadSettings()
	{
		string? json = NSUserDefaults.StandardUserDefaults.StringForKey(SettingsKey);
		if (string.IsNullOrEmpty(json))
		{
			var fresh = new MobileSettings();
			fresh.IsDisclaimerAccepted = LoadDisclaimerAccepted();
			return fresh;
		}

		try
		{
			var dto = JsonSerializer.Deserialize<SettingsDto>(json);
			if (dto is null)
			{
				return new MobileSettings { IsDisclaimerAccepted = LoadDisclaimerAccepted() };
			}

			return dto.ToSettings(LoadDisclaimerAccepted());
		}
		catch (JsonException)
		{
			// Forward-compatibility: if a future version's schema lands here
			// during a rollback, fall back to defaults rather than crashing
			// on launch.
			return new MobileSettings { IsDisclaimerAccepted = LoadDisclaimerAccepted() };
		}
	}

	public void SaveSettings(MobileSettings settings)
	{
		var dto = SettingsDto.FromSettings(settings);
		string json = JsonSerializer.Serialize(dto);
		NSUserDefaults.StandardUserDefaults.SetString(json, SettingsKey);

		// Keep the disclaimer key in sync so the next launch can answer the
		// disclaimer question before deserializing the JSON blob.
		SaveDisclaimerAccepted(settings.IsDisclaimerAccepted);
	}

	private sealed record SettingsDto(
		DetectionThresholds Thresholds,
		PolarDeviceType DeviceType,
		DateTimeOffset? PausedUntil,
		bool EnableChime,
		bool EnableNotifications,
		string AlertSuggestion,
		bool WriteEpisodesToHealthKit)
	{
		public static SettingsDto FromSettings(MobileSettings s) => new(
			s.Thresholds,
			s.DeviceType,
			s.PausedUntil,
			s.EnableChime,
			s.EnableNotifications,
			s.AlertSuggestion,
			s.WriteEpisodesToHealthKit);

		public MobileSettings ToSettings(bool disclaimerAccepted) => new()
		{
			Thresholds = Thresholds,
			DeviceType = DeviceType,
			PausedUntil = PausedUntil,
			EnableChime = EnableChime,
			EnableNotifications = EnableNotifications,
			AlertSuggestion = AlertSuggestion,
			WriteEpisodesToHealthKit = WriteEpisodesToHealthKit,
			IsDisclaimerAccepted = disclaimerAccepted,
		};
	}
}
