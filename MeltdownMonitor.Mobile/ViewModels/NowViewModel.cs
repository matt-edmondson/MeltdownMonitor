using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Mobile.ViewModels;

/// <summary>
/// Backing view model for the Now screen — live state pill, HR/RMSSD
/// readout, sparkline history, and the connect/disconnect button. Pipeline
/// wiring is injected; this VM stays free of CoreBluetooth so the iOS head
/// can compose it with the real <c>PolarHrSource</c> and any other host can
/// stub it with a synthetic source for screenshots.
/// </summary>
public sealed class NowViewModel : ViewModelBase
{
	private const int SparklineMaxPoints = 360; // ~60 s at 6 Hz update cadence

	private readonly ObservableCollection<double> _rmssdHistory = [];
	private readonly ObservableCollection<double> _baselineHistory = [];

	private readonly Func<Task>? _onConnect;
	private readonly Func<Task>? _onDisconnect;

	private DetectorState _state = DetectorState.Idle;
	private bool _isPaused;
	private double _heartRate;
	private double _rmssd;
	private double _baselineRmssd;
	private DateTimeOffset _stateChangedAt = DateTimeOffset.UtcNow;
	private ConnectionState _connection = ConnectionState.Disconnected;

	public NowViewModel(
		Func<Task>? onConnect = null,
		Func<Task>? onDisconnect = null)
	{
		_onConnect = onConnect;
		_onDisconnect = onDisconnect;
		ToggleConnectionCommand = new RelayCommand(ToggleConnection);
	}

	public IReadOnlyList<double> RmssdHistory => _rmssdHistory;
	public IReadOnlyList<double> BaselineHistory => _baselineHistory;

	public DetectorState State
	{
		get => _state;
		private set
		{
			if (SetField(ref _state, value))
			{
				_stateChangedAt = DateTimeOffset.UtcNow;
				Raise(nameof(StateLabel));
				Raise(nameof(StateBrush));
				Raise(nameof(TimeSinceStateChange));
			}
		}
	}

	public bool IsPaused
	{
		get => _isPaused;
		set
		{
			if (SetField(ref _isPaused, value))
			{
				Raise(nameof(StateLabel));
				Raise(nameof(StateBrush));
			}
		}
	}

	public string StateLabel => StateColors.LabelFor(_state, _isPaused);
	public IBrush StateBrush => StateColors.BrushFor(_state, _isPaused);

	public double HeartRate
	{
		get => _heartRate;
		private set
		{
			if (SetField(ref _heartRate, value))
			{
				Raise(nameof(HeartRateText));
			}
		}
	}

	public string HeartRateText =>
		_heartRate > 0 ? $"{_heartRate:F0} bpm" : "— bpm";

	public double Rmssd
	{
		get => _rmssd;
		private set
		{
			if (SetField(ref _rmssd, value))
			{
				Raise(nameof(RmssdText));
			}
		}
	}

	public string RmssdText =>
		_rmssd > 0 ? $"RMSSD {_rmssd:F1} ms" : "RMSSD —";

	public double BaselineRmssd
	{
		get => _baselineRmssd;
		private set
		{
			if (SetField(ref _baselineRmssd, value))
			{
				Raise(nameof(BaselineText));
			}
		}
	}

	public string BaselineText =>
		_baselineRmssd > 0 ? $"Baseline {_baselineRmssd:F1} ms" : "Baseline warming up…";

	public string TimeSinceStateChange
	{
		get
		{
			var span = DateTimeOffset.UtcNow - _stateChangedAt;
			if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds}s in {StateLabel}";
			if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m in {StateLabel}";
			return $"{(int)span.TotalHours}h in {StateLabel}";
		}
	}

	public ConnectionState Connection
	{
		get => _connection;
		set
		{
			if (SetField(ref _connection, value))
			{
				Raise(nameof(ConnectionLabel));
				(ToggleConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
			}
		}
	}

	public string ConnectionLabel => _connection switch
	{
		ConnectionState.Disconnected => "Connect device",
		ConnectionState.Connecting => "Connecting…",
		ConnectionState.Connected => "Disconnect",
		ConnectionState.Reconnecting => "Reconnecting…",
		_ => "Connect device",
	};

	public ICommand ToggleConnectionCommand { get; }

	/// <summary>
	/// Push a fresh sample into the VM. Marshals to the UI thread so the
	/// pipeline callback can call this from a background BLE thread.
	/// </summary>
	public void OnSampleUpdated(HrvSample sample)
	{
		void Apply()
		{
			HeartRate = sample.MeanHr;
			Rmssd = sample.Rmssd;
			BaselineRmssd = sample.BaselineRmssd;
			State = sample.State;

			_rmssdHistory.Add(sample.Rmssd);
			_baselineHistory.Add(sample.BaselineRmssd);
			TrimHistory();

			Raise(nameof(RmssdHistory));
			Raise(nameof(BaselineHistory));
		}

		if (Dispatcher.UIThread.CheckAccess())
		{
			Apply();
		}
		else
		{
			Dispatcher.UIThread.Post(Apply);
		}
	}

	public void TickTimeDisplay() => Raise(nameof(TimeSinceStateChange));

	private void TrimHistory()
	{
		while (_rmssdHistory.Count > SparklineMaxPoints)
		{
			_rmssdHistory.RemoveAt(0);
		}

		while (_baselineHistory.Count > SparklineMaxPoints)
		{
			_baselineHistory.RemoveAt(0);
		}
	}

	private async void ToggleConnection()
	{
		switch (_connection)
		{
			case ConnectionState.Disconnected:
				Connection = ConnectionState.Connecting;
				if (_onConnect is not null)
				{
					await _onConnect().ConfigureAwait(true);
					Connection = ConnectionState.Connected;
				}

				break;
			case ConnectionState.Connected:
				if (_onDisconnect is not null)
				{
					await _onDisconnect().ConfigureAwait(true);
				}

				Connection = ConnectionState.Disconnected;
				break;
		}
	}
}
