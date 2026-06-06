using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CoreBluetooth;
using CoreFoundation;
using Foundation;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Beats.Polar;

namespace MeltdownMonitor.Ble.Apple;

/// <summary>
/// CoreBluetooth-backed heart-rate source. Works with any device exposing RR
/// intervals over the standard HR service — Polar H10 / Verity Sense and Garmin
/// HRM-Dual / HRM-Pro chest straps. Scans for the Heart Rate Service
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
public sealed class BleHrSource : CBCentralManagerDelegate, IBeatSource, IBatterySource, IContactSource, IDeviceInfoSource, IMotionSource
{
	public const string DefaultRestoreIdentifier = "com.matthewedmondson.meltdownmonitor.central";

	private static readonly CBUUID HeartRateServiceUuid = CBUUID.FromString("180D");
	private static readonly CBUUID HrMeasurementCharUuid = CBUUID.FromString("2A37");

	// Polar PMD (proprietary) service + control-point/data characteristics for the accelerometer stream.
	private static readonly CBUUID PmdServiceUuid = CBUUID.FromString(PmdConstants.ServiceUuid.ToString());
	private static readonly CBUUID PmdControlPointUuid = CBUUID.FromString(PmdConstants.ControlPointUuid.ToString());
	private static readonly CBUUID PmdDataUuid = CBUUID.FromString(PmdConstants.DataUuid.ToString());

	// Standard GATT Battery Service / Battery Level characteristic.
	private static readonly CBUUID BatteryServiceUuid = CBUUID.FromString("180F");
	private static readonly CBUUID BatteryLevelCharUuid = CBUUID.FromString("2A19");

	// Standard GATT Device Information Service and its string characteristics.
	private static readonly CBUUID DeviceInfoServiceUuid = CBUUID.FromString("180A");
	private static readonly CBUUID ManufacturerNameCharUuid = CBUUID.FromString("2A29");
	private static readonly CBUUID ModelNumberCharUuid = CBUUID.FromString("2A24");
	private static readonly CBUUID SerialNumberCharUuid = CBUUID.FromString("2A25");
	private static readonly CBUUID FirmwareRevisionCharUuid = CBUUID.FromString("2A26");
	private static readonly CBUUID HardwareRevisionCharUuid = CBUUID.FromString("2A27");
	private static readonly CBUUID SoftwareRevisionCharUuid = CBUUID.FromString("2A28");

	private readonly CBUUID[] _deviceInfoCharacteristics =
	[
		ManufacturerNameCharUuid, ModelNumberCharUuid, SerialNumberCharUuid,
		FirmwareRevisionCharUuid, HardwareRevisionCharUuid, SoftwareRevisionCharUuid,
	];

	// Accumulated as the individual DIS characteristics are read back one by one.
	private DeviceInformation _deviceInfo = new();

	/// <inheritdoc />
	public event Action<BatteryReading>? BatteryLevelChanged;

	/// <inheritdoc />
	public event Action<SensorContactStatus>? SensorContactChanged;

	/// <inheritdoc />
	public event Action<DeviceInformation>? DeviceInformationChanged;

	/// <inheritdoc />
	public event Action<MotionSample>? MotionSampleReceived;

	private readonly HeartRateDeviceType _deviceType;
	private readonly bool _enableMotion;
	private readonly CBUUID[] _servicesToDiscover;
	private readonly BleStateRestoration _restoration;
	private readonly RrArtifactFilter _artifactFilter = new();
	private readonly Channel<Beat> _channel = Channel.CreateUnbounded<Beat>(
		new UnboundedChannelOptions { SingleWriter = true });
	private readonly CBCentralManager _central;

	private PeripheralObserver? _peripheralObserver;
	private CBPeripheral? _peripheral;

	public BleHrSource(
		HeartRateDeviceType deviceType = HeartRateDeviceType.Auto,
		BleStateRestoration? restoration = null,
		string restoreIdentifier = DefaultRestoreIdentifier,
		bool enableMotion = false)
	{
		_deviceType = deviceType;
		_enableMotion = enableMotion;
		_servicesToDiscover = enableMotion
			? [HeartRateServiceUuid, BatteryServiceUuid, DeviceInfoServiceUuid, PmdServiceUuid]
			: [HeartRateServiceUuid, BatteryServiceUuid, DeviceInfoServiceUuid];
		_restoration = restoration ?? new BleStateRestoration();

		var options = new CBCentralInitOptions
		{
			RestoreIdentifier = restoreIdentifier,
		};

		_central = new CBCentralManager(this, DispatchQueue.MainQueue, options);
	}

	internal CBUUID ServiceUuid => HeartRateServiceUuid;
	internal CBUUID CharacteristicUuid => HrMeasurementCharUuid;
	internal CBUUID BatteryService => BatteryServiceUuid;
	internal CBUUID BatteryCharacteristic => BatteryLevelCharUuid;
	internal CBUUID DeviceInfoService => DeviceInfoServiceUuid;
	internal CBUUID[] DeviceInfoCharacteristics => _deviceInfoCharacteristics;

	internal bool IsDeviceInfoCharacteristic(CBUUID uuid) =>
		Array.Exists(_deviceInfoCharacteristics, u => u.Equals(uuid));

	internal bool EnableMotion => _enableMotion;
	internal CBUUID PmdService => PmdServiceUuid;
	internal CBUUID PmdControlPoint => PmdControlPointUuid;
	internal CBUUID PmdData => PmdDataUuid;

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
		if (DeviceNamePrefix.For(_deviceType) is string prefix)
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
		peripheral.DiscoverServices(_servicesToDiscover);
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
			peripheral.DiscoverServices(_servicesToDiscover);
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

		// Surface contact on every notification — it's the only signal that
		// survives contact loss, when RR intervals (and beats) dry up.
		SensorContactChanged?.Invoke(measurement.SensorContact);

		foreach (double rrMs in measurement.RrIntervals)
		{
			bool isArtifact = _artifactFilter.IsArtifact(rrMs);
			var beat = new Beat(now, rrMs, measurement.HeartRateBpm, isArtifact);
			_channel.Writer.TryWrite(beat);
		}
	}

	// Decode a PMD data frame; only ACC frames produce motion samples.
	internal void OnPmdData(byte[] bytes)
	{
		if (bytes.Length < 10 || (PmdMeasurementType)bytes[0] != PmdMeasurementType.Acc)
		{
			return;
		}

		var now = DateTimeOffset.UtcNow;
		foreach (PmdAccSample acc in PmdFrameParser.ParseAcc(bytes))
		{
			MotionSampleReceived?.Invoke(PolarMotion.ToMotionSample(acc, now));
		}
	}

	// Battery Level is a single uint8 percentage (0–100).
	internal void OnBatteryByte(byte percent)
		=> BatteryLevelChanged?.Invoke(new BatteryReading(DateTimeOffset.UtcNow, Math.Clamp((int)percent, 0, 100)));

	// DIS characteristics are read back one at a time; fold each into the record
	// and re-emit so subscribers converge on the full identity as fields arrive.
	internal void OnDeviceInfoCharacteristic(CBUUID uuid, byte[] bytes)
	{
		// DIS string characteristics are UTF-8; trim any trailing NUL padding.
		string value = System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
		if (value.Length == 0)
		{
			return;
		}

		_deviceInfo =
			uuid.Equals(ManufacturerNameCharUuid) ? _deviceInfo with { ManufacturerName = value } :
			uuid.Equals(ModelNumberCharUuid)      ? _deviceInfo with { ModelNumber = value } :
			uuid.Equals(SerialNumberCharUuid)     ? _deviceInfo with { SerialNumber = value } :
			uuid.Equals(FirmwareRevisionCharUuid) ? _deviceInfo with { FirmwareRevision = value } :
			uuid.Equals(HardwareRevisionCharUuid) ? _deviceInfo with { HardwareRevision = value } :
			uuid.Equals(SoftwareRevisionCharUuid) ? _deviceInfo with { SoftwareRevision = value } :
			_deviceInfo;

		DeviceInformationChanged?.Invoke(_deviceInfo);
	}

	private sealed class PeripheralObserver : CBPeripheralDelegate
	{
		private BleHrSource? _owner;

		public PeripheralObserver(BleHrSource owner) => _owner = owner;

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
				else if (service.UUID.Equals(_owner.BatteryService))
				{
					peripheral.DiscoverCharacteristics(new[] { _owner.BatteryCharacteristic }, service);
				}
				else if (service.UUID.Equals(_owner.DeviceInfoService))
				{
					peripheral.DiscoverCharacteristics(_owner.DeviceInfoCharacteristics, service);
				}
				else if (_owner.EnableMotion && service.UUID.Equals(_owner.PmdService))
				{
					peripheral.DiscoverCharacteristics(new[] { _owner.PmdControlPoint, _owner.PmdData }, service);
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
				else if (characteristic.UUID.Equals(_owner.BatteryCharacteristic))
				{
					// One immediate read, plus notifications for later changes.
					peripheral.ReadValue(characteristic);
					peripheral.SetNotifyValue(true, characteristic);
				}
				else if (_owner.IsDeviceInfoCharacteristic(characteristic.UUID))
				{
					// Static identity — a single read, no notifications.
					peripheral.ReadValue(characteristic);
				}
				else if (_owner.EnableMotion && characteristic.UUID.Equals(_owner.PmdData))
				{
					peripheral.SetNotifyValue(true, characteristic);
				}
				else if (_owner.EnableMotion && characteristic.UUID.Equals(_owner.PmdControlPoint))
				{
					// Enable indications, then ask the device to start streaming the accelerometer.
					peripheral.SetNotifyValue(true, characteristic);
					peripheral.WriteValue(NSData.FromArray(PmdControlPoint.BuildStartAcc()), characteristic,
						CBCharacteristicWriteType.WithResponse);
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

			bool isHr = characteristic.UUID.Equals(_owner.CharacteristicUuid);
			bool isBattery = characteristic.UUID.Equals(_owner.BatteryCharacteristic);
			bool isDeviceInfo = _owner.IsDeviceInfoCharacteristic(characteristic.UUID);
			bool isPmdData = _owner.EnableMotion && characteristic.UUID.Equals(_owner.PmdData);
			if (!isHr && !isBattery && !isDeviceInfo && !isPmdData)
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

			if (isHr)
			{
				_owner.OnMeasurementBytes(bytes);
			}
			else if (isBattery)
			{
				_owner.OnBatteryByte(bytes[0]);
			}
			else if (isPmdData)
			{
				_owner.OnPmdData(bytes);
			}
			else
			{
				_owner.OnDeviceInfoCharacteristic(characteristic.UUID, bytes);
			}
		}
	}
}
