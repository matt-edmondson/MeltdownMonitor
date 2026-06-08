namespace MeltdownMonitor.Core.Hrv;

/// <summary>One sample of an overlaid beat: microvolts at a signed time offset from that beat's R-peak (0 = the peak).</summary>
/// <param name="OffsetSeconds">Time relative to the beat's R-peak; negative is before the peak, positive after.</param>
/// <param name="MicroVolts">The sample amplitude.</param>
public readonly record struct EcgBeatSample(double OffsetSeconds, double MicroVolts);

/// <summary>
/// One cardiac cycle for the overlay view. <see cref="Age"/> is 0 for the most recent completed beat
/// and grows with each older beat — the renderer maps it to a diminishing alpha so the stacked traces
/// fade into the past. <see cref="IntervalSeconds"/> is this beat's RR (the gap from the previous
/// R-peak); the renderer offsets the beat horizontally by how far that RR sits from the reference
/// cadence, so the fading stack shows how early or late each beat arrived relative to the others.
/// </summary>
/// <param name="Samples">The beat's samples, oldest-first, with offsets relative to its R-peak.</param>
/// <param name="Age">0 = most recent completed beat; larger = older.</param>
/// <param name="IntervalSeconds">RR interval preceding this beat's R-peak, in seconds; 0 when unknown (no prior peak).</param>
public sealed record EcgOverlayBeat(IReadOnlyList<EcgBeatSample> Samples, int Age, double IntervalSeconds);

/// <summary>
/// Immutable render model for the "stacked beats" ECG view. Re-slices the rolling waveform
/// (<see cref="EcgWaveformSnapshot"/>) into the last few cardiac cycles on a shared "one beat wide"
/// time axis. Within each beat the R-peak sits at offset 0; the renderer then shifts each beat
/// horizontally by (<see cref="EcgOverlayBeat.IntervalSeconds"/> − reference cadence), so the stacked,
/// fading traces reveal how early or late each beat arrived compared with the others (a time-domain
/// view of beat-to-beat variability). Platform-neutral and unit-tested; the fade and the reference
/// easing are render concerns owned by each head.
/// </summary>
/// <param name="Beats">Completed beats, oldest first, newest last (the newest carries <see cref="EcgOverlayBeat.Age"/> 0).</param>
/// <param name="Live">The still-incomplete current beat (samples from the latest R-peak onward), or null when none.</param>
/// <param name="HalfWindowSeconds">Half the axis width: the axis spans [-HalfWindowSeconds, +HalfWindowSeconds] around the reference cadence.</param>
/// <param name="ReferenceRrSeconds">Median RR across the visible beats — the cadence each beat's arrival is measured against.</param>
/// <param name="MinMicroVolts">Minimum sample across the retained window (for vertical auto-scaling).</param>
/// <param name="MaxMicroVolts">Maximum sample across the retained window.</param>
/// <param name="SampleRateHz">Sample rate of the trace.</param>
/// <param name="Quality">Signal-quality cue, carried through from the source snapshot.</param>
public sealed record EcgBeatOverlay(
	IReadOnlyList<EcgOverlayBeat> Beats,
	EcgOverlayBeat? Live,
	double HalfWindowSeconds,
	double ReferenceRrSeconds,
	double MinMicroVolts,
	double MaxMicroVolts,
	double SampleRateHz,
	EcgSignalQuality Quality)
{
	/// <summary>An empty overlay (no ECG streaming / nothing alignable yet).</summary>
	public static readonly EcgBeatOverlay Empty = new([], null, 0, 0, 0, 0, 0, EcgSignalQuality.Unknown);

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

		// The reference cadence is the median RR across the visible peaks — robust to a stray long/short
		// interval — and the window is one such cycle wide. Each beat's own RR is measured against this.
		double referenceRr = MedianRrSeconds(peaks, rate);
		double half = referenceRr / 2.0;
		int halfSamples = Math.Max(1, (int)Math.Round(half * rate));

		// The most recent peak anchors the still-incomplete "live" beat; the ones before it form the
		// completed, fading stack. Walk newest-to-oldest and keep up to maxBeats that have a full window.
		int lastPeak = peaks.Count - 1;
		var completed = new List<EcgOverlayBeat>(maxBeats);
		for (int p = lastPeak - 1; p >= 0 && completed.Count < maxBeats; p--)
		{
			double interval = p > 0 ? (peaks[p] - peaks[p - 1]) / rate : 0.0;
			EcgOverlayBeat? beat = SliceBeat(samples, peaks[p], halfSamples, rate, completed.Count, interval, requireFullPost: true);
			if (beat is not null)
			{
				completed.Add(beat);
			}
		}

		completed.Reverse(); // oldest first, newest (Age 0) last

		// Live beat: the latest peak's lead-in plus whatever samples have arrived since — drawn bright as
		// it sweeps, with no full-post requirement so it appears the instant the peak is detected.
		double liveInterval = lastPeak > 0 ? (peaks[lastPeak] - peaks[lastPeak - 1]) / rate : 0.0;
		EcgOverlayBeat? live = SliceBeat(samples, peaks[lastPeak], halfSamples, rate, age: 0, liveInterval, requireFullPost: false);

		// Vertical scale from the beats actually shown, robust to amplitude spikes: a single noise/motion
		// artifact would otherwise blow out a raw window min/max and squash the whole stack.
		(double robustMin, double robustMax) = RobustRange(completed, live);

		return new EcgBeatOverlay(
			completed,
			live,
			half,
			referenceRr,
			robustMin,
			robustMax,
			rate,
			snapshot.Quality);
	}

	// Median of each beat's own min/max across the stack (+ live), padded a little. Taking the median
	// over beats means one artifact beat can't drive the scale — it just clips off the top/bottom.
	private static (double Min, double Max) RobustRange(IReadOnlyList<EcgOverlayBeat> completed, EcgOverlayBeat? live)
	{
		var mins = new List<double>();
		var maxs = new List<double>();

		void Accumulate(EcgOverlayBeat beat)
		{
			if (beat.Samples.Count == 0)
			{
				return;
			}

			double mn = double.MaxValue;
			double mx = double.MinValue;
			foreach (EcgBeatSample s in beat.Samples)
			{
				if (s.MicroVolts < mn) { mn = s.MicroVolts; }
				if (s.MicroVolts > mx) { mx = s.MicroVolts; }
			}

			mins.Add(mn);
			maxs.Add(mx);
		}

		foreach (EcgOverlayBeat beat in completed)
		{
			Accumulate(beat);
		}

		if (live is not null)
		{
			Accumulate(live);
		}

		if (mins.Count == 0)
		{
			return (0, 0);
		}

		double lo = Median(mins);
		double hi = Median(maxs);
		if (hi < lo)
		{
			(lo, hi) = (hi, lo);
		}

		double pad = (hi - lo) * 0.08;
		return (lo - pad, hi + pad);
	}

	private static double Median(List<double> values)
	{
		values.Sort();
		int n = values.Count;
		return n % 2 == 1 ? values[n / 2] : (values[(n / 2) - 1] + values[n / 2]) / 2.0;
	}

	// Slices a window centred on a peak. Requires a full lead-in; the trailing half is required only for
	// completed beats (the live beat draws whatever has arrived so far, clipped to the axis by the renderer).
	private static EcgOverlayBeat? SliceBeat(
		IReadOnlyList<int> samples, int peak, int halfSamples, double rate, int age, double intervalSeconds, bool requireFullPost)
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

		return new EcgOverlayBeat(pts, age, intervalSeconds);
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
