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
}
