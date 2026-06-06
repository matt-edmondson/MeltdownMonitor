using CoreMotion;
using Foundation;
using MeltdownMonitor.Core.Beats;

namespace MeltdownMonitor.Ble.Apple;

/// <summary>
/// Device-IMU motion fallback for iOS, used when the connected sensor exposes no Polar PMD
/// accelerometer (a non-Polar strap). Streams the phone's own accelerometer via CoreMotion. The
/// phone is a coarser proxy than a chest strap — it only moves when the device does — so the
/// movement monitor prefers strap samples whenever both are live. Requires the
/// <c>NSMotionUsageDescription</c> Info.plist key.
/// </summary>
public sealed class ImuMotionSource : IMotionSource, IDisposable
{
	private readonly CMMotionManager _manager = new();
	private bool _started;

	/// <inheritdoc />
	public event Action<MotionSample>? MotionSampleReceived;

	/// <param name="hz">Sample rate (default 50 Hz — ample for movement detection, easy on the battery).</param>
	public ImuMotionSource(double hz = 50.0)
	{
		if (!_manager.AccelerometerAvailable)
		{
			return;
		}

		_manager.AccelerometerUpdateInterval = 1.0 / hz;
		_manager.StartAccelerometerUpdates(NSOperationQueue.CurrentQueue ?? new NSOperationQueue(), OnUpdate);
		_started = true;
	}

	// CMAcceleration is already in g units, matching MotionSample.
	private void OnUpdate(CMAccelerometerData? data, NSError? error)
	{
		if (data is null || error is not null)
		{
			return;
		}

		CMAcceleration a = data.Acceleration;
		MotionSampleReceived?.Invoke(
			new MotionSample(DateTimeOffset.UtcNow, a.X, a.Y, a.Z, MotionSourceKind.DeviceImu));
	}

	public void Dispose()
	{
		if (_started && _manager.AccelerometerActive)
		{
			_manager.StopAccelerometerUpdates();
		}

		_manager.Dispose();
	}
}
