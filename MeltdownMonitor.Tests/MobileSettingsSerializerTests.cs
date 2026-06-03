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
			EnableLiveActivity = true,
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
		Assert.AreEqual(original.EnableLiveActivity, restored.EnableLiveActivity);
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

	[TestMethod]
	public void RoundTrip_PreservesRegulationTrailLength()
	{
		var settings = new MobileSettings { RegulationTrailLength = 96 };

		string json = MobileSettingsSerializer.Serialize(settings);
		MobileSettings restored = MobileSettingsSerializer.Deserialize(json);

		Assert.AreEqual(96, restored.RegulationTrailLength);
	}

	[TestMethod]
	public void Default_RegulationTrailLength_Is48()
	{
		Assert.AreEqual(48, new MobileSettings().RegulationTrailLength);
	}

	[TestMethod]
	public void RoundTrip_PreservesJitterExaggeration()
	{
		var settings = new MobileSettings { JitterExaggeration = 2.5 };

		string json = MobileSettingsSerializer.Serialize(settings);
		MobileSettings restored = MobileSettingsSerializer.Deserialize(json);

		Assert.AreEqual(2.5, restored.JitterExaggeration, 1e-9);
	}

	[TestMethod]
	public void Default_JitterExaggeration_Is1()
	{
		Assert.AreEqual(1.0, new MobileSettings().JitterExaggeration, 1e-9);
	}

	[TestMethod]
	public void RoundTrip_PreservesLobeThickness()
	{
		var settings = new MobileSettings { LobeThickness = 2.5 };

		string json = MobileSettingsSerializer.Serialize(settings);
		MobileSettings restored = MobileSettingsSerializer.Deserialize(json);

		Assert.AreEqual(2.5, restored.LobeThickness, 1e-9);
	}

	[TestMethod]
	public void Default_LobeThickness_Is1()
	{
		Assert.AreEqual(1.0, new MobileSettings().LobeThickness, 1e-9);
	}

	[TestMethod]
	public void RoundTrip_PreservesLobeSegments()
	{
		var settings = new MobileSettings { LobeSegments = 128 };

		string json = MobileSettingsSerializer.Serialize(settings);
		MobileSettings restored = MobileSettingsSerializer.Deserialize(json);

		Assert.AreEqual(128, restored.LobeSegments);
	}

	[TestMethod]
	public void Default_LobeSegments_Is96()
	{
		Assert.AreEqual(96, new MobileSettings().LobeSegments);
	}
}
