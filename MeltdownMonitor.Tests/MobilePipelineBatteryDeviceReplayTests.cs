using System.Runtime.CompilerServices;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Mobile;

namespace MeltdownMonitor.Tests;

/// <summary>
/// Battery level and device info are one-shot BLE reads on connect. On iOS the central manager is
/// created early (for state restoration) — before the pipeline is built — so those reads can fire
/// before any subscriber exists. Unlike the continuous HR/ECG notify streams (and the buffered beat
/// channel), a missed one-shot read is lost forever, leaving the Debug tab's Battery/Device blank.
/// The source latches the latest value and the pipeline replays it on wiring; these cover that.
/// </summary>
[TestClass]
public class MobilePipelineBatteryDeviceReplayTests
{
	[TestMethod]
	public void Battery_AlreadyReadBeforeWiring_IsReplayedOnConstruction()
	{
		var settings = new MobileSettings();
		using var repo = new MeltdownRepository(":memory:");

		// The source read battery before the pipeline (and its event subscription) existed.
		var source = new PreReadSource
		{
			LatestBattery = new BatteryReading(DateTimeOffset.UnixEpoch, 74),
		};

		BatteryReading? forwarded = null;
		using var pipeline = new Pipeline(settings, repo, source);
		pipeline.BatteryUpdated += r => forwarded = r;

		// Constructing the pipeline must surface the already-latched reading without any new event.
		Assert.AreEqual(74, pipeline.LatestBatteryPercent);

		// And a later live notification still flows normally.
		source.RaiseBattery(new BatteryReading(DateTimeOffset.UnixEpoch.AddMinutes(1), 73));
		Assert.AreEqual(73, forwarded?.Percent);
		Assert.AreEqual(73, pipeline.LatestBatteryPercent);
	}

	[TestMethod]
	public void DeviceInfo_AlreadyReadBeforeWiring_IsReplayedOnConstruction()
	{
		var settings = new MobileSettings();
		using var repo = new MeltdownRepository(":memory:");

		var source = new PreReadSource
		{
			LatestDeviceInfo = new DeviceInformation(ModelNumber: "Polar H10", FirmwareRevision: "3.1.1"),
		};

		DeviceInformation? forwarded = null;
		using var pipeline = new Pipeline(settings, repo, source);
		pipeline.DeviceInfoUpdated += i => forwarded = i;
		Assert.AreEqual("Polar H10", pipeline.LatestDeviceInfo?.ModelNumber);

		// A later live read (e.g. another DIS field arriving) still flows normally.
		source.RaiseDeviceInfo(new DeviceInformation(ModelNumber: "Polar H10", FirmwareRevision: "3.1.1", SerialNumber: "ABC123"));
		Assert.AreEqual("ABC123", forwarded?.SerialNumber);
		Assert.AreEqual("ABC123", pipeline.LatestDeviceInfo?.SerialNumber);
	}

	[TestMethod]
	public void DebugTab_AttachingAfterReads_ConvergesFromLatchedPipelineState()
	{
		var settings = new MobileSettings();
		using var repo = new MeltdownRepository(":memory:");

		// Both one-shot reads completed before any UI existed.
		var source = new PreReadSource
		{
			LatestBattery = new BatteryReading(DateTimeOffset.UnixEpoch, 74),
			LatestDeviceInfo = new DeviceInformation(ModelNumber: "Polar H10", FirmwareRevision: "3.1.1"),
		};

		using var pipeline = new Pipeline(settings, repo, source);

		// The Debug view model attaches well after the pipeline already latched the values — it must
		// seed from the pipeline rather than wait for an event that already fired (the screenshot bug).
		var debug = new MeltdownMonitor.Mobile.ViewModels.DebugViewModel();
		debug.AttachPipeline(pipeline);

		StringAssert.Contains(debug.BatteryText, "74");
		StringAssert.Contains(debug.DeviceText, "Polar H10");
	}

	[TestMethod]
	public void NothingReadYet_ReplaysNothing()
	{
		var settings = new MobileSettings();
		using var repo = new MeltdownRepository(":memory:");

		// No latched values — the pipeline must not invent a reading.
		using var pipeline = new Pipeline(settings, repo, new PreReadSource());
		Assert.IsNull(pipeline.LatestBatteryPercent);
		Assert.IsNull(pipeline.LatestDeviceInfo);
	}

	// A beat source that also exposes the battery/device-info capabilities, pre-seeded as if the
	// one-shot reads completed before the pipeline subscribed.
	private sealed class PreReadSource : IBeatSource, IBatterySource, IDeviceInfoSource
	{
		public event Action<BatteryReading>? BatteryLevelChanged;
		public event Action<DeviceInformation>? DeviceInformationChanged;

		public BatteryReading? LatestBattery { get; init; }
		public DeviceInformation? LatestDeviceInfo { get; init; }

		public void RaiseBattery(BatteryReading reading) => BatteryLevelChanged?.Invoke(reading);

		public void RaiseDeviceInfo(DeviceInformation info) => DeviceInformationChanged?.Invoke(info);

		public async IAsyncEnumerable<Beat> GetBeatsAsync(
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			await Task.Yield();
			yield break;
		}
	}
}
