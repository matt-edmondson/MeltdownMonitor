using System.Runtime.CompilerServices;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Beats.Polar;
using MeltdownMonitor.Core.Hrv;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace MeltdownMonitor.Ble.Windows;

/// <summary>
/// Connects to a heart-rate sensor via WinRT BLE and streams beats. Works with any
/// device exposing the standard Heart Rate Measurement characteristic with RR
/// intervals — Polar H10 / Verity Sense and Garmin HRM-Dual / HRM-Pro chest straps;
/// set <see cref="HeartRateDeviceType.Auto"/> to connect to whichever is found first.
/// Reconnects automatically with exponential backoff on disconnect.
/// </summary>
public sealed class BleHrSource : IBeatSource, IBatterySource, IContactSource, IDeviceInfoSource, IMotionSource, IEcgSource, IBeatDiagnosticsSource, IDisposable
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

	// Latch the most recent one-shot reads so a subscriber that wires up after they fired
	// still converges (the pipeline replays these on wiring). Null until the first read.
	private BatteryReading? _latestBattery;
	private DeviceInformation? _latestDeviceInfo;

	/// <inheritdoc />
	public event Action<BatteryReading>? BatteryLevelChanged;

	/// <inheritdoc />
	public event Action<SensorContactStatus>? SensorContactChanged;

	/// <inheritdoc />
	public event Action<DeviceInformation>? DeviceInformationChanged;

	/// <inheritdoc />
	public BatteryReading? LatestBattery => _latestBattery;

	/// <inheritdoc />
	public DeviceInformation? LatestDeviceInfo => _latestDeviceInfo;

	/// <inheritdoc />
	public event Action<MotionSample>? MotionSampleReceived;

	/// <inheritdoc />
	public event Action<EcgSamples>? EcgSamplesReceived;

	/// <inheritdoc />
	public event Action<BeatDiagnostic>? BeatDiagnosticReceived;

	private const double EcgSampleRateHz = 130.0;

	private readonly HeartRateDeviceType _deviceType;
	private readonly bool _enableMotion;
	private readonly IntervalSource _intervalSource;
	private readonly EcgRPeakDetector _rpeak = new();
	private readonly RrArtifactFilter _artifactFilter = new();

	// HRS RR is also reported as debug diagnostics through this private filter, kept separate from
	// the pipeline's _artifactFilter so reporting HRS while a Polar stream drives HRV never perturbs it.
	private readonly RrArtifactFilter _hrsDiagFilter = new();

	// Once the preferred Polar interval stream (PPI/ECG) is actually producing intervals, HRS beats
	// are suppressed so the two sources can't double-count. Until then HRS keeps the pipeline fed.
	private bool _polarIntervalsActive;
	private System.Threading.Channels.ChannelWriter<Beat>? _beatWriter;

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

	/// <param name="deviceType">Which sensor to connect to (or <see cref="HeartRateDeviceType.Auto"/>).</param>
	/// <param name="enableMotion">
	/// When true, also negotiate the Polar PMD accelerometer stream and raise <see cref="MotionSampleReceived"/>
	/// (no-op on non-Polar sensors, which have no PMD service). Off by default — opt-in via settings.
	/// </param>
	/// <param name="intervalSource">
	/// Which stream supplies the beat-to-beat intervals. <see cref="IntervalSource.HeartRateService"/>
	/// (default) uses standard HRS RR. <see cref="IntervalSource.PolarPpi"/> / <see cref="IntervalSource.PolarEcg"/>
	/// switch to the Polar PMD stream once it produces intervals, suppressing HRS to avoid double-counting;
	/// on a device that doesn't offer it, HRS simply keeps flowing.
	/// </param>
	public BleHrSource(
		HeartRateDeviceType deviceType = HeartRateDeviceType.Auto,
		bool enableMotion = false,
		IntervalSource intervalSource = IntervalSource.HeartRateService)
	{
		_deviceType = deviceType;
		_enableMotion = enableMotion;
		_intervalSource = intervalSource;
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
		string? namePrefix = DeviceNamePrefix.For(_deviceType);

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
		_hrsDiagFilter.Reset();
		_rpeak.Reset();
		_polarIntervalsActive = false;

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

		// Not single-writer: the HRS notification and the PMD data callback can both write briefly
		// around the handover to a Polar interval source.
		var channel = System.Threading.Channels.Channel.CreateUnbounded<Beat>(
			new System.Threading.Channels.UnboundedChannelOptions { SingleWriter = false });
		_beatWriter = channel.Writer;

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

			// Report every HRS RR for the debug A/B (even while suppressed below), via a private filter
			// so it never perturbs the pipeline's _artifactFilter that a live Polar stream may be driving.
			foreach (double diagRr in measurement.RrIntervals)
			{
				BeatDiagnosticReceived?.Invoke(new BeatDiagnostic(
					now, IntervalSource.HeartRateService, diagRr, measurement.HeartRateBpm, _hrsDiagFilter.IsArtifact(diagRr)));
			}

			// Once a preferred Polar interval stream is live, HRS RR is the redundant source — drop it.
			if (_polarIntervalsActive)
			{
				return;
			}

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
		if (_enableMotion || _intervalSource != IntervalSource.HeartRateService)
		{
			await TrySubscribePmdAsync(_device).ConfigureAwait(false);
		}

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

	// Negotiates the Polar PMD accelerometer stream: enable indications on the control point and
	// notifications on the data characteristic, confirm ACC is in the feature bitmask, then write
	// the start command. Decoded samples are converted to g and raised as motion. Entirely
	// best-effort — a non-Polar sensor has no PMD service, and any GATT error just means no motion.
	private async Task TrySubscribePmdAsync(BluetoothLEDevice device)
	{
		try
		{
			var serviceResult = await device.GetGattServicesForUuidAsync(PmdConstants.ServiceUuid);
			if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
			{
				return;
			}

			var service = serviceResult.Services[0];
			var controlPoint = await GetCharacteristicAsync(service, PmdConstants.ControlPointUuid);
			var data = await GetCharacteristicAsync(service, PmdConstants.DataUuid);
			if (controlPoint is null || data is null)
			{
				return;
			}

			data.ValueChanged += OnPmdDataChanged;
			if (await data.WriteClientCharacteristicConfigurationDescriptorAsync(
					GattClientCharacteristicConfigurationDescriptorValue.Notify) != GattCommunicationStatus.Success)
			{
				data.ValueChanged -= OnPmdDataChanged;
				return;
			}

			// The control point indicates command responses; enable it so the start writes complete.
			await controlPoint.WriteClientCharacteristicConfigurationDescriptorAsync(
				GattClientCharacteristicConfigurationDescriptorValue.Indicate);

			var featureRead = await controlPoint.ReadValueAsync();
			var supported = featureRead.Status == GattCommunicationStatus.Success
				? PmdControlPoint.ParseSupportedFeatures(ToBytes(featureRead.Value))
				: null;

			// Start each desired measurement the device actually supports (an empty feature read =
			// attempt anyway; unsupported starts just yield no frames, leaving HRS as the source).
			if (_enableMotion && Supports(supported, PmdMeasurementType.Acc))
			{
				await WriteControlPointAsync(controlPoint, PmdControlPoint.BuildStartAcc());
			}

			if (_intervalSource == IntervalSource.PolarPpi && Supports(supported, PmdMeasurementType.Ppi))
			{
				await WriteControlPointAsync(controlPoint, PmdControlPoint.BuildStartPpi());
			}
			else if (_intervalSource == IntervalSource.PolarEcg && Supports(supported, PmdMeasurementType.Ecg))
			{
				await WriteControlPointAsync(controlPoint, PmdControlPoint.BuildStartEcg());
			}
		}
		catch
		{
			// PMD streaming must never interrupt the beat stream.
		}
	}

	private static bool Supports(IReadOnlySet<PmdMeasurementType>? supported, PmdMeasurementType type) =>
		supported is null || supported.Count == 0 || supported.Contains(type);

	private void OnPmdDataChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
	{
		byte[] bytes = ToBytes(args.CharacteristicValue);
		if (bytes.Length < 10)
		{
			return;
		}

		var now = DateTimeOffset.UtcNow;
		switch ((PmdMeasurementType)bytes[0])
		{
			case PmdMeasurementType.Acc:
				foreach (PmdAccSample acc in PmdFrameParser.ParseAcc(bytes))
				{
					MotionSampleReceived?.Invoke(PolarMotion.ToMotionSample(acc, now));
				}

				break;

			case PmdMeasurementType.Ppi:
				foreach (PmdPpiSample ppi in PmdFrameParser.ParsePpi(bytes))
				{
					bool timingArtifact = _artifactFilter.IsArtifact(ppi.PpiMs);
					_polarIntervalsActive = true;
					var ppiBeat = PolarPpi.ToBeat(ppi, now, timingArtifact);
					BeatDiagnosticReceived?.Invoke(new BeatDiagnostic(
						now, IntervalSource.PolarPpi, ppiBeat.RrMs, ppiBeat.HeartRateBpm, ppiBeat.IsArtifact));
					_beatWriter?.TryWrite(ppiBeat);
				}

				break;

			case PmdMeasurementType.Ecg:
			{
				var ecgSamples = PmdFrameParser.ParseEcg(bytes);
				int count = ecgSamples.Count;
				// The frame header carries the device time (since the PMD epoch) of the LAST sample;
				// samples are evenly spaced at the ECG rate. Feeding each sample's true device time lets
				// the detector treat a dropped frame as real elapsed time rather than miscounting RR.
				double frameEndSeconds = (PmdFrameParser.ParseHeader(bytes).Timestamp - PmdConstants.Epoch).TotalSeconds;
				var microVolts = new int[count];
				var peaks = new List<int>();
				for (int i = 0; i < count; i++)
				{
					microVolts[i] = ecgSamples[i].MicroVolts;
					double sampleSeconds = frameEndSeconds - ((count - 1 - i) / EcgSampleRateHz);
					double? rrMs = _rpeak.AddSample(microVolts[i], sampleSeconds);
					if (_rpeak.LastSampleWasRPeak)
					{
						peaks.Add(i);
					}

					if (rrMs is { } rr)
					{
						bool isArtifact = _artifactFilter.IsArtifact(rr);
						_polarIntervalsActive = true;
						int bpm = (int)Math.Round(60000.0 / rr);
						BeatDiagnosticReceived?.Invoke(new BeatDiagnostic(now, IntervalSource.PolarEcg, rr, bpm, isArtifact));
						_beatWriter?.TryWrite(new Beat(now, rr, bpm, isArtifact));
					}
				}

				EcgSamplesReceived?.Invoke(new EcgSamples(now, microVolts, EcgSampleRateHz, peaks));
				break;
			}
		}
	}

	private static async Task<GattCharacteristic?> GetCharacteristicAsync(GattDeviceService service, Guid uuid)
	{
		var result = await service.GetCharacteristicsForUuidAsync(uuid);
		return result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0
			? result.Characteristics[0]
			: null;
	}

	private static async Task WriteControlPointAsync(GattCharacteristic controlPoint, byte[] command)
	{
		var writer = new global::Windows.Storage.Streams.DataWriter();
		writer.WriteBytes(command);
		await controlPoint.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse);
	}

	private static byte[] ToBytes(global::Windows.Storage.Streams.IBuffer buffer)
	{
		var reader = global::Windows.Storage.Streams.DataReader.FromBuffer(buffer);
		var bytes = new byte[buffer.Length];
		reader.ReadBytes(bytes);
		return bytes;
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
		var reading = new BatteryReading(DateTimeOffset.UtcNow, percent);
		_latestBattery = reading;
		BatteryLevelChanged?.Invoke(reading);
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

			_latestDeviceInfo = info;
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
