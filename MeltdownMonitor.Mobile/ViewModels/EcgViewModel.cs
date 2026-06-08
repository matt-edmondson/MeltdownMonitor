using Avalonia.Threading;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Mobile.ViewModels;

/// <summary>
/// Drives the dedicated ECG tab: a "stacked beats" view where each cardiac cycle is re-sliced and
/// overlaid (newest brightest, older ones fading) so beat-to-beat variability is visible at a glance,
/// plus a heart rate derived from the detected peaks and a signal-quality cue. Fed by
/// <see cref="Pipeline.EcgUpdated"/>, which only fires when the Polar ECG interval source is streaming
/// (the H10).
/// </summary>
public sealed class EcgViewModel : ViewModelBase
{
	private EcgBeatOverlay _overlay = EcgBeatOverlay.Empty;
	private EcgSignalQuality _quality = EcgSignalQuality.Unknown;
	private int _heartRate;

	/// <summary>The R-peak-aligned stack of recent beats for the overlay control.</summary>
	public EcgBeatOverlay Overlay
	{
		get => _overlay;
		private set
		{
			if (SetField(ref _overlay, value))
			{
				Raise(nameof(IsStreaming));
				Raise(nameof(IsIdle));
			}
		}
	}

	/// <summary>True once ECG is streaming — the cue to show the trace rather than the hint.</summary>
	public bool IsStreaming => _overlay.HasBeats;

	/// <summary>True when no ECG is streaming — shows the "select Polar ECG" hint.</summary>
	public bool IsIdle => !_overlay.HasBeats;

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
		Overlay = EcgBeatOverlay.Build(snapshot);
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
