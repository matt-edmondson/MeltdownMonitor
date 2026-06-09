namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// A smooth, free-running playhead over an absolute beat-sample timeline, used to scroll the
/// Regulation Field's live RR texture across the lemniscate lobes.
///
/// Beats arrive from BLE in irregular batches — a single notification can carry several RR
/// intervals stamped at one instant, followed by a gap — so any scroll tied to beat-<i>arrival</i>
/// timing stutters (it jumps when a batch lands and freezes in the gap). Instead this dead-reckons
/// forward at the real beat rate (constant velocity → smooth flow, like the marker's pulse halo) and
/// is <i>gently</i> corrected toward the newest sample so it stays locked to the data without ever
/// resetting per beat. The window it drives trails the newest sample by a small lag, and never
/// scrolls past it, so fresh data always flows in at the edge and a dropout simply parks the
/// playhead at the freshest sample until beats resume.
/// </summary>
public struct RrTexturePlayhead
{
	// The window trails the newest real sample by a couple of samples, so there is always fresh
	// data to flow in at the leading edge (and the seam — pinned to the lobe crossover — has room).
	private const double LagSamples = 2.0;

	// Per-second rate at which the playhead is trimmed toward the data. Deliberately gentle: the
	// dead-reckon term supplies the smooth velocity, so this only has to cancel slow drift (a small
	// HR-estimate error, or a batch landing early/late). Too strong and it would chase the stepped
	// arrival timing and reintroduce the stutter it exists to remove.
	private const double CatchUpRate = 0.15;

	// Cap a single advance so a long stall (e.g. the app was backgrounded) eases back toward the
	// data over a few frames instead of teleporting.
	private const double MaxStepSeconds = 0.1;

	private bool _seeded;

	/// <summary>The leading (newest) edge of the displayed window, in absolute sample-index units.</summary>
	public double Position { get; private set; }

	/// <summary>
	/// Advances the playhead by one render frame.
	/// </summary>
	/// <param name="dt">Frame time in seconds. Non-positive or non-finite values hold the playhead.</param>
	/// <param name="samplesPerSecond">The real beat rate (beats/s), i.e. mean HR / 60. Non-finite or
	/// non-positive values are treated as zero (correction-only).</param>
	/// <param name="newestSampleIndex">Absolute index of the newest beat in the buffer. Non-finite
	/// values (no data anchor yet) hold the playhead.</param>
	public void Advance(double dt, double samplesPerSecond, double newestSampleIndex)
	{
		if (!double.IsFinite(newestSampleIndex))
		{
			return; // No data to anchor to — hold.
		}

		double aim = newestSampleIndex - LagSamples;

		if (!_seeded)
		{
			// Snap behind the newest sample on the first frame rather than easing up from zero
			// across the whole buffer.
			Position = Math.Min(aim, newestSampleIndex);
			_seeded = true;
			return;
		}

		if (!double.IsFinite(dt) || dt <= 0.0)
		{
			return; // Paused / bad frame — hold.
		}

		dt = Math.Min(dt, MaxStepSeconds);
		double sps = double.IsFinite(samplesPerSecond) && samplesPerSecond > 0.0 ? samplesPerSecond : 0.0;

		// Dead-reckon forward at the real beat rate, then gently trim toward the data.
		double next = Position + (dt * sps);
		next += (aim - next) * (1.0 - Math.Exp(-dt * CatchUpRate));

		// Never scroll past the newest real sample; during a dropout the dead-reckon parks here.
		Position = Math.Min(next, newestSampleIndex);
	}

	/// <summary>
	/// Clears the playhead so the next <see cref="Advance"/> re-seeds just behind the newest sample
	/// rather than easing up from the last (now stale) position.
	///
	/// Call this when the view resumes after being hidden. The render loop stops while the field is
	/// off-screen, freezing <see cref="Position"/>, but beats keep arriving — so the newest sample
	/// index races far ahead. Without a reset the gentle catch-up would visibly "fast-forward" the
	/// playhead through every buffered beat to close that gap; re-seeding instead snaps it straight
	/// to the live edge, so the texture reads as having advanced continuously in the background.
	/// </summary>
	public void Reset()
	{
		_seeded = false;
		Position = 0.0;
	}
}
