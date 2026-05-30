namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// A single battery-level reading from the sensor's GATT Battery Service
/// (<c>0x180F</c> / Battery Level characteristic <c>0x2A19</c>).
/// </summary>
/// <param name="Timestamp">When the reading was received (UTC).</param>
/// <param name="Percent">Charge level, 0–100.</param>
public record BatteryReading(DateTimeOffset Timestamp, int Percent);
