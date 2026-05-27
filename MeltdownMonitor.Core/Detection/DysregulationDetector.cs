using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Core.Detection;

/// <summary>
/// State machine: Idle → Watching → Warning → Alerting → Cooldown → Watching
///
/// Remains Idle until the baseline tracker reports it is warm (≥10 min of data).
/// </summary>
public class DysregulationDetector
{
	private readonly DetectionThresholds _thresholds;
	private DetectorState _state = DetectorState.Idle;
	private DateTimeOffset _stateEnteredAt = DateTimeOffset.MinValue;
	private DateTimeOffset _warningConditionsMetAt = DateTimeOffset.MinValue;
	private bool _warningConditionsActive;

	public DetectorState State => _state;

	public event Action<AlertPayload>? AlertFired;
	public event Action<DetectorState>? StateChanged;

	public DysregulationDetector(DetectionThresholds? thresholds = null)
	{
		_thresholds = thresholds ?? new DetectionThresholds();
	}

	/// <summary>
	/// Processes a new HRV sample. Returns the (possibly updated) detector state.
	/// </summary>
	public DetectorState Process(HrvSample sample, bool baselineIsWarm)
	{
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
		if (IsSevereDropping(sample))
		{
			FireAlert(sample, "Immediate: RMSSD dropped ≥50% below baseline");
			Transition(DetectorState.Cooldown, sample.Timestamp);
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
		if (IsSevereDropping(sample))
		{
			FireAlert(sample, "Severe: RMSSD dropped ≥50% below baseline during Warning");
			Transition(DetectorState.Cooldown, sample.Timestamp);
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
			Transition(DetectorState.Cooldown, sample.Timestamp);
		}
	}

	private void ProcessAlerting(HrvSample sample)
	{
		// Alerting transitions immediately to Cooldown in FireAlert; this branch
		// should not normally be reached, but guard against it.
		Transition(DetectorState.Cooldown, sample.Timestamp);
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
			&& sample.BaselineLfHfRatio > 0
			&& sample.Extended is { LfHfRatio: > 0 } extended)
		{
			double lfHfRise = (extended.LfHfRatio - sample.BaselineLfHfRatio) / sample.BaselineLfHfRatio;
			return lfHfRise >= _thresholds.LfHfWarningRiseFraction;
		}

		return true;
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
		StateChanged?.Invoke(newState);
	}
}
