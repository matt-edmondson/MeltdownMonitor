using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Motion;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MovementMonitorTests
{
	private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	// Magnitude == |Z| when X and Y are zero, so we can drive an exact RMS.
	private static void Feed(MovementMonitor monitor, IEnumerable<double> magnitudes, int hz = 10,
		MotionSourceKind source = MotionSourceKind.PolarStrap)
	{
		double dt = 1.0 / hz;
		int i = 0;
		foreach (double m in magnitudes)
		{
			monitor.Add(new MotionSample(Start.AddSeconds(i * dt), 0, 0, m, source));
			i++;
		}
	}

	private static IEnumerable<double> Alternating(double a, double b, int count)
	{
		for (int i = 0; i < count; i++)
		{
			yield return (i % 2 == 0) ? a : b;
		}
	}

	[TestMethod]
	public void SingleSample_IsUnknown()
	{
		var monitor = new MovementMonitor();
		monitor.Add(new MotionSample(Start, 0, 0, 1.0, MotionSourceKind.DeviceImu));
		Assert.AreEqual(MovementLevel.Unknown, monitor.Level);
	}

	[TestMethod]
	public void ConstantMagnitude_IsStill()
	{
		var monitor = new MovementMonitor();
		Feed(monitor, Enumerable.Repeat(1.0, 20));
		Assert.AreEqual(MovementLevel.Still, monitor.Level);
		Assert.AreEqual(0.0, monitor.IntensityG, 1e-9);
	}

	[TestMethod]
	public void SmallOscillation_IsLight()
	{
		var monitor = new MovementMonitor();
		// Alternating 1.00/1.06 → RMS deviation 0.03 g (Light band 0.02–0.08).
		Feed(monitor, Alternating(1.00, 1.06, 20));
		Assert.AreEqual(MovementLevel.Light, monitor.Level);
	}

	[TestMethod]
	public void WalkingOscillation_IsModerate()
	{
		var monitor = new MovementMonitor();
		// Alternating 1.00/1.30 → RMS deviation 0.15 g (Moderate band 0.08–0.25).
		Feed(monitor, Alternating(1.00, 1.30, 20));
		Assert.AreEqual(MovementLevel.Moderate, monitor.Level);
	}

	[TestMethod]
	public void LargeOscillation_IsVigorous()
	{
		var monitor = new MovementMonitor();
		// Alternating 1.00/1.70 → RMS deviation 0.35 g (≥ 0.25).
		Feed(monitor, Alternating(1.00, 1.70, 20));
		Assert.AreEqual(MovementLevel.Vigorous, monitor.Level);
	}

	[TestMethod]
	public void GravityOffsetDoesNotMatter()
	{
		// Linear-acceleration source reads ~0 g at rest; raw accel reads ~1 g. Both are Still
		// because intensity is the AC component about the rolling mean.
		var withGravity = new MovementMonitor();
		Feed(withGravity, Enumerable.Repeat(1.0, 20));

		var withoutGravity = new MovementMonitor();
		Feed(withoutGravity, Enumerable.Repeat(0.0, 20));

		Assert.AreEqual(MovementLevel.Still, withGravity.Level);
		Assert.AreEqual(MovementLevel.Still, withoutGravity.Level);
	}

	[TestMethod]
	public void OldSamplesAgeOutOfWindow()
	{
		var monitor = new MovementMonitor { Window = TimeSpan.FromSeconds(2) };
		// Vigorous burst, then a long quiet stretch beyond the window → returns to Still.
		Feed(monitor, Alternating(1.00, 1.70, 10));
		Assert.AreEqual(MovementLevel.Vigorous, monitor.Level);

		for (int i = 0; i < 40; i++)
		{
			monitor.Add(new MotionSample(Start.AddSeconds(10 + (i * 0.1)), 0, 0, 1.0, MotionSourceKind.PolarStrap));
		}

		Assert.AreEqual(MovementLevel.Still, monitor.Level);
	}

	[TestMethod]
	public void StrapSamplesSuppressDeviceImuWhileActive()
	{
		var monitor = new MovementMonitor();
		// Strap reports vigorous movement...
		for (int i = 0; i < 10; i++)
		{
			double m = (i % 2 == 0) ? 1.0 : 1.7;
			monitor.Add(new MotionSample(Start.AddSeconds(i * 0.1), 0, 0, m, MotionSourceKind.PolarStrap));
		}

		// ...while the phone IMU (on a desk) simultaneously reports stillness. The strap wins.
		for (int i = 0; i < 10; i++)
		{
			monitor.Add(new MotionSample(Start.AddSeconds(1.0 + (i * 0.1)), 0, 0, 1.0, MotionSourceKind.DeviceImu));
		}

		Assert.AreEqual(MovementLevel.Vigorous, monitor.Level);
		Assert.AreEqual(MotionSourceKind.PolarStrap, monitor.LatestSource);
	}

	[TestMethod]
	public void DeviceImuUsedWhenNoStrap()
	{
		var monitor = new MovementMonitor();
		Feed(monitor, Alternating(1.00, 1.30, 20), source: MotionSourceKind.DeviceImu);
		Assert.AreEqual(MovementLevel.Moderate, monitor.Level);
		Assert.AreEqual(MotionSourceKind.DeviceImu, monitor.LatestSource);
	}

	[TestMethod]
	public void TracksLatestSourceAndReset()
	{
		var monitor = new MovementMonitor();
		Feed(monitor, Alternating(1.00, 1.30, 10), source: MotionSourceKind.DeviceImu);
		Assert.AreEqual(MotionSourceKind.DeviceImu, monitor.LatestSource);

		monitor.Reset();
		Assert.AreEqual(MovementLevel.Unknown, monitor.Level);
		Assert.AreEqual(0.0, monitor.IntensityG, 1e-9);
		Assert.IsNull(monitor.LatestSource);
	}
}
