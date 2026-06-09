namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// An optional capability a beat source may also implement to surface the
/// sensor's identity from the GATT Device Information Service (<c>0x180A</c>).
/// The pipeline checks for this interface on its <see cref="IBeatSource"/> and,
/// when present, forwards the details to the UI. The fields are read once on
/// connect; some transports deliver them field-by-field, so the event may fire
/// more than once with an increasingly complete record.
/// </summary>
public interface IDeviceInfoSource
{
	/// <summary>
	/// Raised when device information is read (typically once on connect). May
	/// fire on a background BLE thread, so subscribers must marshal to their UI
	/// thread.
	/// </summary>
	event Action<DeviceInformation>? DeviceInformationChanged;

	/// <summary>
	/// The device information accumulated so far, or <c>null</c> until at least one
	/// field has been read. Like <see cref="IBatterySource.LatestBattery"/>, the DIS
	/// characteristics are read once on connect, so a subscriber that wires up
	/// <see cref="DeviceInformationChanged"/> after the read would miss it; the
	/// pipeline replays this on wiring. Default <c>null</c> for sources that don't latch it.
	/// </summary>
	DeviceInformation? LatestDeviceInfo => null;
}
