using System.Runtime.CompilerServices;

namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// Produces a deterministic stream of beats from a fixed RR sequence.
/// Useful for tests and offline replay. Each beat is emitted without
/// delay so tests run instantly.
/// </summary>
public sealed class SyntheticBeatSource : IBeatSource
{
	private readonly IReadOnlyList<double> _rrSequence;
	private readonly int _heartRateBpm;
	private readonly DateTimeOffset _startTime;

	public SyntheticBeatSource(
		IReadOnlyList<double> rrSequence,
		int heartRateBpm = 70,
		DateTimeOffset? startTime = null)
	{
		_rrSequence = rrSequence;
		_heartRateBpm = heartRateBpm;
		_startTime = startTime ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
	}

	public async IAsyncEnumerable<Beat> GetBeatsAsync(
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var ts = _startTime;
		var filter = new RrArtifactFilter();

		foreach (double rr in _rrSequence)
		{
			cancellationToken.ThrowIfCancellationRequested();
			bool isArtifact = filter.IsArtifact(rr);
			yield return new Beat(ts, rr, _heartRateBpm, isArtifact);
			ts = ts.AddMilliseconds(rr);
			await Task.Yield();
		}
	}
}
