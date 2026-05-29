using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MobileSettingsSerializerTests
{
	[TestMethod]
	public void RoundTrip_PreservesEveryField()
	{
		var original = new MobileSettings
		{
			DeviceType = PolarDeviceType.VeritySense,
			PausedUntil = new DateTimeOffset(2026, 5, 29, 14, 30, 0, TimeSpan.Zero),
			EnableChime = false,
			EnableNotifications = false,
			AlertSuggestion = "Breathe. You've got this.",
			WriteEpisodesToHealthKit = true,
			PeripheralIdentifier = "11112222-3333-4444-5555-666677778888",
			IsDisclaimerAccepted = true,
			Thresholds = new DetectionThresholds
			{
				RmssdWarningDropFraction = 0.42,
				HrWarningRiseFraction = 0.22,
				WarningHoldDuration = TimeSpan.FromSeconds(45),
				AlertingEscalationDuration = TimeSpan.FromSeconds(90),
				RmssdAlertingDropFraction = 0.55,
				CooldownDuration = TimeSpan.FromMinutes(7),
				UseLfHfCorroboration = true,
				LfHfWarningRiseFraction = 0.6,
			},
		};

		var restored = MobileSettingsSerializer.Deserialize(MobileSettingsSerializer.Serialize(original));

		Assert.AreEqual(original.DeviceType, restored.DeviceType);
		Assert.AreEqual(original.PausedUntil, restored.PausedUntil);
		Assert.AreEqual(original.EnableChime, restored.EnableChime);
		Assert.AreEqual(original.EnableNotifications, restored.EnableNotifications);
		Assert.AreEqual(original.AlertSuggestion, restored.AlertSuggestion);
		Assert.AreEqual(original.WriteEpisodesToHealthKit, restored.WriteEpisodesToHealthKit);
		Assert.AreEqual(original.PeripheralIdentifier, restored.PeripheralIdentifier);
		Assert.AreEqual(original.IsDisclaimerAccepted, restored.IsDisclaimerAccepted);
		Assert.AreEqual(original.Thresholds, restored.Thresholds);
	}

	[TestMethod]
	public void Serialize_IsStable_ForIdenticalSettings()
	{
		var a = new MobileSettings { AlertSuggestion = "x", WriteEpisodesToHealthKit = true };
		var b = new MobileSettings { AlertSuggestion = "x", WriteEpisodesToHealthKit = true };

		Assert.AreEqual(MobileSettingsSerializer.Serialize(a), MobileSettingsSerializer.Serialize(b));
	}

	[TestMethod]
	public void Deserialize_NullOrGarbage_ReturnsDefaults()
	{
		Assert.IsTrue(MobileSettingsSerializer.Deserialize(null).EnableChime);
		Assert.IsTrue(MobileSettingsSerializer.Deserialize("").EnableChime);
		Assert.IsTrue(MobileSettingsSerializer.Deserialize("{not json").EnableChime);
	}
}
