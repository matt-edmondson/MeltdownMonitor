namespace MeltdownMonitor.Core.Detection;

/// <summary>
/// Tuning for the <see cref="HypoarousalDetector"/>. Serialises with the rest of the detection
/// settings (System.Text.Json round-trips these members). Conservative by design — for the
/// low-arousal edge a false alarm is worse than a miss (audit A(b)), and the HRV signature is
/// provisional, so the bar to enter a shutdown episode is deliberately high.
/// </summary>
public record HypoarousalThresholds
{
	/// <summary>
	/// The <see cref="HypoarousalSignal"/> level [0,1] that, once sustained, enters a LowArousal
	/// episode. The signal is a product of an HR-fall ramp and a low-variability gate, so 0.5
	/// requires a clear, joint collapse — e.g. HR ~20–25% below baseline <i>and</i> RMSSD around
	/// 25–50% of baseline. A milder bar would fire on ordinary calm.
	/// </summary>
	public double EnterSignal { get; init; } = 0.5;

	/// <summary>
	/// The signal level the episode must fall to/below (sustained) to exit. Hysteresis below
	/// <see cref="EnterSignal"/> stops the state flapping around a single threshold.
	/// </summary>
	public double ExitSignal { get; init; } = 0.3;

	/// <summary>
	/// The enter signal must hold continuously for this long before entering LowArousal. Shutdown
	/// builds slowly; a long hold rejects a transient low-HR dip (a sigh, a posture change).
	/// </summary>
	public TimeSpan EnterHoldDuration { get; init; } = TimeSpan.FromSeconds(60);

	/// <summary>
	/// The exit signal must hold continuously for this long before leaving LowArousal back to
	/// Monitoring — the analogue of the dysregulation recovery hold.
	/// </summary>
	public TimeSpan RecoveryDuration { get; init; } = TimeSpan.FromSeconds(60);

	/// <summary>
	/// Minimum time between hypoarousal alerts. Re-entering LowArousal within this window updates
	/// the state silently so the user is not pestered while still settling.
	/// </summary>
	public TimeSpan CooldownDuration { get; init; } = TimeSpan.FromMinutes(10);
}
