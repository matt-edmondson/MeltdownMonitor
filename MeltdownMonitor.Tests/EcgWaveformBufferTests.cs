using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class EcgWaveformBufferTests
{
	private static EcgSamples Batch(int[] microVolts, double rateHz, params int[] peaks) =>
		new(DateTimeOffset.UtcNow, microVolts, rateHz, peaks);

	[TestMethod]
	public void Empty_SnapshotIsEmptyAndUnknown()
	{
		var snap = new EcgWaveformBuffer().Snapshot();
		Assert.AreEqual(0, snap.MicroVolts.Count);
		Assert.AreEqual(EcgSignalQuality.Unknown, snap.Quality);
	}

	[TestMethod]
	public void Append_ExposesSamplesPeaksAndScale()
	{
		var buffer = new EcgWaveformBuffer();
		buffer.Append(Batch([10, -20, 30], 10.0, 2));

		var snap = buffer.Snapshot();
		CollectionAssert.AreEqual(new[] { 10, -20, 30 }, snap.MicroVolts.ToArray());
		CollectionAssert.AreEqual(new[] { 2 }, snap.RPeakIndices.ToArray());
		Assert.AreEqual(-20, snap.MinMicroVolts);
		Assert.AreEqual(30, snap.MaxMicroVolts);
		Assert.AreEqual(10.0, snap.SampleRateHz, 1e-9);
	}

	[TestMethod]
	public void Append_EvictsBeyondWindowAndDropsOffscreenPeaks()
	{
		// 1-second window at 10 Hz ⇒ capacity 10.
		var buffer = new EcgWaveformBuffer(windowSeconds: 1.0);
		buffer.Append(Batch([.. Enumerable.Range(0, 8)], 10.0, 0));   // peak at absolute index 0
		buffer.Append(Batch([.. Enumerable.Range(8, 8)], 10.0, 4));   // peak at absolute index 12

		var snap = buffer.Snapshot();
		Assert.AreEqual(10, snap.MicroVolts.Count, "Only the last 10 samples are retained.");
		CollectionAssert.AreEqual(new[] { 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 }, snap.MicroVolts.ToArray());
		// Absolute peak 0 scrolled off; absolute peak 12 maps to window index 12-6 = 6.
		CollectionAssert.AreEqual(new[] { 6 }, snap.RPeakIndices.ToArray());
	}

	[TestMethod]
	public void Quality_FlatlineIsPoor()
	{
		var buffer = new EcgWaveformBuffer();
		buffer.Append(Batch([.. Enumerable.Repeat(0, 200)], 130.0)); // flat, no peaks
		Assert.AreEqual(EcgSignalQuality.Poor, buffer.Snapshot().Quality);
	}

	[TestMethod]
	public void Quality_PlausibleTraceIsGood()
	{
		var buffer = new EcgWaveformBuffer();
		// 200 samples at 130 Hz ≈ 1.54 s; alternating ±100 µV (range 200 > flatline); two peaks ⇒ ~78 bpm.
		int[] samples = [.. Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? 100 : -100)];
		buffer.Append(Batch(samples, 130.0, 40, 120));
		Assert.AreEqual(EcgSignalQuality.Good, buffer.Snapshot().Quality);
	}

	[TestMethod]
	public void Reset_ClearsBuffer()
	{
		var buffer = new EcgWaveformBuffer();
		buffer.Append(Batch([1, 2, 3], 130.0, 1));
		buffer.Reset();

		Assert.IsFalse(buffer.HasData);
		Assert.AreEqual(0, buffer.Snapshot().MicroVolts.Count);
	}
}
