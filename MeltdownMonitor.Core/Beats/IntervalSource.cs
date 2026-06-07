namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// Which stream supplies the beat-to-beat intervals that drive HRV. A device exposes up to three
/// equivalent-but-distinct interval sources, so exactly one must be chosen — feeding more than one
/// would double-count beats and corrupt every downstream metric.
///
/// The heads treat the non-default sources as a <i>preference</i>: HRS RR keeps flowing until the
/// preferred Polar stream is actually producing intervals, then the head switches and suppresses HRS
/// beats. So an unsupported device (e.g. PPI on an H10, which has none) or a slow PMD start simply
/// stays on HRS rather than going silent.
/// </summary>
public enum IntervalSource
{
	/// <summary>Standard BLE Heart Rate Measurement RR intervals (default; works on every device).</summary>
	HeartRateService = 0,

	/// <summary>
	/// Polar PMD peak-to-peak intervals (Verity Sense). Optical, with a per-beat error estimate and
	/// contact flags that sharpen artifact rejection; carries more latency than HRS RR.
	/// </summary>
	PolarPpi = 1,

	/// <summary>
	/// RR derived from Polar PMD raw ECG R-peaks (H10). Gold-standard fidelity; heavier to process.
	/// </summary>
	PolarEcg = 2,
}
