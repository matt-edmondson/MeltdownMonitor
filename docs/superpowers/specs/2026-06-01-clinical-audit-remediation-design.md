# Clinical Audit Remediation — Design Spec

**Date:** 2026-06-01
**Source:** `docs/clinical-audit.md` (2026-06-01 clinical audit).
**Status:** design for an autonomous first pass. The user asked for as much progress as possible
without intervention, so this spec makes documented decisions rather than asking questions, and
**explicitly tiers** the work into what is safe to implement unilaterally versus what is a consequential
clinical/product decision that must wait for the user's sign-off.

---

## Guiding principle

Every audit finding fails in the same direction — **the app can stay quiet, or show "calm," while the
user is dysregulated.** Remediation therefore prioritises *not making confident false-calm claims*. Where
a fix is additive and clearly correct, implement it. Where a fix changes what fires an alert for every
existing user, or changes the product's vocabulary/UI, keep the current behaviour as the default, expose
the safer behaviour behind a switch, and recommend the flip — but let the user own that flip.

## Decision tiers

| Tier | Meaning | Findings |
|---|---|---|
| **1 — Implement now** | Additive, low product-risk, fully Core-testable, no change to steady-state behaviour. | H, E, F, G, A(a) |
| **2 — Implement default-safe** | Behaviour-changing, but shipped behind a new option whose default preserves today's behaviour. The user flips the default. | C, D |
| **3 — Spec only, await sign-off** | New alerts/states/vocabulary/UI; unvalidated signature; touches untested heads. | A(b), A(c), B |

---

## Tier 1 — implement now

### H. Detection efficacy analyzer (audit rec 6) — *highest value, zero risk*

**What:** a pure Core class `DetectionEfficacyAnalyzer` that, given the alerts and the self-check-in
annotations already on disk, measures whether alerts actually precede felt escalation and by how long.

**Why:** the README claims alerts fire "seconds to minutes before the person consciously registers it"
with no evidence path, yet `alerts` and `annotations` together are exactly the ground truth needed. This
turns the app into its own validation instrument and changes no runtime behaviour.

**Interface (Core, no DB dependency — operates on lists so it is unit-testable):**

```csharp
public sealed record AlertEfficacyResult(
    int EscalationAnnotations,        // count of Escalating/Blown check-ins considered
    int PrecededByAlert,              // how many had an alert within the lead window before them
    double Sensitivity,               // PrecededByAlert / EscalationAnnotations (0 if none)
    TimeSpan? MedianLeadTime,         // median (annotation - nearest preceding alert), null if none
    int AlertsWithNoFollowingEscalation); // alerts not followed by an escalation within the window (false-alarm proxy)

public static class DetectionEfficacyAnalyzer
{
    public static AlertEfficacyResult Analyze(
        IReadOnlyList<DateTimeOffset> alertTimes,
        IReadOnlyList<AnnotationRecord> annotations,
        TimeSpan leadWindow);   // e.g. 10 minutes
}
```

**Decisions:**
- "Escalation" = `AnnotationLabel.Escalating` or `Blown`. `Edged` is borderline and excluded from the
  strict sensitivity metric (documented; can be added later).
- Lead window default 10 min, caller-supplied so it stays policy-free.
- Operates on plain lists. A thin DB-backed convenience (`MeltdownRepository.ReadAlerts` if missing) can
  feed it, but the analysis logic itself never touches SQLite.
- Pure/deterministic → no `DateTime.Now`, all inputs supplied.

### E. Minimum-beat floor for short-window metrics

**What:** `ShortWindowHrvCalculator` gains `MinBeatsForMetrics` (default **5**); `AddBeat` does not emit a
sample until the short window holds at least that many beats.

**Why:** today a sample emits from as few as 2 beats (one successive difference) — a meaningless, volatile
RMSSD that can trip the unguarded ≥50 % immediate path. A 60 s window at any normal HR holds dozens of
beats, so a floor of 5 never affects steady state; it only suppresses garbage from sparse/post-dropout
data.

**Decisions:**
- Default 5 is a conservative floor, not a clinically "reliable" N (which is ~20+); raising it further
  trades responsiveness for stability and is left tunable. Documented as such.
- The pure static `ComputeRmssd`/`ComputePnn50` helpers are unchanged (callers may use them directly).

### F. Reset the successive-difference chain across temporal gaps

**What:** `ShortWindowHrvCalculator` tracks the previous beat's timestamp; when a new beat arrives more
than `MaxBeatGapSeconds` (default **5 s**) after it, the rolling windows are cleared before the new beat
is added, so no successive difference bridges a dropout.

**Why:** RMSSD/pNN50 are computed between adjacent beats in the buffer regardless of the time between
them. A beat just before a dropout and the first after it are currently treated as "successive," which
injects a spurious large difference and inflates RMSSD.

**Decisions:**
- 5 s threshold is longer than any physiological RR (`MaxRrMs` = 2 s), so only genuine gaps trip it.
- Clearing (rather than excluding the single bridging diff) is simpler and strictly correct: post-gap you
  want fresh data. It re-incurs the `MinBeatsForMetrics` warm-up, which is the desired behaviour.
- Extended (5-min) window is cleared on the same gap for consistency.

### G. Artifact-filter staleness escape

**What:** `RrArtifactFilter` recovers from a "stuck median." After `MaxConsecutiveRejections` (default
**4**) consecutive in-absolute-bounds beats are rejected by the relative-median rule, treat it as a
regime shift: clear the median window and accept the next in-bounds beat, re-seeding the median.

**Why (confirmed by deterministic trace in the audit):** rejected beats never enter the median buffer, so
a sustained abrupt >25 % step (most realistically a resumed stream after a within-connection gap on a
non-contact-reporting sensor) is rejected *forever* until BLE reconnect. There is currently no escape.

**Decisions:**
- A lone ectopic (1 outlier) is still rejected — the escape only fires after a *run*, so genuine artifact
  rejection is preserved.
- Absolute bounds (300–2000 ms) still always apply; the escape never admits physiologically impossible
  intervals.
- 4 consecutive ≈ a couple of seconds of data — long enough to distinguish a sustained regime from noise.

### A(a). Hypoarousal *display* signal — the safe slice of the headline finding

**What:** `RegulationReading` gains an init-only `Hypoarousal` field in `[0,1]`, computed by
`RegulationFieldCalculator`. It rises when HR has fallen meaningfully below baseline **and** variability
is not elevated — i.e. low-arousal collapse, distinct from genuine high-vagal rest.

**Why:** the single signed arousal index maps *any* below-baseline HR to the cool REST lobe, so a
hypoaroused/shutdown user is rendered as *more regulated*. Surfacing a distinct hypoarousal signal lets
the renderers stop presenting collapse as calm. Adding it as an **init-only property** (not a positional
record-struct parameter) keeps every existing `new RegulationReading(...)` call site compiling and
defaults to 0.

**Provisional heuristic (documented as needing validation):**

```
hrFall      = (baselineHr - meanHr) / baselineHr            // + when HR below baseline
hypoarousal = clamp((hrFall - HypoHrBand) / HypoHrSpan, 0, 1) * (1 - clamp(rmssd/baseline - 1, 0, 1))
```

with `HypoHrBand` ≈ 0.10 (only below-baseline beyond 10 % counts) and `HypoHrSpan` ≈ 0.15 (saturates by
~25 % below). The `(1 - …)` factor suppresses the signal when RMSSD is *above* baseline (true relaxed
rest), so it flags the collapse pattern, not ordinary calm.

**Scope boundary:** A(a) is *display data only*. It does **not** drive the detector or fire alerts (that
is A(b), Tier 3). Renderers consuming the new field (`App/Regulation/RegulationFieldView.cs`,
`Mobile/Controls/RegulationField.cs`) are untested heads, so this spec only guarantees the Core field +
calculator + tests; renderer wiring is listed as a follow-up and flagged, not silently shipped.

---

## Tier 2 — implement default-safe (user owns the flip)

### C. LF/HF corroboration: veto → additive (audit finding C)

**Problem:** corroboration (default ON) makes Warning require core **AND** LF/HF ↑≥50 %. LF/HF comes from
a 5-minute window, so during a fast onset it lags and *suppresses* the early Warning that the 60 s signal
already justified — undercutting the whole "early warning" value proposition.

**Design:** add `LfHfCorroborationMode { Veto, Additive }` to `DetectionThresholds`, default **`Veto`**
(today's behaviour). In `Additive` mode, an unmet LF/HF condition no longer blocks a core-satisfied
Warning; LF/HF only contributes to confidence/strength (surfaced via the regulation reading, not the gate).

**Why a switch, not a flip:** changing the default alters detection for every user. The audit recommends
Additive; the user makes that call. Default-safe means existing tests and behaviour are untouched.

### D. Immediate ≥50 % path: optional confirmation (audit finding D)

**Problem:** the only firing path with neither a sustain requirement nor a second axis. A single noisy
sample (e.g. a breath-hold transiently regularising RSA) fires a real alert.

**Design:** add `SevereDropConfirmationCount` to `DetectionThresholds`, default **1** (today: fire on the
first qualifying sample). With value 2, the ≥50 % drop must hold across that many consecutive in-contact
samples (~10 s at 5 s cadence) before firing — long enough to reject a transient breath-hold, short
enough not to meaningfully delay a genuine severe event.

**Why a switch, not a flip:** raising it trades a small added latency on truly severe events for fewer
false alarms. Default 1 preserves behaviour; recommend 2.

---

## Tier 3 — spec only, await sign-off

These change what alerts fire, the product's vocabulary, or untested heads, and/or rest on an unvalidated
physiological signature. Designs are recorded so the user can approve and a follow-up plan can implement.

### A(b). Hypoarousal detection state + alert path
Add a low-arousal detection branch and surface it (likely a new `DetectorState` member and/or a distinct
alert reason). **Blocked on:** the hypoarousal signature is unvalidated (the A(a) heuristic is a starting
point, not a calibrated detector), and this adds *new alerts* for every user. Validate A(a) against real
data (using H) before arming it.

### A(c). Hypoarousal self-report label
Append `AnnotationLabel.Shutdown` (or "Numb"). **De-risked:** labels persist as case-insensitive strings,
so appending a member is fully backward-compatible — no ordinal/migration hazard. **Blocked on:** product
wording choice + dialog wiring in both (untested) heads, and it should land *with* A(b) so the
ground-truth vocabulary matches what the detector can see.

### B. Cold-start "calibrate-during-symptom" guard
On a cold start with no history, the first sample becomes the baseline; if the user is already
dysregulated, the baseline normalises the symptom and the detector goes blind. **Design options:** seed
the cold baseline from the median of the warm-up window rather than the first sample; and/or surface a
"calibrating from a possibly-activated state — low confidence" state. **Blocked on:** it changes baseline
values and warm-up semantics (interacts with `IsWarm`/`WarmUpProgress` and both pipelines), and the
mobile HealthKit `WarmStartAsync` RMSSD-from-sparse-HR approximation should be addressed in the same pass.

---

## Testing strategy

- All Tier 1/2 logic lives in Core and is covered by MSTest in `MeltdownMonitor.Tests` (Core + Mobile
  only, no BLE/DB needed), per repo conventions: tabs, file-scoped namespaces, semantic asserts.
- New tests: `DetectionEfficacyAnalyzerTests`, additions to `HrvCalculatorTests`/new
  `ShortWindowHrvCalculatorTests` for E/F, additions to `RrArtifactFilterTests` for G,
  `RegulationFieldCalculatorTests` for A(a), `DetectionStateMachineTests` for C/D (asserting **defaults
  preserve current behaviour**, plus the opt-in paths).
- Gate: `dotnet build MeltdownMonitor.Core` and `dotnet test MeltdownMonitor.Tests` clean, warnings-as-
  errors (repo `Directory.Build.props`).

## Out of scope (this pass)
- Renderer (ImGui/Avalonia) wiring of the hypoarousal field — follow-up, untested heads.
- Any change to VERSION/CHANGELOG (auto-generated).
- iOS/App pipeline behavioural changes beyond reading new default-safe options.
