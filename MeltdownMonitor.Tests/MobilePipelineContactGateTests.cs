using System.Runtime.CompilerServices;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Persistence;
using MeltdownMonitor.Mobile;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MobilePipelineContactGateTests
{
	// While the sensor reports no skin contact, nothing should be collected — no beats persisted or
	// fanned out — because off-body RR is unreliable and would poison the history and baseline.
	[TestMethod]
	public async Task NoContact_SuppressesCollection()
	{
		int received = await RunWithContactAsync(SensorContactStatus.NotDetected).ConfigureAwait(false);
		Assert.AreEqual(0, received, "No beats should be collected while contact is lost.");
	}

	[TestMethod]
	public async Task Contact_AllowsCollection()
	{
		int received = await RunWithContactAsync(SensorContactStatus.Detected).ConfigureAwait(false);
		Assert.AreEqual(5, received, "Beats flow normally once contact is reported.");
	}

	[TestMethod]
	public async Task ContactNotReported_AllowsCollection()
	{
		// A sensor that doesn't report contact at all is never gated.
		int received = await RunWithContactAsync(SensorContactStatus.NotSupported).ConfigureAwait(false);
		Assert.AreEqual(5, received);
	}

	private static async Task<int> RunWithContactAsync(SensorContactStatus contact)
	{
		var settings = new MobileSettings();
		using var repo = new MeltdownRepository(":memory:");
		var source = new ContactThenBeatsSource(contact, beats: 5);
		using var pipeline = new Pipeline(settings, repo, source);

		int received = 0;
		pipeline.BeatReceived += _ => Interlocked.Increment(ref received);

		pipeline.Start();

		// Let the (finite) source drain. The detected case finishes as soon as 5 arrive; the gated
		// case never reaches 5, so fall through on the timeout and assert it stayed at 0.
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
		while (Volatile.Read(ref received) < 5 && !cts.Token.IsCancellationRequested)
		{
			await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
		}

		await pipeline.StopAsync().ConfigureAwait(false);
		return Volatile.Read(ref received);
	}

	private sealed class ContactThenBeatsSource(SensorContactStatus contact, int beats)
		: IBeatSource, IContactSource
	{
		public event Action<SensorContactStatus>? SensorContactChanged;

		public async IAsyncEnumerable<Beat> GetBeatsAsync(
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			// Report contact before any beats, so the pipeline's LatestContact is set when they arrive.
			SensorContactChanged?.Invoke(contact);
			for (int i = 0; i < beats; i++)
			{
				yield return new Beat(DateTimeOffset.UnixEpoch.AddSeconds(i), 820, 73, IsArtifact: false);
				await Task.Yield();
			}
		}
	}
}
