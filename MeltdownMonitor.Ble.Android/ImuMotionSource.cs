using Android.Content;
using Android.Hardware;
using Android.Runtime;
using MeltdownMonitor.Core.Beats;

namespace MeltdownMonitor.Ble.Android;

/// <summary>
/// Device-IMU motion fallback for Android, used when the connected sensor exposes no Polar PMD
/// accelerometer (a non-Polar strap). Streams the phone's own accelerometer via
/// <see cref="SensorManager"/>. The phone is a coarser proxy than a chest strap — it only moves
/// when the device does — so the movement monitor prefers strap samples whenever both are live.
/// The raw accelerometer needs no runtime permission. Values are converted from m/s² to g.
/// </summary>
public sealed class ImuMotionSource : Java.Lang.Object, ISensorEventListener, IMotionSource
{
	private const double GravityMetersPerSecondSquared = 9.80665;

	private readonly SensorManager? _sensorManager;
	private readonly Sensor? _accelerometer;

	/// <inheritdoc />
	public event Action<MotionSample>? MotionSampleReceived;

	public ImuMotionSource(Context context)
	{
		_sensorManager = context.GetSystemService(Context.SensorService) as SensorManager;
		_accelerometer = _sensorManager?.GetDefaultSensor(SensorType.Accelerometer);
		if (_accelerometer is not null)
		{
			// SensorDelay.Game is ~50 Hz — plenty for movement classification.
			_sensorManager?.RegisterListener(this, _accelerometer, SensorDelay.Game);
		}
	}

	public void OnAccuracyChanged(Sensor? sensor, [GeneratedEnum] SensorStatus accuracy)
	{
		// Accuracy changes don't affect coarse movement classification.
	}

	public void OnSensorChanged(SensorEvent? e)
	{
		if (e?.Values is null || e.Values.Count < 3)
		{
			return;
		}

		double x = e.Values[0] / GravityMetersPerSecondSquared;
		double y = e.Values[1] / GravityMetersPerSecondSquared;
		double z = e.Values[2] / GravityMetersPerSecondSquared;
		MotionSampleReceived?.Invoke(
			new MotionSample(DateTimeOffset.UtcNow, x, y, z, MotionSourceKind.DeviceImu));
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_sensorManager?.UnregisterListener(this);
		}

		base.Dispose(disposing);
	}
}
