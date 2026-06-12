using MeltdownMonitor.Core.Motion;

namespace MeltdownMonitor.Tests;

[TestClass]
public class HrMotionContextClassifierTests
{
	private static readonly DateTimeOffset T0 = new(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);

	[TestMethod]
	public void Unknown_BeforeAnyUpdate()
	{
		var classifier = new HrMotionContextClassifier();
		Assert.AreEqual(HrMotionContext.Unknown, classifier.ContextAt(T0));
	}

	[TestMethod]
	public void Active_AtWalkingLevelAndAbove()
	{
		var classifier = new HrMotionContextClassifier();

		classifier.Update(T0, MovementLevel.Moderate);
		Assert.AreEqual(HrMotionContext.Active, classifier.ContextAt(T0));

		classifier.Update(T0.AddSeconds(10), MovementLevel.Vigorous);
		Assert.AreEqual(HrMotionContext.Active, classifier.ContextAt(T0.AddSeconds(10)));
	}

	[TestMethod]
	public void Still_IsNotSedentary_UntilThreshold()
	{
		var classifier = new HrMotionContextClassifier();

		var last = Feed(classifier, T0, TimeSpan.FromMinutes(4), MovementLevel.Still);
		Assert.AreEqual(HrMotionContext.Unknown, classifier.ContextAt(last),
			"Four minutes of stillness is not yet a resting reading.");

		last = Feed(classifier, last.AddSeconds(10), TimeSpan.FromMinutes(2), MovementLevel.Still);
		Assert.AreEqual(HrMotionContext.Sedentary, classifier.ContextAt(last));
	}

	[TestMethod]
	public void Light_FidgetingStillCountsAsSedentary()
	{
		var classifier = new HrMotionContextClassifier();

		// Alternate Still/Light (typing at a desk) for six minutes — still a resting HR.
		var time = T0;
		for (int i = 0; i < 37; i++)
		{
			classifier.Update(time, i % 2 == 0 ? MovementLevel.Still : MovementLevel.Light);
			time = time.AddSeconds(10);
		}

		Assert.AreEqual(HrMotionContext.Sedentary, classifier.ContextAt(time.AddSeconds(-10)));
	}

	[TestMethod]
	public void Movement_RestartsTheStillRun()
	{
		var classifier = new HrMotionContextClassifier();

		var last = Feed(classifier, T0, TimeSpan.FromMinutes(6), MovementLevel.Still);
		Assert.AreEqual(HrMotionContext.Sedentary, classifier.ContextAt(last));

		// A walk to the kitchen — active now, and the rest clock starts over.
		var walk = last.AddSeconds(10);
		classifier.Update(walk, MovementLevel.Moderate);
		Assert.AreEqual(HrMotionContext.Active, classifier.ContextAt(walk));

		last = Feed(classifier, walk.AddSeconds(10), TimeSpan.FromMinutes(2), MovementLevel.Still);
		Assert.AreEqual(HrMotionContext.Unknown, classifier.ContextAt(last),
			"Two minutes back in the chair is not yet rest again.");

		last = Feed(classifier, last.AddSeconds(10), TimeSpan.FromMinutes(4), MovementLevel.Still);
		Assert.AreEqual(HrMotionContext.Sedentary, classifier.ContextAt(last));
	}

	[TestMethod]
	public void UnknownLevel_NeverClaims_AndResetsTheRun()
	{
		var classifier = new HrMotionContextClassifier();

		var last = Feed(classifier, T0, TimeSpan.FromMinutes(6), MovementLevel.Still);
		Assert.AreEqual(HrMotionContext.Sedentary, classifier.ContextAt(last));

		// Motion source drops out: no claim, and the accumulated run is discarded.
		var dropout = last.AddSeconds(10);
		classifier.Update(dropout, MovementLevel.Unknown);
		Assert.AreEqual(HrMotionContext.Unknown, classifier.ContextAt(dropout));

		last = Feed(classifier, dropout.AddSeconds(10), TimeSpan.FromMinutes(2), MovementLevel.Still);
		Assert.AreEqual(HrMotionContext.Unknown, classifier.ContextAt(last));
	}

	[TestMethod]
	public void StaleStream_DegradesToUnknown()
	{
		var classifier = new HrMotionContextClassifier();

		var last = Feed(classifier, T0, TimeSpan.FromMinutes(6), MovementLevel.Still);
		Assert.AreEqual(HrMotionContext.Sedentary, classifier.ContextAt(last));
		Assert.AreEqual(HrMotionContext.Unknown,
			classifier.ContextAt(last + classifier.Staleness + TimeSpan.FromSeconds(1)));
	}

	[TestMethod]
	public void GapInUpdates_RestartsTheStillRun()
	{
		var classifier = new HrMotionContextClassifier();

		var last = Feed(classifier, T0, TimeSpan.FromMinutes(4), MovementLevel.Still);

		// The stream goes quiet for ten minutes, then resumes still. We can't vouch for
		// the hole, so the run restarts — two more minutes of stillness is not rest yet.
		var resume = last.AddMinutes(10);
		last = Feed(classifier, resume, TimeSpan.FromMinutes(2), MovementLevel.Still);
		Assert.AreEqual(HrMotionContext.Unknown, classifier.ContextAt(last));

		last = Feed(classifier, last.AddSeconds(10), TimeSpan.FromMinutes(4), MovementLevel.Still);
		Assert.AreEqual(HrMotionContext.Sedentary, classifier.ContextAt(last));
	}

	[TestMethod]
	public void Reset_GoesColdEverywhere()
	{
		var classifier = new HrMotionContextClassifier();

		var last = Feed(classifier, T0, TimeSpan.FromMinutes(6), MovementLevel.Still);
		Assert.AreEqual(HrMotionContext.Sedentary, classifier.ContextAt(last));

		classifier.Reset();
		Assert.AreEqual(HrMotionContext.Unknown, classifier.ContextAt(last));
	}

	/// <summary>Feeds <paramref name="level"/> every 10 s for <paramref name="duration"/>; returns the last timestamp fed.</summary>
	private static DateTimeOffset Feed(
		HrMotionContextClassifier classifier, DateTimeOffset from, TimeSpan duration, MovementLevel level)
	{
		var time = from;
		var end = from + duration;
		while (true)
		{
			classifier.Update(time, level);
			if (time >= end)
			{
				return time;
			}

			time = time.AddSeconds(10);
		}
	}
}
