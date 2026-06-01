using System;

namespace MeltdownMonitor.Mobile.Controls;

/// <summary>
/// Pure, frame-driven animation state for the <see cref="RegulationField"/> —
/// the marker easing, the HR-paced breathing phase, and the free-running clock
/// that drives the trace's variability jitter. Mirrors the desktop renderer's
/// animation state (Regulation Field plan, Step 4) but lives in its own type so
/// the easing and cadence maths can be exercised without a render surface.
///
/// All members are UI-thread-only: the control owns a single instance and steps
/// it once per render tick.
/// </summary>
public sealed class RegulationFieldAnimator
{
	// Cap a single step so a long gap (app backgrounded, GC pause, tab torn
	// down and reattached) eases smoothly toward the new target instead of
	// teleporting the marker the moment the timer resumes.
	private const double MaxStepSeconds = 0.1;
	private const double MarkerEaseRate = 6.0;     // matches the desktop's exp ease
	private const double SpeedEaseRate = 6.0;      // matches the marker ease so the arrow grows/shrinks in step
	private const double JitterRate = 6.0;
	private const double JitterAmplitude = 1.5;    // px at full quality + depth and 1× exaggeration
	private const double BreathHalfAmplitude = 0.18;
	private const double MinBreathBpm = 40.0;      // a missing/low HR still breathes gently

	/// <summary>Marker position along the major axis, eased toward the latest
	/// reading's index so it glides between the multi-second samples.</summary>
	public double MarkerPos { get; private set; }

	/// <summary>Eased magnitude of the velocity arrow, glided toward the latest
	/// <c>RegulationDynamics.NormalizedSpeed</c> so it grows/shrinks smoothly.</summary>
	public double DisplayedSpeed { get; private set; }

	/// <summary>Breathing phase in radians (wrapped to <c>[0, 2π)</c>), advanced
	/// at the current HR cadence; drives the marker halo's gentle pulse.</summary>
	public double BreathPhase { get; private set; }

	/// <summary>Free-running clock in seconds driving the trace's variability
	/// jitter.</summary>
	public double AnimTime { get; private set; }

	/// <summary>User-configurable multiplier on the jitter amplitude (clamp 0–3 at
	/// the consumer). 1.0 is the tuned default; 0 flattens the trace, higher
	/// exaggerates the beat-to-beat undulation.</summary>
	public double JitterExaggeration { get; set; } = 1.0;

	/// <summary>
	/// Advance the animation by <paramref name="dt"/> seconds: ease the marker
	/// toward <paramref name="targetIndex"/> and breathe at
	/// <paramref name="heartRate"/> bpm (floored at 40). Non-positive or
	/// non-finite <paramref name="dt"/> is a no-op; long gaps are clamped so the
	/// marker never teleports.
	/// </summary>
	public void Step(double dt, double targetIndex, double heartRate, double targetSpeed = 0.0)
	{
		if (!double.IsFinite(dt) || dt <= 0.0)
		{
			return;
		}

		dt = Math.Min(dt, MaxStepSeconds);

		double target = double.IsFinite(targetIndex) ? targetIndex : MarkerPos;
		MarkerPos += (target - MarkerPos) * (1.0 - Math.Exp(-dt * MarkerEaseRate));

		double speedTarget = double.IsFinite(targetSpeed) ? Math.Clamp(targetSpeed, 0.0, 1.0) : DisplayedSpeed;
		DisplayedSpeed += (speedTarget - DisplayedSpeed) * (1.0 - Math.Exp(-dt * SpeedEaseRate));

		double bpm = Math.Max(MinBreathBpm, double.IsFinite(heartRate) ? heartRate : 0.0);
		BreathPhase = (BreathPhase + (dt * (bpm / 60.0) * Math.Tau)) % Math.Tau;

		AnimTime += dt;
	}

	/// <summary>Multiplier for the breathing halo radius: <c>1 ± 0.18</c> over
	/// the breath cycle.</summary>
	public double HaloPulse => 1.0 + (BreathHalfAmplitude * Math.Sin(BreathPhase));

	/// <summary>Perpendicular jitter offset in px for trace segment
	/// <paramref name="segmentIndex"/>, scaling with variability
	/// <paramref name="quality"/> and lobe <paramref name="depth"/> so the line
	/// looks "alive" only where there is healthy variability.</summary>
	public double JitterOffset(int segmentIndex, double quality, double depth) =>
		quality * JitterAmplitude * JitterExaggeration * Math.Sin((AnimTime * JitterRate) + (segmentIndex * 0.7)) * depth;
}
