using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class ShortWindowHrvCalculatorTests
{
	private static Beat Beat(DateTimeOffset ts, double rr) => new(ts, rr, (int)Math.Round(60_000.0 / rr), IsArtifact: false);

	[TestMethod]
	public void DoesNotEmit_BeforeMinimumBeatCount()
	{
		var calc = new ShortWindowHrvCalculator { EmitIntervalSeconds = 0, MinBeatsForMetrics = 5 };
		var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

		// Four beats — one below the floor.
		HrvSample? last = null;
		for (int i = 0; i < 4; i++)
		{
			last = calc.AddBeat(Beat(start.AddMilliseconds(i * 800), 800), 50, 75, DetectorState.Watching);
		}

		Assert.IsNull(last, "Must not emit until the short window holds MinBeatsForMetrics beats.");
	}

	[TestMethod]
	public void Emits_OnceMinimumBeatCountReached()
	{
		var calc = new ShortWindowHrvCalculator { EmitIntervalSeconds = 0, MinBeatsForMetrics = 5 };
		var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

		HrvSample? last = null;
		for (int i = 0; i < 5; i++)
		{
			last = calc.AddBeat(Beat(start.AddMilliseconds(i * 800), 800), 50, 75, DetectorState.Watching);
		}

		Assert.IsNotNull(last, "Should emit once the floor is reached.");
	}

	[TestMethod]
	public void GapLongerThanThreshold_ResetsWindow_NoBridgingDifference()
	{
		// 800ms beats, then a long gap, then 600ms beats. Without a reset the first
		// post-gap difference (800→600) would inflate RMSSD. After the reset the window
		// rebuilds from the new level, so once it re-emits, RMSSD reflects 600ms beats only.
		var calc = new ShortWindowHrvCalculator { EmitIntervalSeconds = 0, MinBeatsForMetrics = 5, MaxBeatGapSeconds = 5 };
		var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

		for (int i = 0; i < 6; i++)
		{
			calc.AddBeat(Beat(start.AddMilliseconds(i * 800), 800), 50, 75, DetectorState.Watching);
		}

		// 30-second gap, then six identical 600ms beats.
		var resume = start.AddSeconds(30);
		HrvSample? last = null;
		for (int i = 0; i < 6; i++)
		{
			last = calc.AddBeat(Beat(resume.AddMilliseconds(i * 600), 600), 50, 75, DetectorState.Watching);
		}

		Assert.IsNotNull(last);
		Assert.AreEqual(0.0, last.Rmssd, 0.001, "RMSSD must reflect only post-gap (identical 600ms) beats.");
	}
}
