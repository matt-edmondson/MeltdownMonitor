namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Tracks the velocity (rate of change) of the arousal index across successive readings
/// and classifies it into an escalating / steady / de-escalating trend. Stateful: it
/// remembers the previous index and timestamp, EWMA-smooths the derivative, and applies a
/// deadband for the trend. Single-threaded; the owning pipeline updates it once per sample.
///
/// Tuning constants are initial estimates to validate against a live sensor (RR data is
/// batched, so real-time feel is only gated by the running app — see CLAUDE.md).
/// </summary>
public sealed class RegulationVelocityTracker
{
	// EWMA weight for the per-sample derivative (~2-sample memory at the 5 s emit cadence).
	private const double SmoothingAlpha = 0.5;
	// |velocity| (index-units/s) below this is treated as Steady — hysteresis around zero.
	private const double TrendDeadband = 0.01;
	// |velocity| that maps to full visual magnitude (~ baseline->saturate in ~20 s).
	private const double ReferenceSpeed = 0.05;
	// dt clamp, matching the desktop view's inter-sample interval clamp.
	private const double MinDtSeconds = 0.5;
	private const double MaxDtSeconds = 30.0;

	private bool _seeded;
	private double _prevIndex;
	private DateTimeOffset _prevTimestamp;
	private double _velocity;

	/// <summary>The latest computed dynamics. <see cref="RegulationDynamics.Steady"/> before the first update.</summary>
	public RegulationDynamics Latest { get; private set; } = RegulationDynamics.Steady;

	/// <summary>
	/// Folds a new reading index into the velocity estimate and returns the updated dynamics.
	/// The first call after construction or <see cref="Reset"/> only seeds the previous sample
	/// and returns <see cref="RegulationDynamics.Steady"/> — so the cold->warm jump in index
	/// (0 -> real value) never registers as a spurious spike. Non-finite inputs are ignored.
	/// </summary>
	public RegulationDynamics Update(double index, DateTimeOffset timestamp)
	{
		if (!double.IsFinite(index))
		{
			return Latest;
		}

		if (!_seeded)
		{
			_seeded = true;
			_prevIndex = index;
			_prevTimestamp = timestamp;
			_velocity = 0.0;
			Latest = RegulationDynamics.Steady;
			return Latest;
		}

		double dt = Math.Clamp((timestamp - _prevTimestamp).TotalSeconds, MinDtSeconds, MaxDtSeconds);
		double rawVelocity = (index - _prevIndex) / dt;
		_velocity = (SmoothingAlpha * rawVelocity) + ((1.0 - SmoothingAlpha) * _velocity);

		_prevIndex = index;
		_prevTimestamp = timestamp;

		RegulationTrend trend = _velocity > TrendDeadband ? RegulationTrend.Escalating
			: _velocity < -TrendDeadband ? RegulationTrend.DeEscalating
			: RegulationTrend.Steady;
		double normalizedSpeed = Math.Clamp(Math.Abs(_velocity) / ReferenceSpeed, 0.0, 1.0);

		Latest = new RegulationDynamics(_velocity, trend, normalizedSpeed);
		return Latest;
	}

	/// <summary>
	/// Forgets the previous sample so the next <see cref="Update"/> re-seeds (returns Steady)
	/// rather than computing a derivative across a gap — used when the sensor goes off-contact
	/// or the baseline is not yet warm, so the resumed stream doesn't produce a spurious spike.
	/// </summary>
	public void Reset()
	{
		_seeded = false;
		_prevIndex = 0.0;
		_velocity = 0.0;
		Latest = RegulationDynamics.Steady;
	}
}
