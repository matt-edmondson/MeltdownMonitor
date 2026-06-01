using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Core.Detection;

/// <summary>
/// State machine: Idle → Watching → Warning → Alerting → Cooldown → Watching
///
/// Remains Idle until the baseline tracker reports it is warm (≥10 min of data).
/// </summary>
public class DysregulationDetector
{
	private readonly Func<DetectionThresholds> _thresholdsProvider;
	private DetectorState _state = DetectorState.Idle;
	private DateTimeOffset _stateEnteredAt = DateTimeOffset.MinValue;
	private DateTimeOffset _warningConditionsMetAt = DateTimeOffset.MinValue;
	private bool _warningConditionsActive;
	private DateTimeOffset _recoveryHeldSince = DateTimeOffset.MinValue;
	private bool _recoveryActive;
	private int _severeDropStreak;

	private DetectionThresholds _thresholds => _thresholdsProvider();

	public DetectorState State => _state;

	public event Action<AlertPayload>? AlertFired;
	public event Action<DetectorState>? StateChanged;

	public DysregulationDetector(DetectionThresholds? thresholds = null)
	{
		var snapshot = thresholds ?? new DetectionThresholds();
		_thresholdsProvider = () => snapshot;
	}

	public DysregulationDetector(Func<DetectionThresholds> thresholdsProvider)
	{
		_thresholdsProvider = thresholdsProvider;
	}

	/// <summary>
	/// Processes a new HRV sample. Returns the (possibly updated) detector state.
	/// </summary>
	/// <param name="sample">The latest HRV sample.</param>
	/// <param name="baselineIsWarm">Whether the baseline tracker has enough data to arm the detector.</param>
	/// <param name="contact">
	/// The sensor's skin / electrode contact state. When <see cref="SensorContactStatus.NotDetected"/>,
	/// the sample is treated as untrustworthy: the state is held and in-progress streaks reset, so a
	/// dropped sensor can neither raise an alert nor be mistaken for recovery. The default
	/// (<see cref="SensorContactStatus.NotSupported"/>) and <see cref="SensorContactStatus.Detected"/>
	/// are both treated as reliable — sensors that don't report contact are never gated.
	/// </param>
	public DetectorState Process(
		HrvSample sample,
		bool baselineIsWarm,
		SensorContactStatus contact = SensorContactStatus.NotSupported)
	{
		// Sensor off-body: RR data is unreliable, so don't let this sample drive the
		// state machine. Hold the current state and clear any in-progress warning or
		// recovery streak, so a contact blip neither triggers an alert nor counts as
		// recovery — the streak must re-accumulate from clean data once contact returns.
		if (contact == SensorContactStatus.NotDetected)
		{
			_warningConditionsActive = false;
			_recoveryActive = false;
			_severeDropStreak = 0;
			return _state;
		}

		if (!baselineIsWarm && _state == DetectorState.Idle)
		{
			return _state;
		}

		if (_state == DetectorState.Idle && baselineIsWarm)
		{
			Transition(DetectorState.Watching, sample.Timestamp);
		}

		switch (_state)
		{
			case DetectorState.Watching:
				ProcessWatching(sample);
				break;

			case DetectorState.Warning:
				ProcessWarning(sample);
				break;

			case DetectorState.Alerting:
				ProcessAlerting(sample);
				break;

			case DetectorState.Cooldown:
				ProcessCooldown(sample);
				break;
		}

		return _state;
	}

	private void ProcessWatching(HrvSample sample)
	{
		if (IsSevereDropConfirmed(sample))
		{
			FireAlert(sample, "Immediate: RMSSD dropped ≥50% below baseline");
			Transition(DetectorState.Alerting, sample.Timestamp);
			return;
		}

		bool conditionsMet = IsWarningConditionMet(sample);

		if (conditionsMet && !_warningConditionsActive)
		{
			_warningConditionsActive = true;
			_warningConditionsMetAt = sample.Timestamp;
		}
		else if (!conditionsMet)
		{
			_warningConditionsActive = false;
		}

		if (_warningConditionsActive &&
			(sample.Timestamp - _warningConditionsMetAt) >= _thresholds.WarningHoldDuration)
		{
			_warningConditionsActive = false;
			Transition(DetectorState.Warning, sample.Timestamp);
		}
	}

	private void ProcessWarning(HrvSample sample)
	{
		if (IsSevereDropConfirmed(sample))
		{
			FireAlert(sample, "Severe: RMSSD dropped ≥50% below baseline during Warning");
			Transition(DetectorState.Alerting, sample.Timestamp);
			return;
		}

		bool conditionsMet = IsWarningConditionMet(sample);

		if (!conditionsMet)
		{
			// Conditions cleared — step back to watching
			_warningConditionsActive = false;
			Transition(DetectorState.Watching, sample.Timestamp);
			return;
		}

		if ((sample.Timestamp - _stateEnteredAt) >= _thresholds.AlertingEscalationDuration)
		{
			FireAlert(sample, "Sustained: Warning conditions held for escalation window");
			Transition(DetectorState.Alerting, sample.Timestamp);
		}
	}

	private void ProcessAlerting(HrvSample sample)
	{
		// Hold the Alerting state until the body has *physiologically recovered*,
		// which is distinct from merely returning toward baseline. A single sample
		// dipping back below the Warning trigger is not recovery — recovery requires
		// a genuine vagal rebound (RMSSD restored near baseline, HR settled) that is
		// sustained for RecoveryHoldDuration. Transient blips reset the streak.
		bool recovering = IsPhysiologicallyRecovered(sample);

		if (recovering && !_recoveryActive)
		{
			_recoveryActive = true;
			_recoveryHeldSince = sample.Timestamp;
		}
		else if (!recovering)
		{
			_recoveryActive = false;
		}

		if (_recoveryActive &&
			(sample.Timestamp - _recoveryHeldSince) >= _thresholds.RecoveryHoldDuration)
		{
			Transition(DetectorState.Cooldown, sample.Timestamp);
		}
	}

	private void ProcessCooldown(HrvSample sample)
	{
		if ((sample.Timestamp - _stateEnteredAt) >= _thresholds.CooldownDuration)
		{
			_warningConditionsActive = false;
			Transition(DetectorState.Watching, sample.Timestamp);
		}
	}

	private bool IsWarningConditionMet(HrvSample sample)
	{
		if (sample.BaselineRmssd <= 0 || sample.BaselineHr <= 0)
		{
			return false;
		}

		double rmssdDrop = (sample.BaselineRmssd - sample.Rmssd) / sample.BaselineRmssd;
		double hrRise = (sample.MeanHr - sample.BaselineHr) / sample.BaselineHr;

		bool coreConditionMet = rmssdDrop >= _thresholds.RmssdWarningDropFraction &&
								hrRise >= _thresholds.HrWarningRiseFraction;

		if (!coreConditionMet)
		{
			return false;
		}

		// Optional LF/HF corroboration — only checked when enabled, baseline is ready,
		// and extended metrics are present in this sample.
		if (_thresholds.UseLfHfCorroboration
			&& _thresholds.LfHfCorroborationMode == LfHfCorroborationMode.Veto
			&& sample.BaselineLfHfRatio > 0
			&& sample.Extended is { LfHfRatio: > 0 } extended)
		{
			double lfHfRise = (extended.LfHfRatio - sample.BaselineLfHfRatio) / sample.BaselineLfHfRatio;
			return lfHfRise >= _thresholds.LfHfWarningRiseFraction;
		}

		return true;
	}

	/// <summary>
	/// True when the sample shows a genuine return of autonomic regulation rather
	/// than a transient excursion back toward baseline: parasympathetic tone (RMSSD)
	/// has climbed back near baseline AND sympathetic drive (HR) has settled. The
	/// caller additionally requires this to persist (see <see cref="ProcessAlerting"/>)
	/// before treating it as physiological recovery.
	/// </summary>
	private bool IsPhysiologicallyRecovered(HrvSample sample)
	{
		if (sample.BaselineRmssd <= 0 || sample.BaselineHr <= 0)
		{
			return false;
		}

		double rmssdDrop = (sample.BaselineRmssd - sample.Rmssd) / sample.BaselineRmssd;
		double hrRise = (sample.MeanHr - sample.BaselineHr) / sample.BaselineHr;

		return rmssdDrop <= _thresholds.RmssdRecoveryDropFraction
			&& hrRise <= _thresholds.HrRecoveryRiseFraction;
	}

	private bool IsSevereDropping(HrvSample sample)
	{
		if (sample.BaselineRmssd <= 0)
		{
			return false;
		}

		double rmssdDrop = (sample.BaselineRmssd - sample.Rmssd) / sample.BaselineRmssd;
		return rmssdDrop >= _thresholds.RmssdAlertingDropFraction;
	}

	// Counts consecutive immediate-severe samples; fires only once the configured confirmation
	// count is reached. Default count 1 → fires on the first qualifying sample (prior behaviour).
	private bool IsSevereDropConfirmed(HrvSample sample)
	{
		if (IsSevereDropping(sample))
		{
			_severeDropStreak++;
			return _severeDropStreak >= Math.Max(1, _thresholds.SevereDropConfirmationCount);
		}

		_severeDropStreak = 0;
		return false;
	}

	private void FireAlert(HrvSample sample, string reason)
	{
		var payload = new AlertPayload(
			sample.Timestamp,
			reason,
			sample.Rmssd,
			sample.BaselineRmssd);
		AlertFired?.Invoke(payload);
	}

	private void Transition(DetectorState newState, DateTimeOffset timestamp)
	{
		_state = newState;
		_stateEnteredAt = timestamp;
		// Recovery and severe-drop streaks are tracked per episode; never carry one across states.
		_recoveryActive = false;
		_severeDropStreak = 0;
		StateChanged?.Invoke(newState);
	}
}
