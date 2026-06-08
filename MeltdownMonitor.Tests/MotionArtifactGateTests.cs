using MeltdownMonitor.Core.Motion;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MotionArtifactGateTests
{
	[TestMethod]
	public void Unknown_NeverRejects()
	{
		Assert.IsFalse(MotionArtifactGate.IsArtifact(MovementLevel.Unknown, MovementLevel.Moderate));
		Assert.IsFalse(MotionArtifactGate.IsArtifact(MovementLevel.Unknown, MovementLevel.Still));
	}

	[TestMethod]
	public void BelowThreshold_DoesNotReject()
	{
		Assert.IsFalse(MotionArtifactGate.IsArtifact(MovementLevel.Still, MovementLevel.Moderate));
		Assert.IsFalse(MotionArtifactGate.IsArtifact(MovementLevel.Light, MovementLevel.Moderate));
	}

	[TestMethod]
	public void AtOrAboveThreshold_Rejects()
	{
		Assert.IsTrue(MotionArtifactGate.IsArtifact(MovementLevel.Moderate, MovementLevel.Moderate));
		Assert.IsTrue(MotionArtifactGate.IsArtifact(MovementLevel.Vigorous, MovementLevel.Moderate));
	}
}
