namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// An optional capability a beat source may also implement to surface the
/// sensor's battery level from the standard GATT Battery Service
/// (<c>0x180F</c> / Battery Level characteristic <c>0x2A19</c>). The pipeline
/// checks for this interface on its <see cref="IBeatSource"/> and, when present,
/// forwards readings to the UI and persistence — sources that don't implement it
/// simply report no battery data.
/// </summary>
public interface IBatterySource
{
	/// <summary>
	/// Raised when a fresh battery level arrives — once on connect (the initial
	/// read) and again whenever the sensor notifies a change. May fire on a
	/// background BLE thread, so subscribers must marshal to their UI thread.
	/// </summary>
	event Action<BatteryReading>? BatteryLevelChanged;

	/// <summary>
	/// The most recent battery reading already observed, or <c>null</c> until the
	/// first read. The initial read is one-shot (unlike the continuous notify
	/// stream), so a subscriber that wires up <see cref="BatteryLevelChanged"/>
	/// after it has already fired — e.g. on iOS the restoring central manager is
	/// created early in launch, before the pipeline is built — would otherwise
	/// miss it entirely. The pipeline replays this on wiring so a late subscriber
	/// still converges. Default <c>null</c> for sources that don't latch it.
	/// </summary>
	BatteryReading? LatestBattery => null;
}
