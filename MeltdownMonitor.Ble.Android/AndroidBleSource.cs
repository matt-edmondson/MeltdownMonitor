using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using Java.Util;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Beats.Polar;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Ble.Android;

/// <summary>
/// Android BLE heart-rate source. Mirrors <c>MeltdownMonitor.Ble.Apple.BleHrSource</c>:
/// scans for the standard Heart Rate Service (<c>0x180D</c>), connects with
/// <c>autoConnect: true</c> so the stack transparently reconnects when the
/// sensor returns to range (design doc §5.1), subscribes to the Heart Rate
/// Measurement characteristic (<c>0x2A37</c>), and surfaces beats as an
/// <see cref="IAsyncEnumerable{Beat}"/>. Payload parsing and artifact rejection
/// reuse the shared <see cref="HrMeasurementParser"/> / <see cref="RrArtifactFilter"/>
/// from Core verbatim — the GATT bytes are identical on every platform.
///
/// Android's GATT stack permits only one outstanding operation at a time, so
/// service discovery, the CCCD write that enables notifications, and the
/// Device-Information reads are funnelled through a small serial queue
/// (<see cref="EnqueueGattOp"/>). This is the one place the Android source
/// carries more ceremony than the CoreBluetooth one, which hides the queue
/// internally — see design doc §7.
/// </summary>
public sealed class AndroidBleSource : IBeatSource, IBatterySource, IContactSource, IDeviceInfoSource, IMotionSource, IEcgSource, IBeatDiagnosticsSource, IDisposable
{
	// Standard 16-bit GATT UUIDs, expanded against the Bluetooth base UUID.
	private static readonly UUID HeartRateServiceUuid = ShortUuid(0x180D);
	private static readonly UUID HrMeasurementCharUuid = ShortUuid(0x2A37);
	private static readonly UUID BatteryServiceUuid = ShortUuid(0x180F);
	private static readonly UUID BatteryLevelCharUuid = ShortUuid(0x2A19);
	private static readonly UUID DeviceInfoServiceUuid = ShortUuid(0x180A);

	// Polar PMD (proprietary) service + control-point/data characteristics for the accelerometer.
	private static readonly UUID PmdServiceUuid = UUID.FromString(PmdConstants.ServiceUuid.ToString())!;
	private static readonly UUID PmdControlPointUuid = UUID.FromString(PmdConstants.ControlPointUuid.ToString())!;
	private static readonly UUID PmdDataUuid = UUID.FromString(PmdConstants.DataUuid.ToString())!;

	// Client Characteristic Configuration Descriptor — written explicitly to turn
	// on notifications (CoreBluetooth does this implicitly; Android does not).
	private static readonly UUID CccdUuid = ShortUuid(0x2902);

	private static readonly UUID ManufacturerNameCharUuid = ShortUuid(0x2A29);
	private static readonly UUID ModelNumberCharUuid = ShortUuid(0x2A24);
	private static readonly UUID SerialNumberCharUuid = ShortUuid(0x2A25);
	private static readonly UUID FirmwareRevisionCharUuid = ShortUuid(0x2A26);
	private static readonly UUID HardwareRevisionCharUuid = ShortUuid(0x2A27);
	private static readonly UUID SoftwareRevisionCharUuid = ShortUuid(0x2A28);

	private static readonly UUID[] DeviceInfoCharacteristics =
	[
		ManufacturerNameCharUuid, ModelNumberCharUuid, SerialNumberCharUuid,
		FirmwareRevisionCharUuid, HardwareRevisionCharUuid, SoftwareRevisionCharUuid,
	];

	/// <inheritdoc />
	public event Action<BatteryReading>? BatteryLevelChanged;

	/// <inheritdoc />
	public event Action<SensorContactStatus>? SensorContactChanged;

	/// <inheritdoc />
	public event Action<DeviceInformation>? DeviceInformationChanged;

	/// <inheritdoc />
	public event Action<MotionSample>? MotionSampleReceived;

	/// <inheritdoc />
	public event Action<EcgSamples>? EcgSamplesReceived;

	/// <inheritdoc />
	public event Action<BeatDiagnostic>? BeatDiagnosticReceived;

	/// <inheritdoc />
	public BatteryReading? LatestBattery => _latestBattery;

	/// <inheritdoc />
	public DeviceInformation? LatestDeviceInfo => _deviceInfoRead ? _deviceInfo : null;

	private const double EcgSampleRateHz = 130.0;

	private readonly Context _context;
	private readonly HeartRateDeviceType _deviceType;
	private readonly bool _enableMotion;
	private readonly IntervalSource _intervalSource;
	private readonly EcgRPeakDetector _rpeak = new();
	private readonly AndroidBleStateRestoration _restoration;

	// Once a preferred Polar interval stream (PPI/ECG) is producing intervals, HRS beats are
	// suppressed so the two sources can't double-count. Until then HRS keeps the pipeline fed.
	private bool _polarIntervalsActive;
	private bool PmdEnabled => _enableMotion || _intervalSource != IntervalSource.HeartRateService;
	private readonly RrArtifactFilter _artifactFilter = new();

	// HRS RR is also reported as debug diagnostics through this private filter, kept separate from
	// the pipeline's _artifactFilter so reporting HRS while a Polar stream drives HRV never perturbs it.
	private readonly RrArtifactFilter _hrsDiagFilter = new();

	// Not single-writer: the HRS notification (OnMeasurementBytes) and the PMD data callback (OnPmdData,
	// for PPI/ECG) can both write briefly around the handover to a Polar interval source. A single GATT's
	// callbacks are serialised on one binder thread today, but the channel must not rely on that — matches
	// the Windows head, which documents the same dual-writer handover.
	private readonly Channel<Beat> _channel = Channel.CreateUnbounded<Beat>(
		new UnboundedChannelOptions { SingleWriter = false });
	private readonly BluetoothManager? _manager;
	private readonly BluetoothAdapter? _adapter;

	// Serial GATT operation queue — Android allows only one in-flight operation.
	private readonly object _opGate = new();
	private readonly Queue<Action> _gattOps = new();
	private bool _opInFlight;

	private DeviceInformation _deviceInfo = new();

	// Latch the most recent one-shot reads so a subscriber that wires up after they fired
	// still converges (the pipeline replays these on wiring). Null/false until the first read.
	private BatteryReading? _latestBattery;
	private bool _deviceInfoRead;
	private ScanCallbackImpl? _scanCallback;
	private GattCallbackImpl? _gattCallback;
	private BluetoothGatt? _gatt;
	private bool _started;
	private bool _disposed;

	public AndroidBleSource(
		Context context,
		HeartRateDeviceType deviceType = HeartRateDeviceType.Auto,
		AndroidBleStateRestoration? restoration = null,
		bool enableMotion = false,
		IntervalSource intervalSource = IntervalSource.HeartRateService)
	{
		_context = context ?? throw new ArgumentNullException(nameof(context));
		_deviceType = deviceType;
		_enableMotion = enableMotion;
		_intervalSource = intervalSource;
		_restoration = restoration ?? new AndroidBleStateRestoration(context);

		_manager = context.GetSystemService(Context.BluetoothService) as BluetoothManager;
		_adapter = _manager?.Adapter;
	}

	public async IAsyncEnumerable<Beat> GetBeatsAsync(
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		// Begin connecting on first consumption, mirroring the Apple source which
		// starts once CoreBluetooth powers on. Idempotent.
		Start();

		using var _ = cancellationToken.Register(Stop);

		await foreach (var beat in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
		{
			yield return beat;
		}
	}

	/// <summary>
	/// Begins connecting: prefers a remembered device address (no scan delay),
	/// otherwise scans filtered to the Heart Rate Service. A no-op when Bluetooth
	/// is unavailable or off — the channel simply stays empty and the pipeline
	/// runs cold until the radio comes up and the source is restarted.
	/// </summary>
	public void Start()
	{
		if (_started || _disposed)
		{
			return;
		}

		if (_adapter is null || !_adapter.IsEnabled)
		{
			return;
		}

		_started = true;

		string? known = _restoration.LoadDeviceAddress();
		if (known is not null)
		{
			try
			{
				var device = _adapter.GetRemoteDevice(known);
				if (device is not null)
				{
					ConnectTo(device);
					return;
				}
			}
			catch (global::Java.Lang.IllegalArgumentException)
			{
				// Stored address no longer valid — fall through to a fresh scan.
				_restoration.Clear();
			}
		}

		StartScan();
	}

	private void StartScan()
	{
		var scanner = _adapter?.BluetoothLeScanner;
		if (scanner is null)
		{
			return;
		}

		// Pin the scan to the HR service so we never see unrelated peripherals
		// (and to support neverForLocation in the manifest).
		var filter = new ScanFilter.Builder()!
			.SetServiceUuid(new ParcelUuid(HeartRateServiceUuid))!
			.Build()!;
		var settings = new ScanSettings.Builder()!
			.SetScanMode(global::Android.Bluetooth.LE.ScanMode.LowLatency)!
			.Build()!;

		_scanCallback = new ScanCallbackImpl(this);
		scanner.StartScan([filter], settings, _scanCallback);
	}

	private void StopScan()
	{
		if (_scanCallback is not null)
		{
			_adapter?.BluetoothLeScanner?.StopScan(_scanCallback);
			_scanCallback = null;
		}
	}

	private void ConnectTo(BluetoothDevice device)
	{
		StopScan();
		_artifactFilter.Reset();
		_hrsDiagFilter.Reset();
		_rpeak.Reset();
		_polarIntervalsActive = false;
		_gattCallback = new GattCallbackImpl(this);

		// autoConnect: true is the Android analog of iOS state restoration —
		// the stack reconnects on its own when the sensor returns to range. The
		// three-argument overload (transport AUTO) is used rather than passing an
		// explicit LE transport: the transport enum has been renamed across binding
		// versions, and AUTO resolves to LE for a Heart Rate Service device anyway.
		_gatt = device.ConnectGatt(_context, autoConnect: true, _gattCallback);
	}

	private void Stop()
	{
		StopScan();
		try
		{
			_gatt?.Disconnect();
			_gatt?.Close();
		}
		catch (global::Java.Lang.Exception)
		{
			// Closing a half-open GATT can throw on some stacks — nothing to do.
		}

		_gatt = null;
		_gattCallback = null;
		_started = false;

		lock (_opGate)
		{
			_gattOps.Clear();
			_opInFlight = false;
		}
	}

	// --- Scan callback ---------------------------------------------------------

	private void OnDeviceDiscovered(BluetoothDevice device, string? advertisedName)
	{
		if (DeviceNamePrefix.For(_deviceType) is string prefix)
		{
			string name = advertisedName ?? device.Name ?? string.Empty;
			if (!name.Contains(prefix, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
		}

		ConnectTo(device);
	}

	// --- GATT operation queue --------------------------------------------------

	private void EnqueueGattOp(Action op)
	{
		lock (_opGate)
		{
			_gattOps.Enqueue(op);
			if (!_opInFlight)
			{
				DrainNextLocked();
			}
		}
	}

	private void CompleteGattOp()
	{
		lock (_opGate)
		{
			_opInFlight = false;
			DrainNextLocked();
		}
	}

	private void DrainNextLocked()
	{
		if (_opInFlight || _gattOps.Count == 0)
		{
			return;
		}

		_opInFlight = true;
		var op = _gattOps.Dequeue();

		// Run outside the lock so the op (a GATT call) can't deadlock against a
		// callback that re-enters the queue on the same thread.
		Task.Run(op);
	}

	// --- Connection / discovery handlers (invoked from the GATT callback) ------

	private void OnConnected(BluetoothGatt gatt, BluetoothDevice device)
	{
		if (device.Address is string address)
		{
			_restoration.SaveDeviceAddress(address);
		}

		_artifactFilter.Reset();
		_hrsDiagFilter.Reset();
		_rpeak.Reset();
		_polarIntervalsActive = false;
		EnqueueGattOp(() => gatt.DiscoverServices());
	}

	private void OnServicesDiscovered(BluetoothGatt gatt)
	{
		var hr = gatt.GetService(HeartRateServiceUuid)?.GetCharacteristic(HrMeasurementCharUuid);
		if (hr is not null)
		{
			EnqueueGattOp(() => EnableNotifications(gatt, hr));
		}

		var battery = gatt.GetService(BatteryServiceUuid)?.GetCharacteristic(BatteryLevelCharUuid);
		if (battery is not null)
		{
			EnqueueGattOp(() => gatt.ReadCharacteristic(battery));
			EnqueueGattOp(() => EnableNotifications(gatt, battery));
		}

		var deviceInfo = gatt.GetService(DeviceInfoServiceUuid);
		if (deviceInfo is not null)
		{
			foreach (var uuid in DeviceInfoCharacteristics)
			{
				var ch = deviceInfo.GetCharacteristic(uuid);
				if (ch is not null)
				{
					EnqueueGattOp(() => gatt.ReadCharacteristic(ch));
				}
			}
		}

		if (PmdEnabled)
		{
			var pmd = gatt.GetService(PmdServiceUuid);
			var data = pmd?.GetCharacteristic(PmdDataUuid);
			var controlPoint = pmd?.GetCharacteristic(PmdControlPointUuid);
			if (data is not null && controlPoint is not null)
			{
				// Notifications on the data stream, indications on the control point, then ask the
				// device to start each desired PMD stream — each step a serialised GATT op.
				EnqueueGattOp(() => EnableNotifications(gatt, data));
				EnqueueGattOp(() => EnableNotifications(gatt, controlPoint, indicate: true));

				if (_enableMotion)
				{
					EnqueueGattOp(() => WriteControlPoint(gatt, controlPoint, PmdControlPoint.BuildStartAcc()));
				}

				if (_intervalSource == IntervalSource.PolarPpi)
				{
					EnqueueGattOp(() => WriteControlPoint(gatt, controlPoint, PmdControlPoint.BuildStartPpi()));
				}
				else if (_intervalSource == IntervalSource.PolarEcg)
				{
					EnqueueGattOp(() => WriteControlPoint(gatt, controlPoint, PmdControlPoint.BuildStartEcg()));
				}
			}
		}
	}

	private void EnableNotifications(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, bool indicate = false)
	{
		gatt.SetCharacteristicNotification(characteristic, true);
		var cccd = characteristic.GetDescriptor(CccdUuid);
		if (cccd is null)
		{
			// No CCCD — can't enable notifications; release the queue slot anyway.
			CompleteGattOp();
			return;
		}

#pragma warning disable CA1422 // SetValue/WriteDescriptor(descriptor) deprecated API 33+, but the
		// value-returning overloads are API 33-only; the deprecated path works across our API 26 floor.
		// The PMD control point delivers command responses as indications, not notifications.
		var cccdValue = indicate
			? BluetoothGattDescriptor.EnableIndicationValue!
			: BluetoothGattDescriptor.EnableNotificationValue!;
		cccd.SetValue(cccdValue.ToArray());
		if (!gatt.WriteDescriptor(cccd))
		{
			// The write didn't dispatch, so OnDescriptorWrite won't fire — free the slot.
			CompleteGattOp();
		}
#pragma warning restore CA1422
	}

	// Writes a PMD control-point command (e.g. "start accelerometer"). Completes via OnCharacteristicWrite.
	private void WriteControlPoint(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] command)
	{
#pragma warning disable CA1422 // SetValue/WriteCharacteristic(characteristic) deprecated API 33+; the
		// value-carrying overload is API 33-only and the deprecated path works across our API 26 floor.
		characteristic.WriteType = GattWriteType.Default;
		characteristic.SetValue(command);
		if (!gatt.WriteCharacteristic(characteristic))
		{
			CompleteGattOp();
		}
#pragma warning restore CA1422
	}

	private void OnCharacteristicChanged(BluetoothGattCharacteristic characteristic, byte[] value)
	{
		if (characteristic.Uuid is null || value.Length == 0)
		{
			return;
		}

		if (characteristic.Uuid.Equals(HrMeasurementCharUuid))
		{
			OnMeasurementBytes(value);
		}
		else if (characteristic.Uuid.Equals(BatteryLevelCharUuid))
		{
			OnBatteryByte(value[0]);
		}
		else if (characteristic.Uuid.Equals(PmdDataUuid))
		{
			OnPmdData(value);
		}
	}

	// Decode a PMD data frame: ACC → motion, PPI/ECG → beats (suppressing HRS once they're live).
	private void OnPmdData(byte[] bytes)
	{
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
					_channel.Writer.TryWrite(ppiBeat);
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
						_channel.Writer.TryWrite(new Beat(now, rr, bpm, isArtifact));
					}
				}

				EcgSamplesReceived?.Invoke(new EcgSamples(now, microVolts, EcgSampleRateHz, peaks));
				break;
			}
		}
	}

	private void OnCharacteristicRead(BluetoothGattCharacteristic characteristic, byte[] value)
	{
		if (characteristic.Uuid is null)
		{
			return;
		}

		if (characteristic.Uuid.Equals(BatteryLevelCharUuid) && value.Length > 0)
		{
			OnBatteryByte(value[0]);
		}
		else if (IsDeviceInfoCharacteristic(characteristic.Uuid))
		{
			OnDeviceInfoCharacteristic(characteristic.Uuid, value);
		}
	}

	private static bool IsDeviceInfoCharacteristic(UUID uuid) =>
		Array.Exists(DeviceInfoCharacteristics, u => u.Equals(uuid));

	private void OnMeasurementBytes(byte[] payload)
	{
		var measurement = HrMeasurementParser.Parse(payload);
		var now = DateTimeOffset.UtcNow;

		// Surface contact on every notification — the only signal that survives
		// contact loss, when RR intervals (and beats) dry up.
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
			_channel.Writer.TryWrite(new Beat(now, rrMs, measurement.HeartRateBpm, isArtifact));
		}
	}

	private void OnBatteryByte(byte percent)
	{
		var reading = new BatteryReading(DateTimeOffset.UtcNow, Math.Clamp((int)percent, 0, 100));
		_latestBattery = reading;
		BatteryLevelChanged?.Invoke(reading);
	}

	private void OnDeviceInfoCharacteristic(UUID uuid, byte[] bytes)
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

		_deviceInfoRead = true;
		DeviceInformationChanged?.Invoke(_deviceInfo);
	}

	private static UUID ShortUuid(int assignedNumber) =>
		UUID.FromString($"0000{assignedNumber:X4}-0000-1000-8000-00805F9B34FB")!;

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		Stop();
		_channel.Writer.TryComplete();
	}

	private sealed class ScanCallbackImpl(AndroidBleSource owner) : ScanCallback
	{
		public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
		{
			if (result?.Device is { } device)
			{
				owner.OnDeviceDiscovered(device, result.ScanRecord?.DeviceName);
			}
		}
	}

	private sealed class GattCallbackImpl(AndroidBleSource owner) : BluetoothGattCallback
	{
		public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
		{
			if (gatt?.Device is null)
			{
				return;
			}

			if (newState == ProfileState.Connected)
			{
				owner.OnConnected(gatt, gatt.Device);
			}

			// On disconnect, autoConnect: true means the platform reconnects on
			// its own — no explicit reconnect call needed here.
		}

		public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
		{
			if (gatt is not null && status == GattStatus.Success)
			{
				owner.OnServicesDiscovered(gatt);
			}

			owner.CompleteGattOp();
		}

		public override void OnDescriptorWrite(BluetoothGatt? gatt, BluetoothGattDescriptor? descriptor, GattStatus status)
			=> owner.CompleteGattOp();

		public override void OnCharacteristicWrite(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
			=> owner.CompleteGattOp();

#pragma warning disable CA1422 // Two-arg OnCharacteristicChanged / GetValue() deprecated API 33+; the
		// value-carrying overload is API 33-only and the deprecated path works across our API 26 floor.
		public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
		{
			if (characteristic?.GetValue() is { } value)
			{
				owner.OnCharacteristicChanged(characteristic, value);
			}
		}

		public override void OnCharacteristicRead(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
		{
			if (status == GattStatus.Success && characteristic?.GetValue() is { } value)
			{
				owner.OnCharacteristicRead(characteristic, value);
			}

			owner.CompleteGattOp();
		}
#pragma warning restore CA1422
	}
}
