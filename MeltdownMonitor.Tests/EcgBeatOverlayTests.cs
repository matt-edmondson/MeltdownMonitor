using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class EcgBeatOverlayTests
{
	// Samples whose value equals their index, so we can assert which sample landed at a given offset.
	private static EcgWaveformSnapshot Snapshot(int sampleCount, int[] peaks, double rate = 130.0) =>
		new(
			MicroVolts: [.. Enumerable.Range(0, sampleCount)],
			RPeakIndices: peaks,
			MinMicroVolts: 0,
			MaxMicroVolts: sampleCount - 1,
			SampleRateHz: rate,
			Quality: EcgSignalQuality.Good);

	[TestMethod]
	public void Build_NoPeaks_IsEmpty()
	{
		EcgBeatOverlay overlay = EcgBeatOverlay.Build(Snapshot(520, []));
		Assert.IsFalse(overlay.HasBeats);
		Assert.AreSame(EcgBeatOverlay.Empty, overlay);
	}

	[TestMethod]
	public void Build_NoSampleRate_IsEmpty()
	{
		EcgBeatOverlay overlay = EcgBeatOverlay.Build(Snapshot(520, [130, 260], rate: 0));
		Assert.IsFalse(overlay.HasBeats);
	}

	[TestMethod]
	public void Build_SplitsCompletedBeatsFromTheLiveBeat()
	{
		// Three peaks 130 samples (1 s at 130 Hz) apart. The last is the live beat; the two before it,
		// each with a full window of data around them, are the completed stack.
		EcgBeatOverlay overlay = EcgBeatOverlay.Build(Snapshot(520, [130, 260, 390]));

		Assert.AreEqual(2, overlay.Beats.Count);
		Assert.IsNotNull(overlay.Live);

		// Median RR is 1 s ⇒ a half-window of ~0.5 s.
		Assert.AreEqual(0.5, overlay.HalfWindowSeconds, 0.01);
	}

	[TestMethod]
	public void Build_NewestCompletedBeatHasAgeZeroAndOlderAgesIncrease()
	{
		EcgBeatOverlay overlay = EcgBeatOverlay.Build(Snapshot(520, [130, 260, 390]));

		// Oldest first, newest last.
		Assert.AreEqual(1, overlay.Beats[0].Age);
		Assert.AreEqual(0, overlay.Beats[^1].Age);
	}

	[TestMethod]
	public void Build_AlignsEachBeatOnItsRPeakAtOffsetZero()
	{
		EcgBeatOverlay overlay = EcgBeatOverlay.Build(Snapshot(520, [130, 260, 390]));

		// The newest completed beat is centred on peak index 260; the sample at offset 0 is that peak.
		EcgOverlayBeat newest = overlay.Beats[^1];
		EcgBeatSample atPeak = newest.Samples.Single(s => Math.Abs(s.OffsetSeconds) < 1e-9);
		Assert.AreEqual(260.0, atPeak.MicroVolts, 1e-9);

		// And the window is symmetric: first sample is ~-0.5 s, last is ~+0.5 s from the peak.
		Assert.AreEqual(-overlay.HalfWindowSeconds, newest.Samples[0].OffsetSeconds, 0.01);
		Assert.AreEqual(overlay.HalfWindowSeconds, newest.Samples[^1].OffsetSeconds, 0.01);
	}

	[TestMethod]
	public void Build_DropsBeatsWithoutAFullLeadInWindow()
	{
		// First peak at index 10 has no room for a ~0.5 s lead-in (65 samples), so it can't be a beat.
		EcgBeatOverlay overlay = EcgBeatOverlay.Build(Snapshot(520, [10, 140, 270, 400]));

		// Peaks 140 and 270 are complete; 10 is dropped; 400 is the live beat.
		Assert.AreEqual(2, overlay.Beats.Count);
	}

	[TestMethod]
	public void Build_HonoursTheMaxBeatsCap()
	{
		int[] peaks = [.. Enumerable.Range(1, 12).Select(i => i * 130)]; // 12 evenly spaced peaks
		EcgBeatOverlay overlay = EcgBeatOverlay.Build(Snapshot(12 * 130 + 100, peaks, rate: 130.0), maxBeats: 4);

		Assert.AreEqual(4, overlay.Beats.Count);
		Assert.AreEqual(0, overlay.Beats[^1].Age);
		Assert.AreEqual(3, overlay.Beats[0].Age);
	}

	[TestMethod]
	public void Build_LiveBeatAppearsBeforeItsTrailingWindowFills()
	{
		// Last peak at 470 in a 520-long buffer: only ~50 trailing samples (< the 65-sample half-window),
		// so it can't be a completed beat — but it must still surface as the live beat.
		EcgBeatOverlay overlay = EcgBeatOverlay.Build(Snapshot(520, [210, 340, 470]));

		Assert.IsNotNull(overlay.Live);
		Assert.AreEqual(2, overlay.Beats.Count); // 210 and 340 completed
	}

	[TestMethod]
	public void Build_CarriesEachBeatsIntervalAndAReferenceCadence()
	{
		// Intervals: 130 then 160 samples — the last beat arrived late. The renderer turns these into
		// horizontal offsets so the stack shows how early/late each beat was.
		EcgBeatOverlay overlay = EcgBeatOverlay.Build(Snapshot(560, [130, 260, 420]));

		Assert.IsNotNull(overlay.Live);
		// Live beat's RR = (420-260)/130 s; the newest completed beat's RR = (260-130)/130 = 1.0 s.
		Assert.AreEqual((420 - 260) / 130.0, overlay.Live!.IntervalSeconds, 1e-9);
		Assert.AreEqual(1.0, overlay.Beats[^1].IntervalSeconds, 1e-9);

		// Reference cadence is the median RR (median of {130,160} samples = 145).
		Assert.AreEqual(145 / 130.0, overlay.ReferenceRrSeconds, 1e-9);
	}

	[TestMethod]
	public void Build_FirstBeatWithoutAPriorPeakHasNoInterval()
	{
		EcgBeatOverlay overlay = EcgBeatOverlay.Build(Snapshot(520, [130, 260, 390]));
		// The oldest retained beat (peak 130) has no peak before it in the window ⇒ interval 0 (not offset).
		Assert.AreEqual(0.0, overlay.Beats[0].IntervalSeconds, 1e-9);
	}

	[TestMethod]
	public void Build_VerticalScaleIsRobustToAnAmplitudeSpike()
	{
		var samples = new int[520]; // mostly flat baseline
		foreach (int p in new[] { 130, 260, 390 })
		{
			samples[p] = 100; // clean R-peaks ≈ 100 µV
		}

		samples[390] = 5000; // one beat carries a big artifact spike

		EcgBeatOverlay overlay = EcgBeatOverlay.Build(
			new EcgWaveformSnapshot(samples, [130, 260, 390], 0, 5000, 130.0, EcgSignalQuality.Good));

		// The 5000 µV spike must not drive the scale — the median beat amplitude (~100) wins.
		Assert.IsTrue(overlay.MaxMicroVolts < 1000, $"max was {overlay.MaxMicroVolts}");
	}

	[TestMethod]
	public void Build_CarriesTheLiveBeatSequenceFromTotalPeaks()
	{
		var snapshot = new EcgWaveformSnapshot(
			[.. Enumerable.Range(0, 520)], [130, 260, 390], 0, 519, 130.0, EcgSignalQuality.Good, TotalPeaks: 42);

		Assert.AreEqual(42, EcgBeatOverlay.Build(snapshot).LiveBeatSequence);
	}

	[TestMethod]
	public void Build_CarriesSampleRateAndQuality()
	{
		EcgBeatOverlay overlay = EcgBeatOverlay.Build(Snapshot(520, [130, 260, 390]));
		Assert.AreEqual(130.0, overlay.SampleRateHz, 1e-9);
		Assert.AreEqual(EcgSignalQuality.Good, overlay.Quality);
	}
}
