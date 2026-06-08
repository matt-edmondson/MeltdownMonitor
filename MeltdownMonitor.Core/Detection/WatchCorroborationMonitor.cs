using MeltdownMonitor.Core.Beats;

namespace MeltdownMonitor.Core.Detection;

/// <summary>
/// Verdict of cross-checking the chest strap's heart rate against the Apple Watch's. The
/// <see cref="Unknown"/> sentinel — no fresh watch reading — keeps detection identical to a
/// no-watch build, exactly like <see cref="Motion.MovementLevel.Unknown"/>.
/// </summary>
public enum WatchCorroboration
{
	/// <summary>No fresh/usable watch reading to compare against — never gates anything.</summary>
	Unknown = 0,

	/// <summary>Watch and strap heart rate agree within tolerance — the strap signal is corroborated.</summary>
	Confirmed = 1,

	/// <summary>Watch and strap heart rate disagree beyond tolerance — the strap signal is suspect.</summary>
	Conflicted = 2,
}

/// <summary>
/// Immutable snapshot of the corroboration monitor's latest verdict, for surfacing in the UI.
/// </summary>
/// <param name="Verdict">The current cross-check verdict.</param>
/// <param name="WatchHeartRateBpm">The watch heart rate the verdict used, or null when none was fresh.</param>
/// <param name="StrapHeartRateBpm">The strap heart rate the verdict used, or null before any sample.</param>
public record WatchCorroborationSnapshot(
	WatchCorroboration Verdict,
	double? WatchHeartRateBpm,
	double? StrapHeartRateBpm);

/// <summary>
/// Cross-checks the chest strap's heart rate against the Apple Watch's, producing a corroboration
/// verdict the detector can gate on (docs/watch-corroboration.md). Two heart-rate sensors on the
/// same body should agree; a sustained disagreement means one is wrong — most often the strap, when
/// motion or poor electrode contact spuriously collapses RMSSD into the dysregulation signature — so
/// the detector defers escalation on a <see cref="WatchCorroboration.Conflicted"/> verdict, mirroring
/// the movement gate.
///
/// The monitor holds only the latest watch reading; the verdict is computed against a strap reading
/// supplied at evaluation time and is driven entirely by sample timestamps (no wall clock), so it is
/// deterministic and replay-safe. Wrist-optical HR lags and smooths chest-ECG HR, so the tolerance
/// is generous: a missed alert is the more harmful error for an awareness tool, so only a large,
/// sustained disagreement is allowed to gate.
/// </summary>
public class WatchCorroborationMonitor
{
	private WatchMetricSample? _latest;
	private WatchCorroborationSnapshot _snapshot = new(WatchCorroboration.Unknown, null, null);

	/// <summary>
	/// Absolute heart-rate gap (bpm) at/above which the strap and watch are treated as
	/// <see cref="WatchCorroboration.Conflicted"/>. Generous by default to absorb the normal lag and
	/// smoothing of wrist-optical HR versus chest ECG — only a genuine sensor disagreement should gate.
	/// </summary>
	public double ConflictToleranceBpm { get; set; } = 12.0;

	/// <summary>
	/// How recent a watch reading must be (relative to the strap sample being evaluated) to count.
	/// A stale watch reading is ignored and the verdict falls back to <see cref="WatchCorroboration.Unknown"/>,
	/// so a watch that has gone quiet (out of range, app suspended) never gates.
	/// </summary>
	public TimeSpan Staleness { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>The most recent verdict, refreshed on each <see cref="Evaluate"/> call.</summary>
	public WatchCorroboration Verdict => _snapshot.Verdict;

	/// <summary>Immutable snapshot of the current corroboration state, for fan-out to the UI.</summary>
	public WatchCorroborationSnapshot Snapshot => _snapshot;

	/// <summary>Records the latest watch metric reading. Out-of-order (older) readings are ignored so
	/// a late-delivered batch can't replace a newer value.</summary>
	public void Add(WatchMetricSample sample)
	{
		if (_latest is { } current && sample.Timestamp < current.Timestamp)
		{
			return;
		}

		_latest = sample;
	}

	/// <summary>
	/// Cross-checks the strap heart rate against the freshest watch reading and refreshes the verdict.
	/// Returns <see cref="WatchCorroboration.Unknown"/> — which never gates — when there is no watch
	/// reading, the reading is stale relative to <paramref name="strapTimestamp"/>, the watch is
	/// off-wrist, or either heart rate is non-positive.
	/// </summary>
	/// <param name="strapHeartRateBpm">The strap-derived heart rate for this sample.</param>
	/// <param name="strapTimestamp">The strap sample's timestamp — the reference for staleness.</param>
	public WatchCorroboration Evaluate(double strapHeartRateBpm, DateTimeOffset strapTimestamp)
	{
		if (_latest is not { } watch
			|| watch.HeartRateBpm <= 0
			|| strapHeartRateBpm <= 0
			|| watch.Contact == SensorContactStatus.NotDetected
			|| strapTimestamp - watch.Timestamp > Staleness
			|| watch.Timestamp - strapTimestamp > Staleness)
		{
			_snapshot = new WatchCorroborationSnapshot(WatchCorroboration.Unknown, null,
				strapHeartRateBpm > 0 ? strapHeartRateBpm : null);
			return WatchCorroboration.Unknown;
		}

		var verdict = Math.Abs(watch.HeartRateBpm - strapHeartRateBpm) >= ConflictToleranceBpm
			? WatchCorroboration.Conflicted
			: WatchCorroboration.Confirmed;

		_snapshot = new WatchCorroborationSnapshot(verdict, watch.HeartRateBpm, strapHeartRateBpm);
		return verdict;
	}

	/// <summary>Clears all state — call on disconnect so a stale watch reading can't gate after a gap.</summary>
	public void Reset()
	{
		_latest = null;
		_snapshot = new WatchCorroborationSnapshot(WatchCorroboration.Unknown, null, null);
	}
}
