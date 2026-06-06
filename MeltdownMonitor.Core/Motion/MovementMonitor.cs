using MeltdownMonitor.Core.Beats;

namespace MeltdownMonitor.Core.Motion;

/// <summary>
/// Coarse classification of how much the body (or device) is moving, derived from the
/// accelerometer. <see cref="Unknown"/> means no motion source is contributing — the sentinel
/// that keeps detection behaviour identical to the no-motion build.
/// </summary>
public enum MovementLevel
{
	/// <summary>No motion data available — never gates anything.</summary>
	Unknown = 0,

	/// <summary>Effectively at rest (sitting/lying still).</summary>
	Still = 1,

	/// <summary>Small movements — fidgeting, typing, gesturing. Not exertion.</summary>
	Light = 2,

	/// <summary>Walking-level activity that meaningfully confounds HRV.</summary>
	Moderate = 3,

	/// <summary>Vigorous activity — running, stairs.</summary>
	Vigorous = 4,
}

/// <summary>
/// Turns the high-rate accelerometer stream into a low-rate movement intensity and level.
///
/// Intensity is the RMS of acceleration magnitude about its own rolling mean over a short window —
/// i.e. the AC (dynamic) component. Subtracting the rolling mean removes the gravity/DC offset, so
/// the metric reads ~0 at rest whether or not the source includes gravity (raw accelerometer vs.
/// Android linear-acceleration), making it robust across the strap and device-IMU sources alike.
///
/// Movement is the dominant confounder for this app: exertion drops HRV and raises HR — the exact
/// signature of dysregulation — so this feeds a gate that defers alerts and freezes the baseline
/// while moving. Deterministic and time-driven (no wall clock), so it is fully unit-testable.
/// </summary>
public class MovementMonitor
{
	private readonly Queue<(DateTimeOffset Time, double Magnitude)> _window = new();
	private double _sum;
	private double _sumSquares;
	private DateTimeOffset? _lastStrapTime;

	/// <summary>Rolling window length over which intensity is computed.</summary>
	public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(5);

	/// <summary>
	/// How recently a strap sample must have arrived for device-IMU samples to be ignored. The
	/// strap sits on the torso and tracks the body directly, so when both sources feed (e.g. a Polar
	/// strap plus the phone IMU fallback running together), the strap wins and the coarser device
	/// IMU is suppressed until the strap goes quiet for this long.
	/// </summary>
	public TimeSpan StrapPreferenceWindow { get; set; } = TimeSpan.FromSeconds(3);

	/// <summary>Dynamic-acceleration RMS (g) at/above which movement counts as <see cref="MovementLevel.Light"/>.</summary>
	public double LightThresholdG { get; set; } = 0.02;

	/// <summary>RMS (g) at/above which movement counts as <see cref="MovementLevel.Moderate"/>.</summary>
	public double ModerateThresholdG { get; set; } = 0.08;

	/// <summary>RMS (g) at/above which movement counts as <see cref="MovementLevel.Vigorous"/>.</summary>
	public double VigorousThresholdG { get; set; } = 0.25;

	/// <summary>Source of the most recent sample, for UI/telemetry.</summary>
	public MotionSourceKind? LatestSource { get; private set; }

	/// <summary>Current dynamic-acceleration intensity (g RMS). Zero before any samples arrive.</summary>
	public double IntensityG { get; private set; }

	/// <summary>
	/// Current movement level. <see cref="MovementLevel.Unknown"/> until the window holds enough
	/// data to judge (so a single stray sample can't gate detection).
	/// </summary>
	public MovementLevel Level { get; private set; } = MovementLevel.Unknown;

	/// <summary>Adds an accelerometer sample and refreshes <see cref="IntensityG"/> / <see cref="Level"/>.</summary>
	public void Add(MotionSample sample)
	{
		if (sample.Source == MotionSourceKind.PolarStrap)
		{
			_lastStrapTime = sample.Timestamp;
		}
		else if (_lastStrapTime is { } strap && (sample.Timestamp - strap) < StrapPreferenceWindow)
		{
			// A strap is actively feeding; ignore the coarser device-IMU fallback sample.
			return;
		}

		LatestSource = sample.Source;
		double magnitude = sample.Magnitude;

		_window.Enqueue((sample.Timestamp, magnitude));
		_sum += magnitude;
		_sumSquares += magnitude * magnitude;

		DateTimeOffset cutoff = sample.Timestamp - Window;
		while (_window.Count > 0 && _window.Peek().Time < cutoff)
		{
			(_, double old) = _window.Dequeue();
			_sum -= old;
			_sumSquares -= old * old;
		}

		// Need at least a couple of samples spanning a meaningful slice of the window before the
		// reading is trustworthy; until then report Unknown so nothing is gated on noise.
		if (_window.Count < 2)
		{
			IntensityG = 0;
			Level = MovementLevel.Unknown;
			return;
		}

		int n = _window.Count;
		double mean = _sum / n;
		// Variance of magnitude about its rolling mean = E[x²] − E[x]²; its root is the AC RMS.
		double variance = Math.Max(0.0, (_sumSquares / n) - (mean * mean));
		IntensityG = Math.Sqrt(variance);
		Level = Classify(IntensityG);
	}

	private MovementLevel Classify(double intensityG)
	{
		if (intensityG >= VigorousThresholdG)
		{
			return MovementLevel.Vigorous;
		}

		if (intensityG >= ModerateThresholdG)
		{
			return MovementLevel.Moderate;
		}

		if (intensityG >= LightThresholdG)
		{
			return MovementLevel.Light;
		}

		return MovementLevel.Still;
	}

	/// <summary>Clears all state — call on disconnect so a stale window can't gate after a gap.</summary>
	public void Reset()
	{
		_window.Clear();
		_sum = 0;
		_sumSquares = 0;
		_lastStrapTime = null;
		IntensityG = 0;
		Level = MovementLevel.Unknown;
		LatestSource = null;
	}
}
