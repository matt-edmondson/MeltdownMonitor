# MeltdownMonitor — Clinical Audit

**Date:** 2026-06-01
**Auditor:** Claude (Opus 4.8), code-level review against standard HRV literature (Task Force 1996; Shaffer & Ginsberg 2017).

**Scope:** physiological/scientific correctness and clinical safety of the HRV pipeline, baseline
tracking, and dysregulation detection in `MeltdownMonitor.Core` (the source of truth for all numbers),
plus the two `Pipeline` heads.

**Out of scope:** UI rendering, BLE transport correctness, persistence durability.

> **Verification caveat.** All findings below are provable from the Core code and deterministic traces.
> Per the repo's own constraint (`CLAUDE.md`), real-time timing/visual behaviour and live-sensor
> physiology — e.g. how often the stuck-median or fast-ramp cases actually occur in the wild — can only
> be confirmed with a live session and a real Polar sensor. Those *likelihoods* are reasoned, not measured.

---

## Bottom line

The underlying HRV mathematics is sound and several safety behaviours are thoughtfully designed.
The clinically significant issues are **not arithmetic errors** — they are **modelling and calibration
gaps that all fail in the same direction: the app can stay quiet, or display "calm," while the user is
dysregulated.** For the stated population (PTSD/C-PTSD/autistic users), that direction of error is the
one that matters most.

---

## What is correct (assurance)

Verified against the standard HRV literature and confirmed right:

- **RMSSD** (`MeltdownMonitor.Core/Hrv/ShortWindowHrvCalculator.cs:140-155`) — `√(Σdiff² / (N−1))`.
  Correct denominator (N−1 successive differences).
- **Frequency-domain PSD** (`MeltdownMonitor.Core/Hrv/FrequencyDomainHrvCalculator.cs`) — one-sided PSD
  with proper Hann-window normalization (`2·|X|²/(fs·Σw²)`), correct band integration, units in ms².
  Textbook-correct.
- **Poincaré** (`MeltdownMonitor.Core/Hrv/PoincarePlotCalculator.cs`) — SD1 = RMSSD/√2 and
  SD2 = √(2·SDNN²−SD1²) are the correct identities.
- **RR unit conversion** (`MeltdownMonitor.Core/Beats/HrMeasurementParser.cs:17`) — `1000/1024` per the
  BLE Heart Rate Service spec; flags parsed correctly.
- **EWMA α conversion** (`MeltdownMonitor.App/Pipeline.cs:133-141`) — `α = cadence/window` is the correct
  first-order discretization of the stated memory window.
- **Well-designed safety behaviours:** contact-gating holds state and resets streaks when off-body
  (`DysregulationDetector.cs:60-65`); baseline freezes during Warning/Alerting and off-contact
  (`BaselineHrvTracker.cs:78-89`); the anchor ±40 % guardrail prevents silent re-normalization
  (`BaselineHrvTracker.cs:194-213`); and the severe ≥50 % drop bypasses the (laggy) LF/HF gate so fast
  events still alert.

**Non-issues (explicitly):** pNN50's N−1 denominator and the population-SD form of SDNN are both accepted
conventions; mean-removal vs. linear detrend and linear vs. spline interpolation only marginally affect
LF. None are defects.

---

## Findings (severity-ranked)

Each finding is stated as: **code fact → clinical consequence → realistic likelihood → classification**
(documented scope-vs-implementation mismatch, or a true gap).

### A. The detector and the display are single-axis; hypoarousal/shutdown is invisible — and shown as "calm" — **HIGH**

- **Code fact:** The detector has exactly two firing paths — `RMSSD↓≥30 % AND HR↑≥15 %`
  (`DysregulationDetector.cs:199-200`) and `RMSSD↓≥50 %` (`DysregulationDetector.cs:248-249`). Both are
  sympathetic-activation signatures. There is **no path, and no state variable, that can represent low
  arousal.** Worse, `RegulationFieldCalculator.cs:52-54` builds a single signed index where a *negative*
  combined deviation (HR below baseline) → the cool **REST** lobe, and `RegulationFieldCalculator.cs:56`
  sets `quality = clamp(RMSSD/baseline)` ≈ 1 when RMSSD is preserved. So a hypoaroused user is rendered
  as *more* regulated.
- **Clinical consequence:** A shutdown / freeze / dissociative collapse — where, depending on
  presentation, HR may fall and RMSSD may be preserved or variable rather than collapse — produces no
  alert and an actively reassuring "REST" display.
- **Likelihood:** High for this population. Dorsal-vagal shutdown and dissociation are core to C-PTSD and
  autistic experience, not edge cases.
- **Classification: documented scope-vs-implementation mismatch.** The README sells a two-sided "window
  of tolerance" and states *"meltdown is the high-arousal form, shutdown the low-arousal form. Both have
  measurable HRV signatures."* The implementation models only the high-arousal edge. Corroborating tell:
  the self-report vocabulary `AnnotationLabel` (Fine/Edged/Escalating/Blown) is *also* single-axis — even
  the ground-truth capture has no word for "shut down / numb / dissociated."

### B. Calibration-during-symptom on cold start — **MEDIUM-HIGH**

- **Code fact:** On first-ever use (no persisted history → no anchor; the anchor is only set inside
  `SeedFromHistory`, `BaselineHrvTracker.cs:142-143`), the first sample *becomes* the baseline
  (`BaselineHrvTracker.cs:94-99`), then the EWMA adapts slowly (α≈0.0056) over a 10-min warm-up.
- **Clinical consequence:** If the user straps the sensor on while already dysregulated, the baseline
  anchors to the dysregulated state. The detector compares against that, sees no relative drop, and never
  fires. The "freeze during Warning/Alerting" mitigation can't help — the detector never enters Warning.
  The ±40 % guardrail can't help — there's no anchor yet.
- **Related code fact (mobile):** `WarmStartAsync` (`MeltdownMonitor.Mobile/Pipeline.cs:169-177`) seeds
  the RMSSD baseline from `rrMs = 60000/bpm` of *sparse HealthKit HR samples*. That quantity is the
  variation of averaged HR readings seconds-to-minutes apart — it is not beat-to-beat parasympathetic
  RMSSD, and will generally bias the seeded baseline high.
- **Likelihood:** Common (people often reach for such a tool *because* they're escalating).
- **Classification: true gap** — no "rest-state" precondition on calibration.

### C. LF/HF corroboration (default ON) suppresses the early-warning value proposition — **MEDIUM-HIGH**

- **Code fact:** Corroboration defaults true (`DetectionThresholds.cs:56`). When a personal LF/HF baseline
  exists, `IsWarningConditionMet` *returns* the LF/HF condition (`DysregulationDetector.cs:209-215`):
  Warning now requires core **AND** LF/HF ↑≥50 %. But LF/HF is computed on the **5-minute** extended
  window (`FrequencyDomainHrvCalculator.cs:17`, ≥2 min of data, recomputed every 30 s), while the core
  condition is a 60-second-window signal.
- **Clinical consequence:** During a fast-onset event the 5-min window is still dominated by the preceding
  calm data, so LF/HF lags by minutes and may not show +50 %. A laggy, smeared signal is gating a
  responsive one — which delays or suppresses exactly the *early* warnings the app exists to provide
  ("before you consciously notice"). The ≥50 % severe path and warm-up fallback survive, so this
  specifically erodes the *moderate-sustained early warning* — the core feature.
- **Likelihood:** Every warm-state moderate escalation.
- **Classification: true gap / risky default** — biases toward false negatives, the more dangerous
  direction here.

### D. Immediate ≥50 % path has no sustain and no corroboration — **MEDIUM (false-positive)**

- **Code fact:** `IsSevereDropping` (`DysregulationDetector.cs:241-250`) alerts on a single sample with
  RMSSD ≥50 % below baseline — no hold window, no HR check, no LF/HF check. (It *is* contact-gated via
  the `NotDetected` early-return, so that is not the gap.)
- **Clinical consequence / mechanism:** A 50 % RMSSD *drop* means beats became more regular. The benign
  cause is transient **regularization of respiratory sinus arrhythmia** — a breath-hold, a Valsalva
  (straining/lifting), talking, or a paced-breathing exercise — on a noisy 60-s window with few beats.
  (Note: ectopics that slip the filter *raise* RMSSD, so they are not the driver.) A single such sample
  fires a real alert with no confirmation.
- **Likelihood:** Occasional.
- **Classification: true gap** — the only firing path with neither a sustain requirement nor a second
  corroborating axis.

### E. No minimum-N for short-window reliability — **MEDIUM**

- **Code fact:** Samples emit once `_shortWindow.Count ≥ 2` (`ShortWindowHrvCalculator.cs:61-64`). RMSSD
  from a single difference is meaningless and volatile.
- **Consequence:** During sparse data or post-dropout recovery, a 2–3-beat RMSSD can swing wildly and trip
  the unguarded ≥50 % path (Finding D), or distort the EWMA.
- **Classification: true gap** — no beat-count floor for metric validity.

### F. RMSSD/pNN50 are computed across temporal gaps — **LOW-MEDIUM**

- **Code fact:** The short window is time-evicted (`ShortWindowHrvCalculator.cs:77-90`); successive
  differences are taken between adjacent beats in the buffer regardless of any time gap between them. A
  beat just before a dropout and the first beat after are treated as "successive."
- **Consequence:** A spurious large successive difference across a gap inflates RMSSD. Partly mitigated by
  contact-gating *when the sensor reports contact*.
- **Classification: true gap** — no discontinuity reset of the difference chain.

### G. Artifact filter can get a "stuck median" on an abrupt sustained step — **MEDIUM (down-calibrated)**

- **Code fact:** Rejected beats are never added to the median buffer
  (`MeltdownMonitor.Core/Beats/RrArtifactFilter.cs:25-34`); `Reset()` runs only on BLE (re)connect
  (`MeltdownMonitor.Ble.Windows/PolarHrSource.cs:147`, `MeltdownMonitor.Ble.Apple/PolarHrSource.cs:170`).
- **Deterministic trace (confirmed):** stable stream `800, 810, 790` → buffer `[800,810,790]`, median 800.
  Then a sustained `590` (≈102 bpm, a 26 % step): `|590−800|/800 = 26.25 % > 25 %` → rejected and **not
  pushed**. Every subsequent `590` is still measured against median 800 → rejected indefinitely until
  `Reset()`.
- **Honest likelihood:** *Low* as a "blind during meltdown" mechanism — real HR change is rate-limited by
  SA-node dynamics, so a genuine escalation is a ramp (~1 %/beat) that the median tracks. The realistic
  trigger is a **resumed stream after a within-connection gap** (a non-contact-reporting sensor briefly
  off-body, or a notification gap) landing at a different HR, which `Reset()` does not cover.
- **Classification: latent robustness gap.**

### H. Unvalidated predictive claim, with the validation data already on disk — **MEDIUM (claims/ethics)**

- **Fact:** The README claims alerts appear *"seconds to minutes before the person consciously registers
  it"* — a sensitivity/lead-time claim with no evidence path. Yet the app persists `annotations`
  (Fine/Edged/Escalating/Blown) alongside `hrv_samples`/`alerts` — i.e. it already collects the ground
  truth needed to measure whether alerts actually precede self-reported escalation, and by how much.
  Nothing computes it.
- **Classification: claims gap + constructive opportunity.**

---

## Clinical synthesis — the dominant risk is false reassurance / over-reliance

Findings **A, B, C, and G converge on one failure mode: the app stays silent or shows "calm/REST" while
the user is actually dysregulated** — most acutely in hypoarousal/shutdown (A), after a cold-start
mis-calibration (B), during an early sympathetic ramp that LF/HF hasn't caught up to (C), or when the
stream is frozen post-gap (G).

For users who are encouraged to trust this tool *over* their own felt sense — and who may have alexithymia
or dissociation that already erodes interoceptive trust — a confident false "you're fine" is **worse than
no signal**: it can override a nascent felt sense of distress and delay coping. The "not a medical device"
disclaimer manages legal liability; it does **not** address this UX-level reliance dynamic. This should be
the framing that ties the individual findings together.

---

## Recommendations (priority order)

1. **Model hypoarousal as its own axis (A).** Add a low-arousal signature (e.g. HR below baseline beyond a
   band, with sustained low SDNN/quality) and a corresponding cool-lobe-but-dysregulated state. At minimum,
   stop rendering low arousal as "REST/regulated," and add a "this can miss shutdown/freeze — trust your
   body" message. Extend `AnnotationLabel` with a hypoarousal term so ground truth can capture it.
2. **Gate calibration on a rest precondition (B).** Don't accept a cold-start baseline from an elevated-
   arousal opening; require a quiescent stretch or flag "calibrating from possibly-activated state — low
   confidence." Revisit seeding the parasympathetic baseline from non-RMSSD HealthKit HR.
3. **Reconsider the corroboration default (C).** Either make LF/HF corroboration *additive* (it can raise
   confidence but not veto a warning), or default it off, given it suppresses the early-warning core
   feature.
4. **Add a sustain + corroboration requirement (or a higher bar) to the immediate ≥50 % path (D)** and a
   **minimum-beat-count floor** before any sample drives detection (E).
5. **Reset the successive-difference chain and the artifact median across detected gaps (F, G)**, not only
   on reconnect.
6. **Close the loop on the predictive claim (H):** compute alert lead-time vs. annotations from the data
   already stored, and either substantiate or soften the README claim. This is the cheapest high-value win
   and turns the app into its own validation instrument.
