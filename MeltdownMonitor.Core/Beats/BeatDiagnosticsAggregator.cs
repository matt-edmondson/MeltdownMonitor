namespace MeltdownMonitor.Core.Beats;

/// <summary>Rolling per-source statistics for one interval stream, for the debug view.</summary>
/// <param name="Source">Which stream.</param>
/// <param name="Count">Total intervals seen since the last reset.</param>
/// <param name="ArtifactCount">How many of those were flagged as artifacts.</param>
/// <param name="LatestRrMs">The most recent interval (ms), or 0 before any arrive.</param>
/// <param name="LatestBpm">The most recent reported heart rate, or 0.</param>
/// <param name="MeanRrMs">Mean interval over the recent window (clean intervals only), or 0.</param>
/// <param name="MedianRrMs">Median interval over the recent window (clean intervals only), or 0.</param>
/// <param name="SdnnMs">Standard deviation of the recent clean intervals (a quick noise read), or 0.</param>
/// <param name="RecentRrMs">The recent intervals (ms), oldest first — clean and artifact alike.</param>
public record SourceDiagnostics(
	IntervalSource Source,
	long Count,
	long ArtifactCount,
	double LatestRrMs,
	int LatestBpm,
	double MeanRrMs,
	double MedianRrMs,
	double SdnnMs,
	IReadOnlyList<double> RecentRrMs)
{
	/// <summary>Fraction of recent-window intervals flagged as artifacts (0–1).</summary>
	public double ArtifactRate => Count > 0 ? (double)ArtifactCount / Count : 0.0;
}

/// <summary>
/// An immutable read-out of every active interval stream plus the headline A/B number.
/// </summary>
/// <param name="Sources">Per-source stats, in a stable order (HRS, PPI, ECG).</param>
/// <param name="HrsVsEcgRrBiasMs">
/// Mean-RR difference HRS − ECG (ms) when both streams are producing clean intervals, else null. The
/// systematic bias between the sensor's own RR and our ECG-derived RR: ≈0 means they agree; a large
/// magnitude points at missed/extra beats or dropped frames on the ECG path.
/// </param>
public record BeatDiagnosticsSnapshot(IReadOnlyList<SourceDiagnostics> Sources, double? HrsVsEcgRrBiasMs);

/// <summary>
/// Accumulates <see cref="BeatDiagnostic"/>s into rolling, per-source statistics for the debug surface —
/// the live ECG-vs-HRS A/B and the per-stream artifact rate. Platform-neutral and thread-safe (the
/// diagnostics arrive on a background BLE thread), so both heads' debug views share one tested
/// implementation rather than each re-deriving the maths.
/// </summary>
public sealed class BeatDiagnosticsAggregator(int window = 60)
{
	private readonly object _gate = new();
	private readonly int _window = Math.Max(2, window);
	private readonly Dictionary<IntervalSource, Accumulator> _sources = [];

	/// <summary>Folds one diagnostic into its stream's rolling window.</summary>
	public void Add(BeatDiagnostic diagnostic)
	{
		ArgumentNullException.ThrowIfNull(diagnostic);
		lock (_gate)
		{
			if (!_sources.TryGetValue(diagnostic.Source, out var acc))
			{
				acc = new Accumulator(_window);
				_sources[diagnostic.Source] = acc;
			}

			acc.Add(diagnostic);
		}
	}

	/// <summary>An immutable snapshot of every stream seen so far, plus the HRS-vs-ECG RR bias.</summary>
	public BeatDiagnosticsSnapshot Snapshot()
	{
		lock (_gate)
		{
			var ordered = new[] { IntervalSource.HeartRateService, IntervalSource.PolarPpi, IntervalSource.PolarEcg };
			var stats = new List<SourceDiagnostics>();
			double? hrsMean = null, ecgMean = null;

			foreach (var source in ordered)
			{
				if (!_sources.TryGetValue(source, out var acc))
				{
					continue;
				}

				var s = acc.ToDiagnostics(source);
				stats.Add(s);
				if (source == IntervalSource.HeartRateService && s.MeanRrMs > 0)
				{
					hrsMean = s.MeanRrMs;
				}
				else if (source == IntervalSource.PolarEcg && s.MeanRrMs > 0)
				{
					ecgMean = s.MeanRrMs;
				}
			}

			double? bias = hrsMean is { } h && ecgMean is { } e ? h - e : null;
			return new BeatDiagnosticsSnapshot(stats, bias);
		}
	}

	/// <summary>Clears every stream — call on disconnect so stale stats can't linger.</summary>
	public void Reset()
	{
		lock (_gate)
		{
			_sources.Clear();
		}
	}

	private sealed class Accumulator(int window)
	{
		private readonly Queue<double> _recent = new();   // last `window` intervals (clean and artifact)
		private readonly Queue<double> _recentClean = new();
		private long _count;
		private long _artifactCount;
		private double _latestRr;
		private int _latestBpm;

		public void Add(BeatDiagnostic d)
		{
			_count++;
			_latestRr = d.RrMs;
			_latestBpm = d.HeartRateBpm;

			_recent.Enqueue(d.RrMs);
			while (_recent.Count > window)
			{
				_recent.Dequeue();
			}

			if (d.IsArtifact)
			{
				_artifactCount++;
			}
			else
			{
				_recentClean.Enqueue(d.RrMs);
				while (_recentClean.Count > window)
				{
					_recentClean.Dequeue();
				}
			}
		}

		public SourceDiagnostics ToDiagnostics(IntervalSource source)
		{
			double[] clean = [.. _recentClean];
			double mean = clean.Length > 0 ? clean.Average() : 0.0;
			double median = Median(clean);
			double sdnn = Sdnn(clean, mean);
			return new SourceDiagnostics(
				source,
				_count,
				_artifactCount,
				_latestRr,
				_latestBpm,
				mean,
				median,
				sdnn,
				[.. _recent]);
		}

		private static double Median(double[] values)
		{
			if (values.Length == 0)
			{
				return 0.0;
			}

			var sorted = (double[])values.Clone();
			Array.Sort(sorted);
			int mid = sorted.Length / 2;
			return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
		}

		private static double Sdnn(double[] values, double mean)
		{
			if (values.Length < 2)
			{
				return 0.0;
			}

			double sumSq = 0.0;
			foreach (double v in values)
			{
				double d = v - mean;
				sumSq += d * d;
			}

			return Math.Sqrt(sumSq / (values.Length - 1));
		}
	}
}
