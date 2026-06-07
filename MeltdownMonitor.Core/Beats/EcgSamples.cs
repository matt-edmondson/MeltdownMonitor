namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// A batch of raw ECG samples decoded from one PMD ECG frame (the H10's 130 Hz stream), with the
/// in-batch offsets of any R-peaks the detector found. Surfaced so the UI can render the live trace;
/// the same samples already drive RR detection inside the beat source.
/// </summary>
/// <param name="Timestamp">Arrival time of the frame (UTC).</param>
/// <param name="MicroVolts">The samples, oldest first, in microvolts.</param>
/// <param name="SampleRateHz">Sample rate (130 Hz on the H10).</param>
/// <param name="RPeakOffsets">Indices into <see cref="MicroVolts"/> where an R-peak was detected.</param>
public record EcgSamples(
	DateTimeOffset Timestamp,
	IReadOnlyList<int> MicroVolts,
	double SampleRateHz,
	IReadOnlyList<int> RPeakOffsets);

/// <summary>
/// An optional capability a beat source implements when it is streaming raw ECG (the Polar PMD ECG
/// interval source). The pipeline checks for it the same way it checks <see cref="IMotionSource"/>;
/// a source that isn't streaming ECG simply never raises it.
/// </summary>
public interface IEcgSource
{
	/// <summary>
	/// Raised for each decoded ECG frame. Arrives in bursts on a background BLE thread, so subscribers
	/// must marshal to their UI thread and tolerate high-rate delivery.
	/// </summary>
	event Action<EcgSamples>? EcgSamplesReceived;
}
