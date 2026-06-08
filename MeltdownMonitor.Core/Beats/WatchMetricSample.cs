namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// One health-metric reading collected on an Apple Watch and relayed to the phone
/// (docs/watch-corroboration.md). The watch is a <b>second, independent witness</b> of the same
/// body the chest strap is on: its optical sensor measures heart rate at the wrist, and watchOS
/// periodically computes HRV as SDNN (not the beat-to-beat RMSSD the strap yields). The phone uses
/// the watch HR to cross-check the strap — two sensors on one body should agree, and a sustained
/// disagreement marks the strap signal as suspect (motion artifact, poor electrode contact).
///
/// Wrist-optical HR is smoothed and lags chest-ECG HR on fast transients, so consumers compare with
/// a generous tolerance and never treat the watch as ground truth — it corroborates, it does not
/// replace the strap.
/// </summary>
/// <param name="Timestamp">When the watch measured the sample (UTC).</param>
/// <param name="HeartRateBpm">Wrist-optical heart rate (bpm).</param>
/// <param name="HrvSdnnMs">watchOS HRV as SDNN in milliseconds, when a fresh value is available;
/// null between watchOS's infrequent HRV computations. Carried for future use — the corroboration
/// gate cross-checks HR, which is the robust, low-latency signal.</param>
/// <param name="Contact">The watch's on-wrist state, mapped to the shared
/// <see cref="SensorContactStatus"/>. An off-wrist watch is untrustworthy and never corroborates.</param>
public readonly record struct WatchMetricSample(
	DateTimeOffset Timestamp,
	double HeartRateBpm,
	double? HrvSdnnMs,
	SensorContactStatus Contact = SensorContactStatus.NotSupported);

/// <summary>
/// An optional capability surfacing the Apple Watch metric stream relayed from the phone↔watch link
/// (WatchConnectivity on iOS). It is distinct from <see cref="IBeatSource"/> — the watch metrics do
/// not ride the strap's BLE connection; they arrive over the same <c>WCSession</c> the haptic
/// companion uses, from the opposite direction. The pipeline checks for this interface the same way
/// it checks <see cref="IMotionSource"/> / <see cref="IBatterySource"/>: a host that supplies no
/// watch source simply contributes no corroboration, leaving detection byte-identical to a
/// no-watch build.
/// </summary>
public interface IWatchMetricSource
{
	/// <summary>
	/// Raised for each metric reading relayed from the watch. May arrive on a background thread and
	/// is coalesced "freshest wins" upstream, so subscribers must marshal to their own thread and
	/// tolerate irregular cadence.
	/// </summary>
	event Action<WatchMetricSample>? WatchMetricReceived;
}
