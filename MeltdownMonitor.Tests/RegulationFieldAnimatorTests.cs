using MeltdownMonitor.Mobile.Controls;

namespace MeltdownMonitor.Tests;

[TestClass]
public class RegulationFieldAnimatorTests
{
	[TestMethod]
	public void Step_EasesMarkerTowardTargetWithoutOvershooting()
	{
		var a = new RegulationFieldAnimator();

		// A single small step should move part-way toward the target, never past it.
		a.Step(0.033, 1.0, 70);
		Assert.IsTrue(a.MarkerPos > 0.0, "marker should advance toward the target");
		Assert.IsTrue(a.MarkerPos < 1.0, "marker should not reach the target in one frame");

		// Many frames converge on the target.
		for (int i = 0; i < 300; i++)
		{
			a.Step(0.033, 1.0, 70);
		}

		Assert.AreEqual(1.0, a.MarkerPos, 1e-3);
	}

	[TestMethod]
	public void Step_MarkerEasesTowardNegativeTargetToo()
	{
		var a = new RegulationFieldAnimator();
		for (int i = 0; i < 300; i++)
		{
			a.Step(0.033, -1.0, 60);
		}

		Assert.AreEqual(-1.0, a.MarkerPos, 1e-3);
	}

	[TestMethod]
	public void Step_ClampsLongGapsSoTheMarkerDoesNotTeleport()
	{
		var a = new RegulationFieldAnimator();

		// A 10-minute gap (app was backgrounded) is clamped to a single 0.1 s
		// step, so the marker only eases part-way rather than snapping.
		a.Step(600.0, 1.0, 70);

		Assert.IsTrue(a.MarkerPos < 0.6,
			$"clamped step should ease modestly, was {a.MarkerPos}");
	}

	[TestMethod]
	public void Step_IgnoresNonPositiveOrNonFiniteDelta()
	{
		var a = new RegulationFieldAnimator();
		a.Step(0.033, 0.5, 70);
		double pos = a.MarkerPos;
		double phase = a.BreathPhase;

		a.Step(0.0, 1.0, 70);
		a.Step(-1.0, 1.0, 70);
		a.Step(double.NaN, 1.0, 70);

		Assert.AreEqual(pos, a.MarkerPos);
		Assert.AreEqual(phase, a.BreathPhase);
	}

	[TestMethod]
	public void Step_NonFiniteTargetHoldsTheMarker()
	{
		var a = new RegulationFieldAnimator();
		a.Step(0.033, 0.5, 70);
		double pos = a.MarkerPos;

		a.Step(0.033, double.NaN, 70);

		Assert.AreEqual(pos, a.MarkerPos, 1e-9);
	}

	[TestMethod]
	public void BreathPhase_AdvancesFasterAtHigherHeartRate()
	{
		var fast = new RegulationFieldAnimator();
		var slow = new RegulationFieldAnimator();

		fast.Step(0.1, 0.0, 120);
		slow.Step(0.1, 0.0, 60);

		Assert.IsTrue(fast.BreathPhase > slow.BreathPhase,
			"a higher HR should breathe faster");
	}

	[TestMethod]
	public void BreathPhase_WrapsAndKeepsHaloPulseInBand()
	{
		var a = new RegulationFieldAnimator();
		for (int i = 0; i < 1000; i++)
		{
			a.Step(0.033, 0.0, 180);
			Assert.IsTrue(a.BreathPhase >= 0.0 && a.BreathPhase < Math.Tau);
			Assert.IsTrue(a.HaloPulse >= 1.0 - 0.18 - 1e-9 && a.HaloPulse <= 1.0 + 0.18 + 1e-9);
		}
	}

	[TestMethod]
	public void BreathPhase_FloorsCadenceWhenHeartRateMissing()
	{
		var absent = new RegulationFieldAnimator();
		var floor = new RegulationFieldAnimator();

		// Zero/NaN HR breathes at the same gentle floor as 40 bpm, not stalled.
		absent.Step(0.5, 0.0, 0);
		floor.Step(0.5, 0.0, 40);
		Assert.AreEqual(floor.BreathPhase, absent.BreathPhase, 1e-9);

		var nan = new RegulationFieldAnimator();
		nan.Step(0.5, 0.0, double.NaN);
		Assert.AreEqual(floor.BreathPhase, nan.BreathPhase, 1e-9);
	}

	[TestMethod]
	public void JitterOffset_IsZeroWithoutVariabilityQuality()
	{
		var a = new RegulationFieldAnimator();
		a.Step(0.033, 0.0, 70);

		Assert.AreEqual(0.0, a.JitterOffset(7, quality: 0.0, depth: 1.0), 1e-12);
	}

	[TestMethod]
	public void JitterOffset_ScalesWithQualityAndDepth()
	{
		var a = new RegulationFieldAnimator();
		// Advance so the sine term is non-zero.
		for (int i = 0; i < 5; i++)
		{
			a.Step(0.033, 0.0, 70);
		}

		double shallow = Math.Abs(a.JitterOffset(7, quality: 1.0, depth: 0.25));
		double deep = Math.Abs(a.JitterOffset(7, quality: 1.0, depth: 1.0));
		Assert.IsTrue(deep > shallow, "deeper into the lobe should jitter more");

		double weak = Math.Abs(a.JitterOffset(7, quality: 0.25, depth: 1.0));
		Assert.IsTrue(deep > weak, "higher variability quality should jitter more");
	}

	[TestMethod]
	public void Step_EasesDisplayedSpeedTowardTarget()
	{
		var a = new RegulationFieldAnimator();

		a.Step(0.033, 0.0, 70, targetSpeed: 1.0);
		Assert.IsTrue(a.DisplayedSpeed > 0.0 && a.DisplayedSpeed < 1.0,
			$"speed should advance part-way, was {a.DisplayedSpeed}");

		for (int i = 0; i < 300; i++)
		{
			a.Step(0.033, 0.0, 70, targetSpeed: 1.0);
		}

		Assert.AreEqual(1.0, a.DisplayedSpeed, 1e-3);
	}

	[TestMethod]
	public void Step_DisplayedSpeedEasesBackToZero()
	{
		var a = new RegulationFieldAnimator();
		for (int i = 0; i < 300; i++)
		{
			a.Step(0.033, 0.0, 70, targetSpeed: 1.0);
		}

		for (int i = 0; i < 300; i++)
		{
			a.Step(0.033, 0.0, 70, targetSpeed: 0.0);
		}

		Assert.AreEqual(0.0, a.DisplayedSpeed, 1e-3);
	}

	[TestMethod]
	public void Step_ClampsTargetSpeedToUnitRange()
	{
		var a = new RegulationFieldAnimator();
		for (int i = 0; i < 300; i++)
		{
			a.Step(0.033, 0.0, 70, targetSpeed: 2.0);   // over-range
		}

		Assert.AreEqual(1.0, a.DisplayedSpeed, 1e-3);
	}

	[TestMethod]
	public void Step_NonFiniteTargetSpeed_HoldsDisplayedSpeed()
	{
		var a = new RegulationFieldAnimator();
		a.Step(0.033, 0.0, 70, targetSpeed: 0.5);
		double held = a.DisplayedSpeed;

		a.Step(0.033, 0.0, 70, targetSpeed: double.NaN);

		Assert.AreEqual(held, a.DisplayedSpeed, 1e-9);
	}
}
