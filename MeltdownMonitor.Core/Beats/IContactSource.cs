namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// An optional capability a beat source may also implement to surface the
/// sensor's skin / electrode contact state from the Heart Rate Measurement
/// characteristic (<c>0x2A37</c>). The pipeline checks for this interface on its
/// <see cref="IBeatSource"/> and, when present, forwards changes to the UI so it
/// can flag when readings are untrustworthy. Reported independently of beats
/// because a sensor that loses contact typically stops emitting RR intervals
/// altogether — so contact loss would never reach the pipeline via a
/// <see cref="Beat"/>.
/// </summary>
public interface IContactSource
{
	/// <summary>
	/// Raised whenever a Heart Rate notification reports the contact state. May
	/// fire on a background BLE thread, so subscribers must marshal to their UI
	/// thread.
	/// </summary>
	event Action<SensorContactStatus>? SensorContactChanged;
}
