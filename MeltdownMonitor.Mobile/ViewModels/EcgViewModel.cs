using Avalonia.Threading;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Mobile.ViewModels;

/// <summary>
/// Drives the dedicated ECG tab: the live raw waveform, R-peak markers, a heart rate derived from the
/// detected peaks, and a signal-quality cue. Fed by <see cref="Pipeline.EcgUpdated"/>, which only
/// fires when the Polar ECG interval source is streaming (the H10).
/// </summary>
public sealed class EcgViewModel : ViewModelBase
{
	private IReadOnlyList<double> _samples = [];
	private IReadOnlyList<int> _rPeakIndices = [];
	private EcgSignalQuality _quality = EcgSignalQuality.Unknown;
	private int _heartRate;

	/// <summary>The recent ECG samples (microvolts), oldest first, for the strip control.</summary>
	public IReadOnlyList<double> Samples
	{
		get => _samples;
		private set
		{
			if (SetField(ref _samples, value))
			{
				Raise(nameof(IsStreaming));
				Raise(nameof(IsIdle));
			}
		}
	}

	/// <summary>Indices into <see cref="Samples"/> marking detected R-peaks.</summary>
	public IReadOnlyList<int> RPeakIndices
	{
		get => _rPeakIndices;
		private set => SetField(ref _rPeakIndices, value);
	}

	/// <summary>True once ECG is streaming — the cue to show the strip rather than the hint.</summary>
	public bool IsStreaming => _samples.Count > 0;

	/// <summary>True when no ECG is streaming — shows the "select Polar ECG" hint.</summary>
	public bool IsIdle => _samples.Count == 0;

	/// <summary>Heart rate derived from R-peak spacing, e.g. "72 bpm".</summary>
	public string HeartRateText => _heartRate > 0 ? $"{_heartRate} bpm" : "— bpm";

	/// <summary>Signal-quality cue for the trace.</summary>
	public string QualityText => _quality switch
	{
		EcgSignalQuality.Good => "Signal: good",
		EcgSignalQuality.Poor => "Signal: poor — check electrode contact",
		_ => "Signal: —",
	};

	/// <summary>Wired to <see cref="Pipeline.EcgUpdated"/>; public so tests can drive it.</summary>
	public void OnEcgUpdated(EcgWaveformSnapshot snapshot) => RunOnUi(() =>
	{
		Samples = [.. snapshot.MicroVolts.Select(v => (double)v)];
		RPeakIndices = snapshot.RPeakIndices;
		_quality = snapshot.Quality;
		Raise(nameof(QualityText));
		_heartRate = EstimateBpm(snapshot);
		Raise(nameof(HeartRateText));
	});

	public void AttachPipeline(Pipeline pipeline)
	{
		ArgumentNullException.ThrowIfNull(pipeline);
		pipeline.EcgUpdated += OnEcgUpdated;
	}

	// Mean heart rate from the spacing of the R-peaks currently in the window.
	private static int EstimateBpm(EcgWaveformSnapshot snapshot)
	{
		IReadOnlyList<int> peaks = snapshot.RPeakIndices;
		if (snapshot.SampleRateHz <= 0 || peaks.Count < 2)
		{
			return 0;
		}

		double meanRrSamples = (double)(peaks[^1] - peaks[0]) / (peaks.Count - 1);
		double rrMs = meanRrSamples / snapshot.SampleRateHz * 1000.0;
		return rrMs > 0 ? (int)Math.Round(60000.0 / rrMs) : 0;
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
