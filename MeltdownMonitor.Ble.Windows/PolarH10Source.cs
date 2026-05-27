using System.Runtime.CompilerServices;
using MeltdownMonitor.Core.Beats;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace MeltdownMonitor.Ble.Windows;

/// <summary>
/// Connects to a Polar H10 (or any GATT Heart Rate Service device) via WinRT BLE.
/// Reconnects automatically with exponential backoff on disconnect.
/// </summary>
public sealed class PolarH10Source : IBeatSource, IDisposable
{
	private static readonly Guid HeartRateServiceUuid = new("0000180d-0000-1000-8000-00805f9b34fb");
	private static readonly Guid HrMeasurementCharUuid = new("00002a37-0000-1000-8000-00805f9b34fb");

	private readonly string? _deviceNameFilter;
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

	/// <param name="deviceNameFilter">
	/// Optional substring match against the device name. If null, connects to
	/// the first device advertising the Heart Rate Service.
	/// </param>
	public PolarH10Source(string? deviceNameFilter = null)
	{
		_deviceNameFilter = deviceNameFilter;
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

			// Device disconnected — wait before retry
			var delay = RetryDelays[Math.Min(retryIndex, RetryDelays.Length - 1)];
			retryIndex++;
			await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task<ulong?> ScanForDeviceAsync(CancellationToken cancellationToken)
	{
		var tcs = new TaskCompletionSource<ulong>();
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		var watcher = new BluetoothLEAdvertisementWatcher();
		watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(HeartRateServiceUuid);

		watcher.Received += (_, args) =>
		{
			if (_deviceNameFilter is not null)
			{
				string name = args.Advertisement.LocalName ?? string.Empty;
				if (!name.Contains(_deviceNameFilter, StringComparison.OrdinalIgnoreCase))
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
			var reader = Windows.Storage.Streams.DataReader.FromBuffer(args.CharacteristicValue);
			var bytes = new byte[args.CharacteristicValue.Length];
			reader.ReadBytes(bytes);

			var measurement = HrMeasurementParser.Parse(bytes);
			var now = DateTimeOffset.UtcNow;

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
