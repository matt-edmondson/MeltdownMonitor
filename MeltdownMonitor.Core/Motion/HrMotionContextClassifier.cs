namespace MeltdownMonitor.Core.Motion;

/// <summary>
/// The activity context attached to a heart-rate sample written to a platform health store —
/// whether the reading was taken at rest or while moving. Mirrors HealthKit's heart-rate motion
/// context (<c>HKHeartRateMotionContext</c>): <see cref="Sedentary"/> / <see cref="Active"/>,
/// with <see cref="Unknown"/> meaning "don't claim anything" (no motion source feeding, a stale
/// stream, or still-but-not-long-enough). Unknown maps to the platform's not-set / no-write, so
/// a build with motion corroboration off records exactly what it did before.
/// </summary>
public enum HrMotionContext
{
	/// <summary>No claim — absent/stale motion data, or stillness too brief to call rest.</summary>
	Unknown = 0,

	/// <summary>At rest: still (or merely fidgeting) continuously for the sedentary threshold.</summary>
	Sedentary = 1,

	/// <summary>In motion at walking level or above when the sample was taken.</summary>
	Active = 2,
}

/// <summary>
/// Classifies what the body was doing when a heart-rate sample was taken — resting, active, or
/// unknown — from the <see cref="MovementLevel"/> stream the motion-corroboration pipeline
/// already produces. <see cref="MovementLevel.Moderate"/> is the walking threshold everywhere
/// else in detection, so it is the active threshold here too; <see cref="MovementLevel.Light"/>
/// (fidgeting, typing) still counts as sedentary, matching the everyday sense of "resting heart
/// rate at a desk". Sedentary additionally requires the stillness to have been continuous for
/// <see cref="SedentaryThreshold"/>, echoing Apple's definition (still for ~5 minutes before the
/// sample) — a reading thirty seconds after sitting down is not a resting heart rate.
///
/// Deterministic and timestamp-driven (no wall clock), so it is fully unit-testable and
/// replay-safe, like the rest of the motion stack. A gap in updates longer than
/// <see cref="Staleness"/> restarts the still run — we never vouch for time we didn't observe.
/// </summary>
public class HrMotionContextClassifier
{
	private DateTimeOffset? _lastUpdate;
	private DateTimeOffset? _stillSince;
	private MovementLevel _latest = MovementLevel.Unknown;

	/// <summary>
	/// How long the body must have been continuously still (≤ <see cref="MovementLevel.Light"/>)
	/// before a sample counts as <see cref="HrMotionContext.Sedentary"/>. Defaults to 5 minutes,
	/// Apple's own sedentary-context convention.
	/// </summary>
	public TimeSpan SedentaryThreshold { get; set; } = TimeSpan.FromMinutes(5);

	/// <summary>
	/// How recent the latest movement update must be for any claim at all; beyond this the
	/// context degrades to <see cref="HrMotionContext.Unknown"/>, and a gap this long between
	/// updates restarts the still run.
	/// </summary>
	public TimeSpan Staleness { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>Records the movement level observed at <paramref name="timestamp"/>.</summary>
	public void Update(DateTimeOffset timestamp, MovementLevel level)
	{
		// A hole in the movement stream means we can't vouch for what happened during it;
		// restart the still run rather than claim continuous rest across the gap.
		if (_lastUpdate is { } previous && timestamp - previous > Staleness)
		{
			_stillSince = null;
		}

		_lastUpdate = timestamp;
		_latest = level;

		if (level == MovementLevel.Unknown || level >= MovementLevel.Moderate)
		{
			_stillSince = null;
			return;
		}

		_stillSince ??= timestamp;
	}

	/// <summary>The context for a heart-rate sample taken at <paramref name="timestamp"/>.</summary>
	public HrMotionContext ContextAt(DateTimeOffset timestamp)
	{
		if (_lastUpdate is not { } last || timestamp - last > Staleness)
		{
			return HrMotionContext.Unknown;
		}

		if (_latest >= MovementLevel.Moderate)
		{
			return HrMotionContext.Active;
		}

		if (_latest != MovementLevel.Unknown
			&& _stillSince is { } since
			&& timestamp - since >= SedentaryThreshold)
		{
			return HrMotionContext.Sedentary;
		}

		return HrMotionContext.Unknown;
	}

	/// <summary>Clears all state — call on disconnect so a stale run can't claim rest after a gap.</summary>
	public void Reset()
	{
		_lastUpdate = null;
		_stillSince = null;
		_latest = MovementLevel.Unknown;
	}
}
