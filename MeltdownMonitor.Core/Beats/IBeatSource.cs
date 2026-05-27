namespace MeltdownMonitor.Core.Beats;

public interface IBeatSource
{
	IAsyncEnumerable<Beat> GetBeatsAsync(CancellationToken cancellationToken);
}
