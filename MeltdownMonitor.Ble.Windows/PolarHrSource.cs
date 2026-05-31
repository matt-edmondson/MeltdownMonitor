using System.Runtime.CompilerServices;
using MeltdownMonitor.Core.Beats;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace MeltdownMonitor.Ble.Windows;

/// <summary>
/// Connects to a Polar HR sensor via WinRT BLE and streams beats.
/// Supports the H10 chest strap and the Verity Sense optical armband;
/// set <see cref="PolarDeviceType.Auto"/> to connect to whichever is found first.
/// Reconnects automatically with exponential backoff on disconnect.
/// </summary>
public sealed class PolarHrSource : IBeatSource, IBatterySource, IContactSource, IDeviceInfoSource, IDisposable
{
	private static readonly Guid HeartRateServiceUuid = new("0000180d-0000-1000-8000-00805f9b34fb");
	private static readonly Guid HrMeasurementCharUuid = new("00002a37-0000-1000-8000-00805f9b34fb");

	// Standard GATT Battery Service / Battery Level characteristic.
	private static readonly Guid BatteryServiceUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
	private static readonly Guid BatteryLevelCharUuid = new("00002a19-0000-1000-8000-00805f9b34fb");

	// Standard GATT Device Information Service and its string characteristics.
	private static readonly Guid DeviceInfoServiceUuid = new("0000180a-0000-1000-8000-00805f9b34fb");
	private static readonly Guid ManufacturerNameCharUuid = new("00002a29-0000-1000-8000-00805f9b34fb");
	private static readonly Guid ModelNumberCharUuid = new("00002a24-0000-1000-8000-00805f9b34fb");
	private static readonly Guid SerialNumberCharUuid = new("00002a25-0000-1000-8000-00805f9b34fb");
	private static readonly Guid FirmwareRevisionCharUuid = new("00002a26-0000-1000-8000-00805f9b34fb");
	private static readonly Guid HardwareRevisionCharUuid = new("00002a27-0000-1000-8000-00805f9b34fb");
	private static readonly Guid SoftwareRevisionCharUuid = new("00002a28-0000-1000-8000-00805f9b34fb");

	/// <inheritdoc />
	public event Action<BatteryReading>? BatteryLevelChanged;

	/// <inheritdoc />
	public event Action<SensorContactStatus>? SensorContactChanged;

	/// <inheritdoc />
	public event Action<DeviceInformation>? DeviceInformationChanged;

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
	private readonly RrArtifactFilter _artifactFilter = new();

	private BluetoothLEDevice? _device;
	private bool _disposed;

	private static readonly TimeSpan[] RetryDelays =
	[
		TimeSpan.FromSeconds(1),
		TimeSpan.FromSeconds(2),
		TimeSpan.FromSeconds(5),
		TimeSpan.FromSeconds(10),
		TimeSpan.FromSeconds(30),
	];

	public PolarHrSource(PolarDeviceType deviceType = PolarDeviceType.Auto)
	{
		_deviceType = deviceType;
	}

	public async IAsyncEnumerable<Beat> GetBeatsAsync(
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		int retryIndex = 0;

		while (!cancellationToken.IsCancellationRequested)
		{
			ulong? address = await ScanForDeviceAsync(cancellationToken).ConfigureAwait(false);
			if (address is null)
			{
				continue;
			}

			await foreach (var beat in StreamBeatsAsync(address.Value, cancellationToken))
			{
				retryIndex = 0;
				yield return beat;
			}

			var delay = RetryDelays[Math.Min(retryIndex, RetryDelays.Length - 1)];
			retryIndex++;
			await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task<ulong?> ScanForDeviceAsync(CancellationToken cancellationToken)
	{
		var tcs = new TaskCompletionSource<ulong>();
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		// Resolve the name prefix for the selected device type (null = accept any HRS device)
		DeviceNamePrefixes.TryGetValue(_deviceType, out string? namePrefix);

		var watcher = new BluetoothLEAdvertisementWatcher();
		watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(HeartRateServiceUuid);

		watcher.Received += (_, args) =>
		{
			if (namePrefix is not null)
			{
				string localName = args.Advertisement.LocalName ?? string.Empty;
				if (!localName.Contains(namePrefix, StringComparison.OrdinalIgnoreCase))
				{
					return;
				}
			}

			tcs.TrySetResult(args.BluetoothAddress);
			cts.Cancel();
		};

		watcher.Start();

		try
		{
			return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return cancellationToken.IsCancellationRequested ? null : tcs.Task.Result;
		}
		finally
		{
			watcher.Stop();
		}
	}

	private async IAsyncEnumerable<Beat> StreamBeatsAsync(
		ulong address,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		// Start each connection with a clean artifact-filter window so RR intervals
		// from before a disconnect can't mis-flag the first beats after a reconnect
		// (matches the Apple source, which resets on every connect).
		_artifactFilter.Reset();

		_device?.Dispose();
		_device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);

		if (_device is null)
		{
			yield break;
		}

		var serviceResult = await _device.GetGattServicesForUuidAsync(HeartRateServiceUuid);
		if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
		{
			yield break;
		}

		using var service = serviceResult.Services[0];
		var charResult = await service.GetCharacteristicsForUuidAsync(HrMeasurementCharUuid);
		if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
		{
			yield break;
		}

		var characteristic = charResult.Characteristics[0];

		var channel = System.Threading.Channels.Channel.CreateUnbounded<Beat>(
			new System.Threading.Channels.UnboundedChannelOptions { SingleWriter = true });

		void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
		{
			var reader = global::Windows.Storage.Streams.DataReader.FromBuffer(args.CharacteristicValue);
			var bytes = new byte[args.CharacteristicValue.Length];
			reader.ReadBytes(bytes);

			var measurement = HrMeasurementParser.Parse(bytes);
			var now = DateTimeOffset.UtcNow;

			// Surface contact on every notification — it's the only signal that
			// survives contact loss, when RR intervals (and beats) dry up.
			SensorContactChanged?.Invoke(measurement.SensorContact);

			foreach (double rrMs in measurement.RrIntervals)
			{
				bool isArtifact = _artifactFilter.IsArtifact(rrMs);
				var beat = new Beat(now, rrMs, measurement.HeartRateBpm, isArtifact);
				channel.Writer.TryWrite(beat);
			}
		}

		characteristic.ValueChanged += OnValueChanged;
		var writeStatus = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
			GattClientCharacteristicConfigurationDescriptorValue.Notify);

		if (writeStatus != GattCommunicationStatus.Success)
		{
			characteristic.ValueChanged -= OnValueChanged;
			yield break;
		}

		_device.ConnectionStatusChanged += (dev, _) =>
		{
			if (dev.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
			{
				channel.Writer.TryComplete();
			}
		};

		// Best-effort: surface the battery level and device identity without ever
		// blocking the beat stream.
		await TrySubscribeBatteryAsync(_device).ConfigureAwait(false);
		await TryReadDeviceInfoAsync(_device).ConfigureAwait(false);

		try
		{
			await foreach (var beat in channel.Reader.ReadAllAsync(cancellationToken))
			{
				yield return beat;
			}
		}
		finally
		{
			characteristic.ValueChanged -= OnValueChanged;
			await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
				GattClientCharacteristicConfigurationDescriptorValue.None);
		}
	}

	// Reads the Battery Level characteristic once and, if the device supports it,
	// subscribes to change notifications. Entirely best-effort: a device without a
	// Battery Service (or a transient GATT error) just means no battery readings.
	private async Task TrySubscribeBatteryAsync(BluetoothLEDevice device)
	{
		try
		{
			var serviceResult = await device.GetGattServicesForUuidAsync(BatteryServiceUuid);
			if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
			{
				return;
			}

			var service = serviceResult.Services[0];
			var charResult = await service.GetCharacteristicsForUuidAsync(BatteryLevelCharUuid);
			if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
			{
				return;
			}

			var characteristic = charResult.Characteristics[0];

			var read = await characteristic.ReadValueAsync();
			if (read.Status == GattCommunicationStatus.Success)
			{
				EmitBattery(read.Value);
			}

			if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
			{
				characteristic.ValueChanged += (_, args) => EmitBattery(args.CharacteristicValue);
				await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
					GattClientCharacteristicConfigurationDescriptorValue.Notify);
			}
		}
		catch
		{
			// Battery monitoring must never interrupt the beat stream.
		}
	}

	private void EmitBattery(global::Windows.Storage.Streams.IBuffer buffer)
	{
		if (buffer.Length == 0)
		{
			return;
		}

		var reader = global::Windows.Storage.Streams.DataReader.FromBuffer(buffer);
		var bytes = new byte[buffer.Length];
		reader.ReadBytes(bytes);

		// Battery Level is a single uint8 percentage (0–100).
		int percent = Math.Clamp(bytes[0], (byte)0, (byte)100);
		BatteryLevelChanged?.Invoke(new BatteryReading(DateTimeOffset.UtcNow, percent));
	}

	// Reads the Device Information Service once on connect. Every characteristic is
	// optional, so missing ones simply stay null. Best-effort: a device without the
	// service (or a transient error) just means no identity is reported.
	private async Task TryReadDeviceInfoAsync(BluetoothLEDevice device)
	{
		try
		{
			var serviceResult = await device.GetGattServicesForUuidAsync(DeviceInfoServiceUuid);
			if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
			{
				return;
			}

			using var service = serviceResult.Services[0];

			var info = new DeviceInformation(
				ManufacturerName: await ReadStringAsync(service, ManufacturerNameCharUuid),
				ModelNumber: await ReadStringAsync(service, ModelNumberCharUuid),
				SerialNumber: await ReadStringAsync(service, SerialNumberCharUuid),
				FirmwareRevision: await ReadStringAsync(service, FirmwareRevisionCharUuid),
				HardwareRevision: await ReadStringAsync(service, HardwareRevisionCharUuid),
				SoftwareRevision: await ReadStringAsync(service, SoftwareRevisionCharUuid));

			DeviceInformationChanged?.Invoke(info);
		}
		catch
		{
			// Device-info reads must never interrupt the beat stream.
		}
	}

	private static async Task<string?> ReadStringAsync(GattDeviceService service, Guid characteristicUuid)
	{
		var charResult = await service.GetCharacteristicsForUuidAsync(characteristicUuid);
		if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
		{
			return null;
		}

		var read = await charResult.Characteristics[0].ReadValueAsync();
		if (read.Status != GattCommunicationStatus.Success || read.Value.Length == 0)
		{
			return null;
		}

		var reader = global::Windows.Storage.Streams.DataReader.FromBuffer(read.Value);
		var bytes = new byte[read.Value.Length];
		reader.ReadBytes(bytes);

		// DIS string characteristics are UTF-8; trim any trailing NUL padding.
		string value = System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
		return value.Length > 0 ? value : null;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_device?.Dispose();
	}
}
