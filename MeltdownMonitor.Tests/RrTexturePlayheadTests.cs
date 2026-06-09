using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

/// <summary>
/// The Regulation Field's live RR texture scrolls across the lemniscate lobes. The data that
/// drives it (beats) arrives from BLE in irregular batches — several RR intervals stamped at one
/// instant, then a gap — so a scroll tied to beat-arrival timing stutters. <see cref="RrTexturePlayhead"/>
/// is a free-running playhead that dead-reckons at the real beat rate and is gently corrected
/// toward the newest sample, so the visual flows smoothly regardless of how the data lands.
/// </summary>
[TestClass]
public class RrTexturePlayheadTests
{
	private const double Fps = 60.0;
	private const double Frame = 1.0 / Fps;

	[TestMethod]
	public void Advance_SeedsToTrailNewestOnFirstCall()
	{
		// The buffer already holds history (newest sample index = 100). The first frame should
		// snap the playhead to just behind the newest sample, not ease up from zero across the
		// whole buffer.
		var p = new RrTexturePlayhead();
		p.Advance(Frame, samplesPerSecond: 1.0, newestSampleIndex: 100.0);

		Assert.IsTrue(p.Position <= 100.0 + 1e-9, $"playhead must not start past the newest sample, was {p.Position}");
		Assert.IsTrue(p.Position >= 96.0, $"playhead should seed close behind the newest sample, was {p.Position}");
	}

	[TestMethod]
	public void Advance_FlowsSmoothlyWhenTargetArrivesInBatches()
	{
		// Simulate the real BLE pattern: ~1 beat/s on average, but delivered in batches (two at
		// once) with gaps — exactly what stutters a beat-arrival-driven scroll. The playhead is
		// advanced once per render frame (60 fps) with a steady sample rate.
		var p = new RrTexturePlayhead();

		double prev = double.NaN;
		double maxStep = 0.0;
		double minStep = double.MaxValue;

		for (int frame = 0; frame <= 720; frame++) // 12 s at 60 fps
		{
			double t = frame * Frame;
			double newest = NewestAt(t);
			p.Advance(Frame, samplesPerSecond: 1.0, newestSampleIndex: newest);

			if (!double.IsNaN(prev))
			{
				double step = p.Position - prev;
				maxStep = Math.Max(maxStep, step);
				minStep = Math.Min(minStep, step);
			}

			prev = p.Position;
		}

		// A beat-arrival-driven scroll would jump ~1 whole sample when a batch lands. A smooth
		// playhead advances ~1/60 sample per frame with only a tiny correction ripple — never a
		// jump. This is the anti-stutter assertion.
		Assert.IsTrue(maxStep < 0.1, $"per-frame advance should stay tiny (no stutter), max was {maxStep}");

		// And it only ever flows forward — no backward scrubbing.
		Assert.IsTrue(minStep >= -1e-9, $"playhead should never scroll backward, min step was {minStep}");

		// It stayed locked to the data: by the end it sits just behind the final newest (112).
		Assert.IsTrue(p.Position is >= 108.0 and <= 112.0 + 1e-9,
			$"playhead should track the newest sample (≈110), was {p.Position}");
	}

	[TestMethod]
	public void Advance_StaysLockedToASteadilyAdvancingTarget()
	{
		// One beat per second, delivered evenly. Over a full minute the playhead must neither
		// drift away from nor lag unboundedly behind the data.
		var p = new RrTexturePlayhead();
		double newest = 0.0;
		double maxOffset = 0.0;

		for (int frame = 0; frame <= 60 * 60; frame++) // 60 s
		{
			double t = frame * Frame;
			newest = Math.Floor(t); // a fresh sample lands every whole second
			p.Advance(Frame, samplesPerSecond: 1.0, newestSampleIndex: newest);

			if (t >= 3.0) // after warm-up
			{
				maxOffset = Math.Max(maxOffset, Math.Abs(newest - p.Position));
			}
		}

		Assert.IsTrue(maxOffset < 4.0, $"playhead should stay within a few samples of the data, drifted {maxOffset}");
	}

	[TestMethod]
	public void Advance_DoesNotRunPastNewestDuringADropout()
	{
		// Beats stop arriving (contact loss). The free-running dead-reckon must not scroll off
		// into never-arriving data — it parks at the freshest sample and resumes when beats return.
		var p = new RrTexturePlayhead();
		p.Advance(Frame, 1.0, 50.0); // seed

		const double frozenNewest = 50.0;
		for (int frame = 0; frame < 600; frame++) // 10 s with no new beats
		{
			p.Advance(Frame, samplesPerSecond: 1.0, newestSampleIndex: frozenNewest);
			Assert.IsTrue(p.Position <= frozenNewest + 1e-9,
				$"playhead must never scroll past the newest sample, was {p.Position}");
		}

		// Dead-reckon pushes it up to (and parks at) the freshest sample during the dropout.
		Assert.AreEqual(frozenNewest, p.Position, 1e-6);
	}

	[TestMethod]
	public void Advance_HoldsPositionOnNonPositiveOrNonFiniteDelta()
	{
		var p = new RrTexturePlayhead();
		p.Advance(Frame, 1.0, 100.0); // seed
		double pos = p.Position;

		p.Advance(0.0, 1.0, 200.0);
		p.Advance(-1.0, 1.0, 200.0);
		p.Advance(double.NaN, 1.0, 200.0);
		p.Advance(double.PositiveInfinity, 1.0, 200.0);

		Assert.AreEqual(pos, p.Position, 1e-12);
	}

	[TestMethod]
	public void Advance_HoldsPositionOnNonFiniteNewest()
	{
		var p = new RrTexturePlayhead();
		p.Advance(Frame, 1.0, 100.0); // seed
		double pos = p.Position;

		p.Advance(Frame, 1.0, double.NaN);

		Assert.AreEqual(pos, p.Position, 1e-12);
	}

	[TestMethod]
	public void Advance_NeverProducesNonFinitePositionForBadRate()
	{
		var p = new RrTexturePlayhead();
		p.Advance(Frame, 1.0, 100.0); // seed

		p.Advance(Frame, double.NaN, 100.0);
		p.Advance(Frame, double.NegativeInfinity, 100.0);
		p.Advance(Frame, -5.0, 100.0);

		Assert.IsTrue(double.IsFinite(p.Position), $"position must stay finite, was {p.Position}");
	}

	[TestMethod]
	public void Reset_SnapsToLiveEdgeInsteadOfFastForwardingAfterAHiddenGap()
	{
		// The field is on-screen and locked to the data at sample 50.
		var p = new RrTexturePlayhead();
		p.Advance(Frame, samplesPerSecond: 1.0, newestSampleIndex: 50.0); // seed
		for (int frame = 0; frame < 120; frame++)
		{
			p.Advance(Frame, samplesPerSecond: 1.0, newestSampleIndex: 50.0);
		}

		// The Now tab is hidden: the render loop stops (no Advance calls) while ~4 minutes of beats
		// arrive in the background, so the newest sample races far ahead of the frozen playhead.
		const double newestAfterGap = 300.0;
		double frozen = p.Position;
		Assert.IsTrue(frozen <= 51.0, $"playhead should still be parked near 50 while hidden, was {frozen}");

		// Without a reset, the first frame back would barely move (gentle catch-up) and then crawl
		// forward across the whole 250-sample gap — the visible "fast-forward through buffered jitter".
		var noReset = p;
		noReset.Advance(Frame, samplesPerSecond: 1.0, newestSampleIndex: newestAfterGap);
		Assert.IsTrue(noReset.Position < 60.0,
			$"sanity: un-reset playhead crawls from the stale position, was {noReset.Position}");

		// Re-seeding on the way back snaps it straight to just behind the live edge, so the texture
		// reads as having advanced continuously rather than fast-forwarding to catch up.
		p.Reset();
		p.Advance(Frame, samplesPerSecond: 1.0, newestSampleIndex: newestAfterGap);
		Assert.IsTrue(p.Position <= newestAfterGap + 1e-9,
			$"playhead must not start past the newest sample, was {p.Position}");
		Assert.IsTrue(p.Position >= newestAfterGap - 4.0,
			$"playhead should snap close behind the live edge after reset, was {p.Position}");
	}

	// Cumulative newest-sample index over time: ~1 beat/s, but delivered in batches of two with
	// gaps, starting from an existing history of 100 samples.
	private static double NewestAt(double t)
	{
		// (deliverySecond, cumulativeNewestIndex)
		(double sec, double cum)[] schedule =
		[
			(1, 101), (2, 102), (3, 103), (4, 105), (5, 105), (6, 106),
			(7, 107), (8, 109), (9, 109), (10, 110), (11, 111), (12, 112),
		];

		double newest = 100.0; // pre-existing history
		foreach (var (sec, cum) in schedule)
		{
			if (t >= sec)
			{
				newest = cum;
			}
		}

		return newest;
	}
}
