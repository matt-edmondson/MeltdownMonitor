using System.Runtime.CompilerServices;
using MeltdownMonitor.Core.Beats;

namespace MeltdownMonitor.Ble.Apple;

/// <summary>
/// CoreBluetooth-backed Polar HR source. Phase 1 scaffold — the
/// CBCentralManager wrap, state restoration (design doc §4.1) and
/// the shared parser/filter wiring land in Phase 2.
/// </summary>
public sealed class PolarHrSource : IBeatSource
{
	private readonly PolarDeviceType _deviceType;

	public PolarHrSource(PolarDeviceType deviceType = PolarDeviceType.Auto)
	{
		_deviceType = deviceType;
	}

	public async IAsyncEnumerable<Beat> GetBeatsAsync(
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		throw new NotImplementedException("CoreBluetooth wiring lands in Phase 2.");
#pragma warning disable CS0162
		yield break;
#pragma warning restore CS0162
	}
}
