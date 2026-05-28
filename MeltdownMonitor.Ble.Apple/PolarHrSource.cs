using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CoreBluetooth;
using CoreFoundation;
using Foundation;
using MeltdownMonitor.Core.Beats;

namespace MeltdownMonitor.Ble.Apple;

/// <summary>
/// CoreBluetooth-backed Polar HR source. Scans for the Heart Rate Service
/// (<c>0x180D</c>), subscribes to the Heart Rate Measurement characteristic
/// (<c>0x2A37</c>), parses payloads with the shared <see cref="HrMeasurementParser"/>,
/// rejects outliers with the shared <see cref="RrArtifactFilter"/>, and surfaces
/// beats as an <see cref="IAsyncEnumerable{Beat}"/>.
///
/// Background-safe per design doc §4.1: scans always specify the service UUID,
/// the central manager is created with a restore identifier, and
/// <see cref="WillRestoreState"/> rehydrates the connected peripheral after
/// iOS relaunches the app.
/// </summary>
public sealed class PolarHrSource : CBCentralManagerDelegate, IBeatSource
{
	public const string DefaultRestoreIdentifier = "com.thethreethousands.meltdownmonitor.central";

	private static readonly CBUUID HeartRateServiceUuid = CBUUID.FromString("180D");
	private static readonly CBUUID HrMeasurementCharUuid = CBUUID.FromString("2A37");

	/// <summary>
	/// BLE advertisement name prefixes for each known device type.
	/// Matching is case-insensitive substring search within the local name.
	/// </summary>
	private static readonly IReadOnlyDictionary<PolarDeviceType, string> DeviceNamePrefixes =
		new Dictionary<PolarDeviceType, string>
		{
			[PolarDeviceType.H10] = "Polar H10",
			[PolarDeviceType.VeritySense] = "Polar Sense",
		};

	private readonly PolarDeviceType _deviceType;
	private readonly BleStateRestoration _restoration;
	private readonly RrArtifactFilter _artifactFilter = new();
	private readonly Channel<Beat> _channel = Channel.CreateUnbounded<Beat>(
		new UnboundedChannelOptions { SingleWriter = true });
	private readonly CBCentralManager _central;

	private PeripheralObserver? _peripheralObserver;
	private CBPeripheral? _peripheral;

	public PolarHrSource(
		PolarDeviceType deviceType = PolarDeviceType.Auto,
		BleStateRestoration? restoration = null,
		string restoreIdentifier = DefaultRestoreIdentifier)
	{
		_deviceType = deviceType;
		_restoration = restoration ?? new BleStateRestoration();

		var options = new CBCentralInitOptions
		{
			RestoreIdentifier = restoreIdentifier,
		};

		_central = new CBCentralManager(this, DispatchQueue.MainQueue, options);
	}

	internal CBUUID ServiceUuid => HeartRateServiceUuid;
	internal CBUUID CharacteristicUuid => HrMeasurementCharUuid;

	public async IAsyncEnumerable<Beat> GetBeatsAsync(
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await foreach (var beat in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
		{
			yield return beat;
		}
	}

	public override void UpdatedState(CBCentralManager central)
	{
		if (central.State != CBManagerState.PoweredOn)
		{
			return;
		}

		// Prefer reconnecting to a previously known peripheral if we have one;
		// avoids the user-facing scan delay on every launch (§4.1).
		Guid? known = _restoration.LoadPeripheralIdentifier();
		if (known is not null)
		{
			var uuid = new NSUuid(known.Value.ToString());
			var matches = central.RetrievePeripheralsWithIdentifiers(new[] { uuid });
			if (matches.Length > 0)
			{
				AttachPeripheral(matches[0]);
				central.ConnectPeripheral(matches[0]);
				return;
			}
		}

		// Background-safe scan must specify the service UUID — wildcard scans
		// return nothing once the app is suspended (design doc §4.1).
		central.ScanForPeripherals(new[] { HeartRateServiceUuid });
	}

	public override void DiscoveredPeripheral(
		CBCentralManager central,
		CBPeripheral peripheral,
		NSDictionary advertisementData,
		NSNumber RSSI)
	{
		if (DeviceNamePrefixes.TryGetValue(_deviceType, out string? prefix))
		{
			string name = peripheral.Name ?? string.Empty;
			if (!name.Contains(prefix, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
		}

		central.StopScan();
		AttachPeripheral(peripheral);
		central.ConnectPeripheral(peripheral);
	}

	public override void ConnectedPeripheral(CBCentralManager central, CBPeripheral peripheral)
	{
		if (Guid.TryParse(peripheral.Identifier.AsString(), out var id))
		{
			_restoration.SavePeripheralIdentifier(id);
		}

		_artifactFilter.Reset();
		peripheral.DiscoverServices(new[] { HeartRateServiceUuid });
	}

	public override void DisconnectedPeripheral(
		CBCentralManager central,
		CBPeripheral peripheral,
		NSError? error)
	{
		// iOS will fulfill this asynchronously when the device returns to range,
		// so a plain reconnect request is the right primitive here.
		central.ConnectPeripheral(peripheral);
	}

	public override void FailedToConnectPeripheral(
		CBCentralManager central,
		CBPeripheral peripheral,
		NSError? error)
	{
		central.ScanForPeripherals(new[] { HeartRateServiceUuid });
	}

	public override void WillRestoreState(CBCentralManager central, NSDictionary dict)
	{
		var key = CBCentralManager.RestoredStatePeripheralsKey;
		if (!dict.ContainsKey(key))
		{
			return;
		}

		if (dict[key] is not NSArray restored || restored.Count == 0)
		{
			return;
		}

		var peripheral = restored.GetItem<CBPeripheral>(0);
		AttachPeripheral(peripheral);

		if (peripheral.State == CBPeripheralState.Connected)
		{
			peripheral.DiscoverServices(new[] { HeartRateServiceUuid });
		}
		else
		{
			central.ConnectPeripheral(peripheral);
		}
	}

	private void AttachPeripheral(CBPeripheral peripheral)
	{
		_peripheralObserver?.Detach();
		_peripheralObserver = new PeripheralObserver(this);
		_peripheral = peripheral;
		peripheral.Delegate = _peripheralObserver;
	}

	internal void OnMeasurementBytes(byte[] payload)
	{
		var measurement = HrMeasurementParser.Parse(payload);
		var now = DateTimeOffset.UtcNow;

		foreach (double rrMs in measurement.RrIntervals)
		{
			bool isArtifact = _artifactFilter.IsArtifact(rrMs);
			var beat = new Beat(now, rrMs, measurement.HeartRateBpm, isArtifact);
			_channel.Writer.TryWrite(beat);
		}
	}

	private sealed class PeripheralObserver : CBPeripheralDelegate
	{
		private PolarHrSource? _owner;

		public PeripheralObserver(PolarHrSource owner) => _owner = owner;

		public void Detach() => _owner = null;

		public override void DiscoveredService(CBPeripheral peripheral, NSError? error)
		{
			if (_owner is null || peripheral.Services is null)
			{
				return;
			}

			foreach (var service in peripheral.Services)
			{
				if (service.UUID.Equals(_owner.ServiceUuid))
				{
					peripheral.DiscoverCharacteristics(new[] { _owner.CharacteristicUuid }, service);
				}
			}
		}

		public override void DiscoveredCharacteristics(CBPeripheral peripheral, CBService service, NSError? error)
		{
			if (_owner is null || service.Characteristics is null)
			{
				return;
			}

			foreach (var characteristic in service.Characteristics)
			{
				if (characteristic.UUID.Equals(_owner.CharacteristicUuid))
				{
					peripheral.SetNotifyValue(true, characteristic);
				}
			}
		}

		public override void UpdatedCharacterteristicValue(
			CBPeripheral peripheral,
			CBCharacteristic characteristic,
			NSError? error)
		{
			if (_owner is null || error is not null)
			{
				return;
			}

			if (!characteristic.UUID.Equals(_owner.CharacteristicUuid))
			{
				return;
			}

			NSData? value = characteristic.Value;
			if (value is null || value.Length == 0)
			{
				return;
			}

			byte[] bytes = new byte[value.Length];
			System.Runtime.InteropServices.Marshal.Copy(value.Bytes, bytes, 0, bytes.Length);
			_owner.OnMeasurementBytes(bytes);
		}
	}
}
