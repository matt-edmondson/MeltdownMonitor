using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeltdownMonitor.Mobile.Services;

/// <summary>
/// JSON round-trip for <see cref="MobileSettings"/>. Persisting the whole
/// object as a single JSON blob (rather than a key per field) keeps the
/// platform store trivial and means there is only one value to migrate later
/// (design doc §6.4). Platform-neutral so it can be unit-tested off-device.
/// </summary>
public static class MobileSettingsSerializer
{
	private static readonly JsonSerializerOptions Options = new()
	{
		// Enums as names so a stored blob survives reordering of enum members.
		Converters = { new JsonStringEnumConverter() },
		DefaultIgnoreCondition = JsonIgnoreCondition.Never,
	};

	public static string Serialize(MobileSettings settings) =>
		JsonSerializer.Serialize(settings, Options);

	/// <summary>
	/// Rehydrates settings from a blob produced by <see cref="Serialize"/>.
	/// Returns a fresh default <see cref="MobileSettings"/> when the blob is
	/// null, empty, or unparseable — a corrupt store should never block launch.
	/// </summary>
	public static MobileSettings Deserialize(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return new MobileSettings();
		}

		try
		{
			return JsonSerializer.Deserialize<MobileSettings>(json, Options) ?? new MobileSettings();
		}
		catch (JsonException)
		{
			return new MobileSettings();
		}
	}
}
