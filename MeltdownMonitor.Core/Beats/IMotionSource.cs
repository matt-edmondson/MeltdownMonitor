namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// An optional capability surfacing a tri-axial motion stream. A Polar beat source implements it
/// directly (the PMD accelerometer rides the same BLE connection as the heart-rate beats); the
/// device-IMU fallback is a standalone implementation the composition root constructs when the
/// connected sensor offers no PMD motion. The pipeline checks for this interface the same way it
/// checks <see cref="IBatterySource"/> / <see cref="IContactSource"/> — a source that doesn't
/// implement it simply contributes no motion context.
/// </summary>
public interface IMotionSource
{
	/// <summary>
	/// Raised for each decoded acceleration sample. May arrive in bursts (BLE batches frames) and
	/// on a background thread, so subscribers must marshal to their own thread and tolerate
	/// high-rate delivery.
	/// </summary>
	event Action<MotionSample>? MotionSampleReceived;
}
