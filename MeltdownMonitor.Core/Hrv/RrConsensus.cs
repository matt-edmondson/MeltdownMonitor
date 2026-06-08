namespace MeltdownMonitor.Core.Hrv;

/// <summary>The cross-check verdict for one beat against the reference RR stream.</summary>
public enum ConsensusVerdict
{
	/// <summary>No fresh reference to compare against — never gates.</summary>
	Unknown = 0,

	/// <summary>The beat's RR agrees with the recent reference rhythm.</summary>
	Confirmed = 1,

	/// <summary>The beat's RR contradicts the reference rhythm — a likely detection error.</summary>
	Conflicted = 2,
}

/// <summary>
/// Cross-validates one RR stream against an independent reference stream of the same heart. On a Polar
/// H10 the standard Heart Rate Service RR is Polar's own validated, sub-millisecond on-device detection,
/// so it's the ideal reference for <i>our</i> ECG-derived RR: a beat we report that the reference rhythm
/// contradicts (a missed beat read as a doubled interval, a T-wave read as a halved one) is almost
/// certainly our error, and can be dropped from HRV with confidence the self-median filter can't offer
/// (the self-filter eventually adapts to a sustained miscount; an independent witness does not).
///
/// References and checks arrive on different threads (the diagnostics side channel runs on the BLE
/// thread, the beat loop on the pipeline task), so the type is thread-safe. Reference staleness gates it
/// off — no fresh reference yields <see cref="ConsensusVerdict.Unknown"/>, which never rejects — so a
/// device without a usable reference stream behaves exactly as before. Platform-neutral and unit-tested.
/// </summary>
public sealed class RrConsensus
{
	private readonly object _gate = new();
	private readonly int _referenceWindow;
	private readonly double _toleranceFraction;
	private readonly TimeSpan _staleness;
	private readonly int _verdictWindow;

	private readonly Queue<double> _reference = new();
	private DateTimeOffset _lastReferenceTime = DateTimeOffset.MinValue;
	private readonly Queue<bool> _recentVerdicts = new();
	private int _conflictCount;

	/// <param name="referenceWindow">Recent reference RR intervals kept for the rolling median.</param>
	/// <param name="toleranceFraction">Allowed |rr − referenceMedian| / referenceMedian before a beat conflicts.</param>
	/// <param name="staleness">A beat is only checked when a reference arrived within this window.</param>
	/// <param name="verdictWindow">Recent verdicts kept for <see cref="ConflictRate"/>.</param>
	public RrConsensus(
		int referenceWindow = 12,
		double toleranceFraction = 0.20,
		TimeSpan? staleness = null,
		int verdictWindow = 60)
	{
		_referenceWindow = Math.Max(3, referenceWindow);
		_toleranceFraction = Math.Max(0.0, toleranceFraction);
		_staleness = staleness ?? TimeSpan.FromSeconds(5);
		_verdictWindow = Math.Max(1, verdictWindow);
	}

	/// <summary>The verdict from the most recent <see cref="Check"/>.</summary>
	public ConsensusVerdict Latest { get; private set; } = ConsensusVerdict.Unknown;

	/// <summary>Fraction of recent checked beats (excluding <see cref="ConsensusVerdict.Unknown"/>) that conflicted (0–1).</summary>
	public double ConflictRate
	{
		get
		{
			lock (_gate)
			{
				return _recentVerdicts.Count == 0 ? 0.0 : (double)_conflictCount / _recentVerdicts.Count;
			}
		}
	}

	/// <summary>The reference rhythm's rolling median RR (ms), or null until enough reference beats exist.</summary>
	public double? ReferenceMedianRrMs
	{
		get
		{
			lock (_gate)
			{
				return _reference.Count >= 3 ? Median(_reference) : null;
			}
		}
	}

	/// <summary>Folds one reference (e.g. HRS) RR into the rolling window. Clean intervals only.</summary>
	public void AddReference(double rrMs, DateTimeOffset time)
	{
		if (rrMs <= 0)
		{
			return;
		}

		lock (_gate)
		{
			_reference.Enqueue(rrMs);
			while (_reference.Count > _referenceWindow)
			{
				_reference.Dequeue();
			}

			if (time > _lastReferenceTime)
			{
				_lastReferenceTime = time;
			}
		}
	}

	/// <summary>
	/// Cross-checks one beat's RR against the recent reference rhythm. Returns
	/// <see cref="ConsensusVerdict.Unknown"/> (no rejection) when the reference is too sparse or stale.
	/// </summary>
	public ConsensusVerdict Check(double rrMs, DateTimeOffset time)
	{
		lock (_gate)
		{
			if (_reference.Count < 3 || (time - _lastReferenceTime) > _staleness)
			{
				Latest = ConsensusVerdict.Unknown;
				return Latest;
			}

			double median = Median(_reference);
			bool conflict = median > 0 && (Math.Abs(rrMs - median) / median) > _toleranceFraction;
			Latest = conflict ? ConsensusVerdict.Conflicted : ConsensusVerdict.Confirmed;

			_recentVerdicts.Enqueue(conflict);
			if (conflict)
			{
				_conflictCount++;
			}

			while (_recentVerdicts.Count > _verdictWindow)
			{
				if (_recentVerdicts.Dequeue())
				{
					_conflictCount--;
				}
			}

			return Latest;
		}
	}

	/// <summary>Clears all state — call on disconnect so a stale reference can't gate after a gap.</summary>
	public void Reset()
	{
		lock (_gate)
		{
			_reference.Clear();
			_lastReferenceTime = DateTimeOffset.MinValue;
			_recentVerdicts.Clear();
			_conflictCount = 0;
			Latest = ConsensusVerdict.Unknown;
		}
	}

	private static double Median(Queue<double> values)
	{
		double[] sorted = [.. values];
		Array.Sort(sorted);
		int mid = sorted.Length / 2;
		return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
	}
}
