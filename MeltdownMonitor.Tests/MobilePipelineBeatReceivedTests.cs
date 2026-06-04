using System.Runtime.CompilerServices;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Mobile;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MobilePipelineBeatReceivedTests
{
	/// <summary>
	/// <see cref="Pipeline.BeatReceived"/> must fire once for each non-artifact beat
	/// that flows through the pipeline loop, so the Metrics charts and the
	/// Regulation Field's RR-textured trace have access to the raw RR stream —
	/// mirroring the desktop pipeline's BeatReceived event.
	/// </summary>
	[TestMethod]
	public async Task BeatReceived_FiresOncePerBeat_ForThreeBeats()
	{
		var settings = new MobileSettings();
		using var repo = new MeltdownRepository(":memory:");
		var source = new ThreeBeatSource();
		using var pipeline = new Pipeline(settings, repo, source);

		int received = 0;
		pipeline.BeatReceived += _ => Interlocked.Increment(ref received);

		pipeline.Start();

		// Wait up to 5 s for all 3 beats to be delivered, then stop.
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (Volatile.Read(ref received) < 3 && !cts.Token.IsCancellationRequested)
		{
			await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
		}

		await pipeline.StopAsync().ConfigureAwait(false);

		Assert.AreEqual(3, received,
			"BeatReceived must fire exactly once per non-artifact beat delivered by the source.");
	}

	private sealed class ThreeBeatSource : IBeatSource
	{
		public async IAsyncEnumerable<Beat> GetBeatsAsync(
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			for (int i = 0; i < 3; i++)
			{
				yield return new Beat(DateTimeOffset.UnixEpoch.AddSeconds(i), 820 - i * 5, 73, false);
				await Task.Yield();
			}
		}
	}
}
