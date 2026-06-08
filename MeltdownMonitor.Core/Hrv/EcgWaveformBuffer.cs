using MeltdownMonitor.Core.Beats;

namespace MeltdownMonitor.Core.Hrv;

/// <summary>Coarse confidence in the live ECG trace, for a UI quality cue.</summary>
public enum EcgSignalQuality
{
	/// <summary>Not enough samples yet to judge.</summary>
	Unknown,

	/// <summary>Flatlined (electrodes off-body / no contact) or railed (saturated) — not a usable trace.</summary>
	Poor,

	/// <summary>A plausible ECG: sensible amplitude with R-peaks at a physiological rate.</summary>
	Good,
}

/// <summary>
/// An immutable render snapshot of the recent ECG window.
/// </summary>
/// <param name="MicroVolts">Samples oldest-first.</param>
/// <param name="RPeakIndices">Indices into <see cref="MicroVolts"/> marking detected R-peaks.</param>
/// <param name="MinMicroVolts">Minimum sample in the window (for auto-scaling).</param>
/// <param name="MaxMicroVolts">Maximum sample in the window.</param>
/// <param name="SampleRateHz">Sample rate of the trace.</param>
/// <param name="Quality">Signal-quality cue.</param>
/// <param name="TotalPeaks">Monotonic count of every R-peak detected since the stream began — a stable
/// new-beat signal for the overlay's sweep animation (unaffected by peaks scrolling out of the window).</param>
public record EcgWaveformSnapshot(
	IReadOnlyList<int> MicroVolts,
	IReadOnlyList<int> RPeakIndices,
	int MinMicroVolts,
	int MaxMicroVolts,
	double SampleRateHz,
	EcgSignalQuality Quality,
	long TotalPeaks = 0)
{
	/// <summary>An empty snapshot (no ECG streaming).</summary>
	public static readonly EcgWaveformSnapshot Empty = new([], [], 0, 0, 0, EcgSignalQuality.Unknown);
}

/// <summary>
/// A rolling window of recent ECG samples plus the positions of detected R-peaks, for the live
/// waveform display. Platform-neutral and unit-tested; the heads forward raw samples
/// (<see cref="EcgSamples"/>) and this maintains the fixed-duration view the UI renders.
/// </summary>
public sealed class EcgWaveformBuffer
{
	// A flatline this small (peak-to-peak) means electrodes off-body, not a real trace.
	private const int FlatlineThresholdMicroVolts = 50;

	private readonly object _lock = new();
	private readonly double _windowSeconds;
	private readonly Queue<int> _samples = new();
	private readonly Queue<long> _peakIndices = new();
	private double _sampleRateHz;
	private long _totalPeaks;
	private long _totalAppended;

	/// <param name="windowSeconds">How many seconds of trace to retain (the visible strip width).</param>
	public EcgWaveformBuffer(double windowSeconds = 6.0)
	{
		_windowSeconds = windowSeconds;
	}

	/// <summary>True once any samples have been buffered.</summary>
	public bool HasData
	{
		get { lock (_lock) { return _samples.Count > 0; } }
	}

	/// <summary>Appends a decoded ECG frame, evicting anything older than the retained window.</summary>
	public void Append(EcgSamples batch)
	{
		lock (_lock)
		{
			if (batch.SampleRateHz > 0)
			{
				_sampleRateHz = batch.SampleRateHz;
			}

			long batchStart = _totalAppended;
			foreach (int sample in batch.MicroVolts)
			{
				_samples.Enqueue(sample);
				_totalAppended++;
			}

			foreach (int offset in batch.RPeakOffsets)
			{
				_peakIndices.Enqueue(batchStart + offset);
				_totalPeaks++;
			}

			int capacity = CapacityLocked();
			while (_samples.Count > capacity)
			{
				_samples.Dequeue();
			}

			// Drop peak markers that have scrolled off the left edge.
			long oldestRetained = _totalAppended - _samples.Count;
			while (_peakIndices.Count > 0 && _peakIndices.Peek() < oldestRetained)
			{
				_peakIndices.Dequeue();
			}
		}
	}

	/// <summary>Number of samples retained at the current sample rate (the window length).</summary>
	public int Capacity
	{
		get { lock (_lock) { return CapacityLocked(); } }
	}

	private int CapacityLocked() => _sampleRateHz > 0 ? (int)Math.Round(_windowSeconds * _sampleRateHz) : int.MaxValue;

	/// <summary>Builds a render snapshot: samples oldest-first, peak positions relative to the window, scale, quality.</summary>
	public EcgWaveformSnapshot Snapshot()
	{
		lock (_lock)
		{
			if (_samples.Count == 0)
			{
				return EcgWaveformSnapshot.Empty;
			}

			int[] samples = [.. _samples];
			long oldestRetained = _totalAppended - samples.Length;
			int[] peaks = [.. _peakIndices.Select(i => (int)(i - oldestRetained)).Where(i => i >= 0 && i < samples.Length)];

			int min = samples[0];
			int max = samples[0];
			foreach (int s in samples)
			{
				if (s < min) { min = s; }
				if (s > max) { max = s; }
			}

			return new EcgWaveformSnapshot(samples, peaks, min, max, _sampleRateHz, AssessQuality(samples, max - min, peaks.Length), _totalPeaks);
		}
	}

	private EcgSignalQuality AssessQuality(int[] samples, int peakToPeak, int peakCount)
	{
		// Need roughly a second of data before judging.
		if (_sampleRateHz <= 0 || samples.Length < _sampleRateHz)
		{
			return EcgSignalQuality.Unknown;
		}

		// Flatline (off-body) or no R-peaks across a full window ⇒ not a usable trace.
		if (peakToPeak < FlatlineThresholdMicroVolts || peakCount == 0)
		{
			return EcgSignalQuality.Poor;
		}

		// A plausible heart rate from the peaks seen across the window guards against noise that
		// trips the detector erratically.
		double windowSeconds = samples.Length / _sampleRateHz;
		double bpm = peakCount / windowSeconds * 60.0;
		return bpm is >= 30 and <= 220 ? EcgSignalQuality.Good : EcgSignalQuality.Poor;
	}

	/// <summary>Clears the window — call on disconnect so a stale trace doesn't linger.</summary>
	public void Reset()
	{
		lock (_lock)
		{
			_samples.Clear();
			_peakIndices.Clear();
			_totalAppended = 0;
			_totalPeaks = 0;
			_sampleRateHz = 0;
		}
	}
}
