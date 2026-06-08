using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Core.Detection;

/// <summary>
/// Watches the <i>lower</i> edge of the window of tolerance — sustained low arousal / shutdown —
/// as a peer to <see cref="DysregulationDetector"/>. State machine:
/// Idle → Monitoring → LowArousal → Monitoring.
///
/// Remains Idle until the baseline tracker reports it is warm. Enters LowArousal once the
/// <see cref="HypoarousalSignal"/> stays at/above <see cref="HypoarousalThresholds.EnterSignal"/>
/// for <see cref="HypoarousalThresholds.EnterHoldDuration"/>, and fires a single
/// <see cref="AlertKind.Hypoarousal"/> alert on entry (debounced by
/// <see cref="HypoarousalThresholds.CooldownDuration"/>). Leaves once the signal stays at/below
/// <see cref="HypoarousalThresholds.ExitSignal"/> for <see cref="HypoarousalThresholds.RecoveryDuration"/>.
///
/// Clinical humility (audit A(b)): the HRV signature catches only HR-down + HRV-flat/down collapse;
/// a dorsal-vagal shutdown that preserves HRV is indistinguishable from rest and will be missed.
/// </summary>
public class HypoarousalDetector
{
	private readonly Func<HypoarousalThresholds> _thresholdsProvider;
	private HypoarousalState _state = HypoarousalState.Idle;
	private DateTimeOffset _enterConditionsMetAt = DateTimeOffset.MinValue;
	private bool _enterConditionsActive;
	private DateTimeOffset _exitConditionsMetAt = DateTimeOffset.MinValue;
	private bool _exitConditionsActive;
	private DateTimeOffset _lastAlertAt = DateTimeOffset.MinValue;
	private bool _hasAlerted;

	private HypoarousalThresholds _thresholds => _thresholdsProvider();

	public HypoarousalState State => _state;

	/// <summary>True while a low-arousal episode is in progress — the cue to freeze the baseline so
	/// it does not re-normalise toward the shutdown.</summary>
	public bool IsEpisodeActive => _state == HypoarousalState.LowArousal;

	public event Action<AlertPayload>? AlertFired;
	public event Action<HypoarousalState>? StateChanged;

	public HypoarousalDetector(HypoarousalThresholds? thresholds = null)
	{
		var snapshot = thresholds ?? new HypoarousalThresholds();
		_thresholdsProvider = () => snapshot;
	}

	public HypoarousalDetector(Func<HypoarousalThresholds> thresholdsProvider)
	{
		_thresholdsProvider = thresholdsProvider;
	}

	/// <summary>
	/// Clears the in-progress enter/exit streaks (without changing state). Called on the off-body path
	/// and by the pipeline on contact loss, so a streak can't resume across a long beats-free gap.
	/// </summary>
	public void ResetTransientStreaks()
	{
		_enterConditionsActive = false;
		_exitConditionsActive = false;
	}

	/// <summary>
	/// Processes a new HRV sample. Returns the (possibly updated) state.
	/// </summary>
	/// <param name="sample">The latest HRV sample.</param>
	/// <param name="baselineIsWarm">Whether the baseline tracker has enough data to arm the detector.</param>
	/// <param name="contact">
	/// Sensor contact state. When <see cref="SensorContactStatus.NotDetected"/> the sample is
	/// untrustworthy — off-body data reads like a low-HR collapse, the exact hypoarousal signature —
	/// so the state is held and both streaks reset: a dropout can neither enter an episode nor be
	/// mistaken for recovery out of one.
	/// </param>
	public HypoarousalState Process(
		HrvSample sample,
		bool baselineIsWarm,
		SensorContactStatus contact = SensorContactStatus.NotSupported)
	{
		if (contact == SensorContactStatus.NotDetected)
		{
			ResetTransientStreaks();
			return _state;
		}

		if (!baselineIsWarm && _state == HypoarousalState.Idle)
		{
			return _state;
		}

		if (_state == HypoarousalState.Idle && baselineIsWarm)
		{
			Transition(HypoarousalState.Monitoring, sample.Timestamp);
		}

		double signal = HypoarousalSignal.Compute(
			sample.Rmssd, sample.MeanHr, sample.BaselineRmssd, sample.BaselineHr);

		switch (_state)
		{
			case HypoarousalState.Monitoring:
				ProcessMonitoring(sample, signal);
				break;

			case HypoarousalState.LowArousal:
				ProcessLowArousal(sample, signal);
				break;
		}

		return _state;
	}

	private void ProcessMonitoring(HrvSample sample, double signal)
	{
		bool met = signal >= _thresholds.EnterSignal;

		if (met && !_enterConditionsActive)
		{
			_enterConditionsActive = true;
			_enterConditionsMetAt = sample.Timestamp;
		}
		else if (!met)
		{
			_enterConditionsActive = false;
		}

		if (_enterConditionsActive &&
			(sample.Timestamp - _enterConditionsMetAt) >= _thresholds.EnterHoldDuration)
		{
			Transition(HypoarousalState.LowArousal, sample.Timestamp);
			MaybeFireAlert(sample);
		}
	}

	private void ProcessLowArousal(HrvSample sample, double signal)
	{
		// Leaving requires the signal to settle below the (lower, hysteretic) exit level and *hold*
		// — a single sample bouncing below the bar is not recovery, mirroring the dysregulation
		// detector's sustained-recovery gate.
		bool clearing = signal <= _thresholds.ExitSignal;

		if (clearing && !_exitConditionsActive)
		{
			_exitConditionsActive = true;
			_exitConditionsMetAt = sample.Timestamp;
		}
		else if (!clearing)
		{
			_exitConditionsActive = false;
		}

		if (_exitConditionsActive &&
			(sample.Timestamp - _exitConditionsMetAt) >= _thresholds.RecoveryDuration)
		{
			Transition(HypoarousalState.Monitoring, sample.Timestamp);
		}
	}

	private void MaybeFireAlert(HrvSample sample)
	{
		// Debounce repeated episodes so a user oscillating around the threshold is not pestered;
		// the state still updates, only the alert is suppressed.
		if (_hasAlerted && (sample.Timestamp - _lastAlertAt) < _thresholds.CooldownDuration)
		{
			return;
		}

		_lastAlertAt = sample.Timestamp;
		_hasAlerted = true;

		var payload = new AlertPayload(
			sample.Timestamp,
			"Hypoarousal: sustained low arousal (HR below baseline, variability not elevated)",
			sample.Rmssd,
			sample.BaselineRmssd,
			AlertKind.Hypoarousal);
		AlertFired?.Invoke(payload);
	}

	private void Transition(HypoarousalState newState, DateTimeOffset timestamp)
	{
		_state = newState;
		// Streaks are tracked per state; never carry one across a transition.
		_enterConditionsActive = false;
		_exitConditionsActive = false;
		StateChanged?.Invoke(newState);
	}
}
