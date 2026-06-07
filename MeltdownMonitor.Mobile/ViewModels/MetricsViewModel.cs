using Avalonia.Threading;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.Mobile.ViewModels;

/// <summary>
/// Backing data for the Metrics tab: live ring-buffered histories of every HRV metric
/// the desktop StatusWindow charts, plus the raw RR stream for the Poincaré/RR plots.
/// Fed from Pipeline.SampleUpdated / BeatReceived / BatteryUpdated and backfilled from
/// the repository on load. Capacity tracks the window-minutes setting and emit cadence,
/// mirroring the desktop DesiredCapacity formula (a 60-sample floor keeps a tiny window
/// from starving the charts).
/// </summary>
public sealed class MetricsViewModel : ViewModelBase
{
	private const int RrCapacity = 512;

	private readonly Func<int> _windowMinutes;
	private readonly Func<double> _emitInterval;
	private readonly Action<Action> _dispatch;

	private readonly List<double> _rmssd = [];
	private readonly List<double> _baselineRmssd = [];
	private readonly List<double> _pnn50 = [];
	private readonly List<double> _sdnn = [];
	private readonly List<double> _meanHr = [];
	private readonly List<double> _baselineHr = [];
	private readonly List<double> _lf = [];
	private readonly List<double> _hf = [];
	private readonly List<double> _lfhf = [];
	private readonly List<double> _baselineLfHf = [];
	private readonly List<double> _sd1 = [];
	private readonly List<double> _sd2 = [];
	private readonly List<double> _sd1sd2 = [];
	private readonly List<double> _battery = [];
	private readonly List<double> _contact = [];
	private readonly List<double> _ts = [];
	private readonly List<double> _extTs = [];
	private readonly List<double> _batteryTs = [];
	private readonly List<double> _contactTs = [];
	private readonly List<double> _recentRr = [];

	/// <param name="dispatcher">Marshals a mutation onto the UI thread. Defaults to the Avalonia
	/// <see cref="Dispatcher.UIThread"/> (apply inline when already on it, else Post). Injectable so
	/// unit tests can run synchronously without binding the process-global UI-thread dispatcher.</param>
	public MetricsViewModel(
		Func<int>? windowMinutesProvider = null,
		Func<double>? emitIntervalProvider = null,
		Action<Action>? dispatcher = null)
	{
		_windowMinutes = windowMinutesProvider ?? (() => 60);
		_emitInterval = emitIntervalProvider ?? (() => 5.0);
		_dispatch = dispatcher ?? RunOnUi;
	}

	public IReadOnlyList<double> Rmssd => _rmssd;
	public IReadOnlyList<double> BaselineRmssd => _baselineRmssd;
	public IReadOnlyList<double> Pnn50 => _pnn50;
	public IReadOnlyList<double> Sdnn => _sdnn;
	public IReadOnlyList<double> MeanHr => _meanHr;
	public IReadOnlyList<double> BaselineHr => _baselineHr;
	public IReadOnlyList<double> LfPower => _lf;
	public IReadOnlyList<double> HfPower => _hf;
	public IReadOnlyList<double> LfHfRatio => _lfhf;
	public IReadOnlyList<double> BaselineLfHf => _baselineLfHf;
	public IReadOnlyList<double> Sd1 => _sd1;
	public IReadOnlyList<double> Sd2 => _sd2;
	public IReadOnlyList<double> Sd1Sd2 => _sd1sd2;
	public IReadOnlyList<double> Battery => _battery;
	public IReadOnlyList<double> Contact => _contact;
	public IReadOnlyList<double> RmssdTimestamps => _ts;

	/// <summary>Unix epoch seconds, one per extended-metric point (SDNN, LF, HF, LF/HF, SD1,
	/// SD2, SD1/SD2). Kept separately from <see cref="RmssdTimestamps"/> because extended
	/// metrics are absent during warm-up — sharing the per-sample list would desync the
	/// lengths and silently drop the charts to index spacing.</summary>
	public IReadOnlyList<double> ExtendedTimestamps => _extTs;
	public IReadOnlyList<double> BatteryTimestamps => _batteryTs;
	public IReadOnlyList<double> ContactTimestamps => _contactTs;
	public IReadOnlyList<double> RecentRr => _recentRr;

	/// <summary>True once any live or backfilled metric data exists. Drives the Metrics tab's
	/// empty state so a freshly opened, sensor-less tab shows guidance instead of a wall of
	/// blank charts.</summary>
	public bool HasData => _rmssd.Count > 0 || _recentRr.Count > 0;

	/// <summary>Inverse of <see cref="HasData"/> — bound by the empty-state overlay.</summary>
	public bool IsEmpty => !HasData;

	/// <summary>Window width (seconds) the charts span — drives MetricChart.WindowSeconds.</summary>
	public double WindowSeconds => Math.Max(60, _windowMinutes()) * 60.0;

	private int Capacity =>
		Math.Max(60, (int)(_windowMinutes() * 60.0 / Math.Max(0.5, _emitInterval())));

	public void OnSampleUpdated(HrvSample s) => _dispatch(() =>
	{
		double ts = s.Timestamp.ToUnixTimeMilliseconds() / 1000.0;
		Append(_rmssd, s.Rmssd);
		Append(_baselineRmssd, s.BaselineRmssd);
		Append(_pnn50, s.Pnn50);
		Append(_meanHr, s.MeanHr);
		Append(_baselineHr, s.BaselineHr);
		Append(_contact, s.SensorContact == SensorContactStatus.NotDetected ? 0.0 : 1.0);
		Append(_contactTs, ts);
		Append(_ts, ts);

		if (s.Extended is { } ext)
		{
			Append(_sdnn, ext.Sdnn);
			Append(_lf, ext.LfPowerMs2);
			Append(_hf, ext.HfPowerMs2);
			Append(_lfhf, ext.LfHfRatio);
			Append(_sd1, ext.SD1);
			Append(_sd2, ext.SD2);
			Append(_sd1sd2, ext.SD1SD2Ratio);
			// The LF/HF baseline overlays the LF/HF chart, whose X axis is the extended
			// cadence — append it here so the two series stay length-aligned.
			Append(_baselineLfHf, s.BaselineLfHfRatio);
			Append(_extTs, ts);
		}

		RaiseAllSeriesChanged();
	});

	public void OnBeatReceived(Beat beat) => _dispatch(() =>
	{
		if (beat.IsArtifact)
		{
			return;
		}

		_recentRr.Add(beat.RrMs);
		while (_recentRr.Count > RrCapacity)
		{
			_recentRr.RemoveAt(0);
		}

		Raise(nameof(RecentRr));
		Raise(nameof(HasData));
		Raise(nameof(IsEmpty));
	});

	public void OnBatteryUpdated(BatteryReading reading) => _dispatch(() =>
	{
		_battery.Add(reading.Percent);
		_batteryTs.Add(reading.Timestamp.ToUnixTimeMilliseconds() / 1000.0);
		Trim(_battery);
		Trim(_batteryTs);
		Raise(nameof(Battery));
		Raise(nameof(BatteryTimestamps));
	});

	/// <summary>Seeds the series from persisted history (oldest first) so the charts aren't
	/// blank on open. Mirrors the desktop StatusWindow.BackfillFromRepository.</summary>
	public void Backfill(IReadOnlyList<HrvSample> samples, IReadOnlyList<BatteryReading> batteries) => _dispatch(() =>
	{
		int cap = Capacity;
		foreach (var s in samples.TakeLast(cap))
		{
			double ts = s.Timestamp.ToUnixTimeMilliseconds() / 1000.0;
			_rmssd.Add(s.Rmssd);
			_baselineRmssd.Add(s.BaselineRmssd);
			_pnn50.Add(s.Pnn50);
			_meanHr.Add(s.MeanHr);
			_baselineHr.Add(s.BaselineHr);
			_contact.Add(s.SensorContact == SensorContactStatus.NotDetected ? 0.0 : 1.0);
			_contactTs.Add(ts);
			_ts.Add(ts);

			if (s.Extended is { } ext)
			{
				_sdnn.Add(ext.Sdnn);
				_lf.Add(ext.LfPowerMs2);
				_hf.Add(ext.HfPowerMs2);
				_lfhf.Add(ext.LfHfRatio);
				_sd1.Add(ext.SD1);
				_sd2.Add(ext.SD2);
				_sd1sd2.Add(ext.SD1SD2Ratio);
				_baselineLfHf.Add(s.BaselineLfHfRatio);
				_extTs.Add(ts);
			}
		}

		foreach (var b in batteries.TakeLast(cap))
		{
			_battery.Add(b.Percent);
			_batteryTs.Add(b.Timestamp.ToUnixTimeMilliseconds() / 1000.0);
		}

		RaiseAllSeriesChanged();
		Raise(nameof(Battery));
		Raise(nameof(BatteryTimestamps));
	});

	/// <summary>Reads recent history from the repository on a background thread, then applies
	/// it on the UI thread. Errors are swallowed (a missing/locked DB must never block the tab),
	/// matching the desktop backfill.</summary>
	public async Task LoadFromRepositoryAsync(string databasePath)
	{
		var to = DateTimeOffset.UtcNow;
		var from = to.AddMinutes(-Math.Max(1, _windowMinutes()));
		try
		{
			var samples = await Task.Run(() => MeltdownRepository.ReadHistory(databasePath, from, to)).ConfigureAwait(false);
			IReadOnlyList<BatteryReading> batteries;
			try
			{
				batteries = await Task.Run(() => MeltdownRepository.ReadBatteryHistory(databasePath, from, to)).ConfigureAwait(false);
			}
			catch
			{
				batteries = [];
			}

			Backfill(samples, batteries);
		}
		catch
		{
			// best-effort: leave the series to fill from live samples
		}
	}

	private void Append(List<double> list, double value)
	{
		list.Add(value);
		Trim(list);
	}

	private void Trim(List<double> list)
	{
		int cap = Capacity;
		while (list.Count > cap)
		{
			list.RemoveAt(0);
		}
	}

	private void RaiseAllSeriesChanged()
	{
		Raise(nameof(Rmssd));
		Raise(nameof(BaselineRmssd));
		Raise(nameof(Pnn50));
		Raise(nameof(Sdnn));
		Raise(nameof(MeanHr));
		Raise(nameof(BaselineHr));
		Raise(nameof(LfPower));
		Raise(nameof(HfPower));
		Raise(nameof(LfHfRatio));
		Raise(nameof(BaselineLfHf));
		Raise(nameof(Sd1));
		Raise(nameof(Sd2));
		Raise(nameof(Sd1Sd2));
		Raise(nameof(Contact));
		Raise(nameof(RmssdTimestamps));
		Raise(nameof(ExtendedTimestamps));
		Raise(nameof(ContactTimestamps));
		Raise(nameof(WindowSeconds));
		Raise(nameof(HasData));
		Raise(nameof(IsEmpty));
	}

	private static void RunOnUi(Action apply)
	{
		// With no Avalonia Application (unit tests / design-time) there is no UI thread to marshal to,
		// so run inline. Checked first so we never touch Dispatcher.UIThread in that context.
		if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess())
		{
			apply();
		}
		else
		{
			Dispatcher.UIThread.Post(apply);
		}
	}
}
