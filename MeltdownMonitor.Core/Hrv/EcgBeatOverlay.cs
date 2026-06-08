namespace MeltdownMonitor.Core.Hrv;

/// <summary>One sample of an overlaid beat: microvolts at a signed time offset from that beat's R-peak (0 = the peak).</summary>
/// <param name="OffsetSeconds">Time relative to the beat's R-peak; negative is before the peak, positive after.</param>
/// <param name="MicroVolts">The sample amplitude.</param>
public readonly record struct EcgBeatSample(double OffsetSeconds, double MicroVolts);

/// <summary>
/// One R-peak-aligned cardiac cycle for the overlay view. <see cref="Age"/> is 0 for the most recent
/// completed beat and grows with each older beat — the renderer maps it to a diminishing alpha so the
/// stacked traces fade into the past.
/// </summary>
/// <param name="Samples">The beat's samples, oldest-first, with offsets relative to its R-peak.</param>
/// <param name="Age">0 = most recent completed beat; larger = older.</param>
public sealed record EcgOverlayBeat(IReadOnlyList<EcgBeatSample> Samples, int Age);

/// <summary>
/// Immutable render model for the "stacked beats" ECG view. Re-slices the rolling waveform
/// (<see cref="EcgWaveformSnapshot"/>) into the last few cardiac cycles, each aligned so its R-peak
/// sits at offset 0 on a shared "one beat wide" time axis. The renderer draws them stacked with
/// diminishing alpha — so beat-to-beat variability shows up as the spread of the overlaid traces —
/// and eases the newest beat to centre. Platform-neutral and unit-tested; the fade and the
/// ease-to-centre animation are render concerns owned by each head.
/// </summary>
/// <param name="Beats">Completed beats, oldest first, newest last (the newest carries <see cref="EcgOverlayBeat.Age"/> 0).</param>
/// <param name="Live">The still-incomplete current beat (samples from the latest R-peak onward), or null when none.</param>
/// <param name="HalfWindowSeconds">Half the axis width: the axis spans [-HalfWindowSeconds, +HalfWindowSeconds] around the R-peak.</param>
/// <param name="MinMicroVolts">Minimum sample across the retained window (for vertical auto-scaling).</param>
/// <param name="MaxMicroVolts">Maximum sample across the retained window.</param>
/// <param name="SampleRateHz">Sample rate of the trace.</param>
/// <param name="Quality">Signal-quality cue, carried through from the source snapshot.</param>
public sealed record EcgBeatOverlay(
	IReadOnlyList<EcgOverlayBeat> Beats,
	EcgOverlayBeat? Live,
	double HalfWindowSeconds,
	double MinMicroVolts,
	double MaxMicroVolts,
	double SampleRateHz,
	EcgSignalQuality Quality)
{
	/// <summary>An empty overlay (no ECG streaming / nothing alignable yet).</summary>
	public static readonly EcgBeatOverlay Empty = new([], null, 0, 0, 0, 0, EcgSignalQuality.Unknown);

	/// <summary>True once there's at least one beat — completed or live — to draw.</summary>
	public bool HasBeats => Beats.Count > 0 || Live is not null;

	/// <summary>
	/// Builds the overlay from a waveform snapshot, keeping at most <paramref name="maxBeats"/> completed beats
	/// behind the live one. Returns <see cref="Empty"/> when there is nothing to align (no sample rate or no peaks).
	/// </summary>
	public static EcgBeatOverlay Build(EcgWaveformSnapshot snapshot, int maxBeats = 10)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		IReadOnlyList<int> samples = snapshot.MicroVolts;
		IReadOnlyList<int> peaks = snapshot.RPeakIndices;
		double rate = snapshot.SampleRateHz;
		if (rate <= 0 || samples.Count == 0 || peaks.Count == 0 || maxBeats < 1)
		{
			return Empty;
		}

		// Window width is one cardiac cycle. Use the median RR across the visible peaks so a stray
		// long/short interval doesn't stretch the axis; symmetric around the peak keeps it centred.
		double half = MedianRrSeconds(peaks, rate) / 2.0;
		int halfSamples = Math.Max(1, (int)Math.Round(half * rate));

		// The most recent peak anchors the still-incomplete "live" beat; the ones before it form the
		// completed, fading stack. Walk newest-to-oldest and keep up to maxBeats that have a full window.
		int lastPeak = peaks.Count - 1;
		var completed = new List<EcgOverlayBeat>(maxBeats);
		for (int p = lastPeak - 1; p >= 0 && completed.Count < maxBeats; p--)
		{
			EcgOverlayBeat? beat = SliceBeat(samples, peaks[p], halfSamples, rate, completed.Count, requireFullPost: true);
			if (beat is not null)
			{
				completed.Add(beat);
			}
		}

		completed.Reverse(); // oldest first, newest (Age 0) last

		// Live beat: the latest peak's lead-in plus whatever samples have arrived since — drawn bright as
		// it sweeps, with no full-post requirement so it appears the instant the peak is detected.
		EcgOverlayBeat? live = SliceBeat(samples, peaks[lastPeak], halfSamples, rate, age: 0, requireFullPost: false);

		return new EcgBeatOverlay(
			completed,
			live,
			half,
			snapshot.MinMicroVolts,
			snapshot.MaxMicroVolts,
			rate,
			snapshot.Quality);
	}

	// Slices a window centred on a peak. Requires a full lead-in; the trailing half is required only for
	// completed beats (the live beat draws whatever has arrived so far, clipped to the axis by the renderer).
	private static EcgOverlayBeat? SliceBeat(
		IReadOnlyList<int> samples, int peak, int halfSamples, double rate, int age, bool requireFullPost)
	{
		int start = peak - halfSamples;
		if (start < 0)
		{
			return null; // not enough lead-in before the peak
		}

		int end = peak + halfSamples;
		if (requireFullPost)
		{
			if (end >= samples.Count)
			{
				return null; // not enough trailing data for a complete beat
			}
		}
		else
		{
			end = Math.Min(end, samples.Count - 1);
		}

		if (end <= start)
		{
			return null;
		}

		var pts = new List<EcgBeatSample>(end - start + 1);
		for (int i = start; i <= end; i++)
		{
			pts.Add(new EcgBeatSample((i - peak) / rate, samples[i]));
		}

		return new EcgOverlayBeat(pts, age);
	}

	// Median RR across the visible peaks, in seconds, clamped to a physiological 30–180 bpm. Falls back to
	// a resting ~75 bpm window when only one peak is in view (so the axis still has a sensible width).
	private static double MedianRrSeconds(IReadOnlyList<int> peaks, double rate)
	{
		if (peaks.Count < 2)
		{
			return 0.8;
		}

		var rr = new int[peaks.Count - 1];
		for (int i = 1; i < peaks.Count; i++)
		{
			rr[i - 1] = peaks[i] - peaks[i - 1];
		}

		Array.Sort(rr);
		double medianSamples = rr.Length % 2 == 1
			? rr[rr.Length / 2]
			: (rr[(rr.Length / 2) - 1] + rr[rr.Length / 2]) / 2.0;

		return Math.Clamp(medianSamples / rate, 0.333, 2.0);
	}
}
