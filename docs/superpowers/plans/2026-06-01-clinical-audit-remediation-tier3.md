# Clinical Audit Remediation — Tier 3 Plan & Session Handoff

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**This document is a handoff.** A prior session completed the clinical audit, Tier 1, and Tier 2
(default-safe) remediations, then merged `origin/main`. This plan covers the remaining work, now
**in scope** because the user directed: *"consider today's behaviour is best effort but ultimately be
guided by clinical best practise."* That reverses the earlier default-preserving stance — **flip defaults
toward clinical best practice** and implement the hypoarousal/shutdown work that was previously deferred.

---

## Where things stand (start here)

- **Branch:** `claude/clinical-audit-remediation` (pushed to origin; merged up to latest `origin/main`).
- **Build/test loop (no BLE/DB needed):**
  ```
  dotnet build MeltdownMonitor.Core/MeltdownMonitor.Core.csproj
  dotnet test  MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj   # 269 tests currently green
  ```
  Windows App head compiles on Windows; iOS heads only build on macOS (`ios.yml` is their gate).
- **The audit:** `docs/clinical-audit.md`. **The design tiers:** `docs/superpowers/specs/2026-06-01-clinical-audit-remediation-design.md`. **Tier 1/2 plan (done):** `docs/superpowers/plans/2026-06-01-clinical-audit-remediation.md`.

### Done in the prior session (committed)
| Audit | What shipped | Where |
|---|---|---|
| H | `DetectionEfficacyAnalyzer` (alert lead-time / sensitivity vs annotations) | `Core/Detection/DetectionEfficacyAnalyzer.cs` |
| E | min-beat floor (`MinBeatsForMetrics`, default 5) | `Core/Hrv/ShortWindowHrvCalculator.cs` |
| F | gap reset (`MaxBeatGapSeconds`, default 5) + Mobile warm-start relaxation | `Core/Hrv/ShortWindowHrvCalculator.cs`, `Mobile/Pipeline.cs` |
| G | artifact-filter staleness escape (`MaxConsecutiveRejections`) | `Core/Beats/RrArtifactFilter.cs` |
| A(a) | `RegulationReading.Hypoarousal` scalar **computed in Core** (rendered nowhere yet) | `Core/Regulation/RegulationReading.cs`, `RegulationFieldCalculator.cs` |
| C | `LfHfCorroborationMode {Veto,Additive}` — **default still `Veto`** | `Core/Detection/DetectionThresholds.cs`, `DysregulationDetector.cs` |
| D | `SevereDropConfirmationCount` — **default still `1`** | `Core/Detection/DetectionThresholds.cs`, `DysregulationDetector.cs` |

### Merge note (important)
`origin/main` added a **`RecoveryProgress`** feature to `DysregulationDetector` (`_recovery`, the
`Recovery` property, `ComputeRecovery`/`BandProximity`, and a `RecoveryUpdated` event + `LatestRecovery`
on both pipelines). It coexists with the Tier 1/2 changes (resolved in merge `0812fca`). When adding the
hypoarousal detector, **mirror this `RecoveryProgress` pattern** for the low-arousal episode rather than
inventing a different shape — consistency matters and the renderers already consume recovery progress.

---

## Clinical-best-practice decisions for this tier

These are the design calls the prior session reasoned through. They are defensible defaults; revisit only
with evidence (the `DetectionEfficacyAnalyzer` is the validation instrument).

### 1. Flip C and D defaults (small, high-value)
- **C:** `LfHfCorroborationMode` default **`Veto` → `Additive`.** Rationale: the LF/HF signal comes from a
  5-minute window and lags a fast onset; letting it *veto* a core-satisfied Warning suppresses the
  early-warning value proposition. For an awareness tool, a missed early warning (false negative) is the
  more harmful error. LF/HF should *strengthen confidence*, not gate.
- **D:** `SevereDropConfirmationCount` default **`1` → `2`.** Rationale: the immediate ≥50 % path is the
  most false-positive-prone (a transient RSA regularisation — breath-hold, Valsalva — can momentarily
  collapse RMSSD). Two consecutive in-contact samples (~10 s) rejects those while only delaying a genuine
  severe event by ~one sample. Alarm fatigue (users disabling the tool) is a worse clinical outcome than
  ~5 s extra latency. **Do NOT add an HR-rise gate to the severe path** — it must stay HR-direction-
  agnostic so it can also catch an RMSSD collapse during a low-arousal crash.

**Test migration when flipping these (do this or the suite breaks):** `DetectionStateMachineTests`
`FastThresholds` fixture must explicitly pin `LfHfCorroborationMode = LfHfCorroborationMode.Veto` and
`SevereDropConfirmationCount = 1` so the existing state-machine *mechanics* tests (which feed a single
severe sample and expect immediate Alerting, and which assert veto suppression) keep their deterministic
behaviour. Then add one test `ProductionDefaults_FollowClinicalBestPractice` asserting
`new DetectionThresholds()` yields `Additive` and `2`. Rename `SevereDropConfirmation_DefaultOne_*`
(it now tests count-1 mechanics, not the default).

### 2. A(c) — add `AnnotationLabel.Shutdown` (the missing ground-truth word)
Append `Shutdown` to `Core/Persistence/AnnotationLabel.cs` (append at the END — labels persist as
case-insensitive strings, see `MeltdownRepository.InsertAnnotation`, so ordinal position is irrelevant and
old DBs are unaffected). The self-report vocabulary is currently single-axis (Fine/Edged/Escalating/Blown),
so even ground truth can't capture the low-arousal state. The check-in UIs auto-enumerate the enum (see
integration map §3) so they pick it up automatically — but review wording/ordering. Consider also
extending `DetectionEfficacyAnalyzer` to treat `Shutdown` as an escalation for hypoarousal-alert efficacy.

### 3. A(b) — hypoarousal/shutdown detection (the headline)
**Architecture: a separate `HypoarousalDetector` in Core**, mirroring `DysregulationDetector`'s shape
(state machine, contact + baseline-warm gating, events) but for the *lower* edge of the window of
tolerance. Keep the existing detector untouched (no regression risk); run both in each pipeline.

- **Signature (conservative, defensible from HRV):** sustained **HR meaningfully below baseline** AND
  **variability NOT elevated** (RMSSD at/below baseline). This is exactly the A(a) `Hypoarousal` scalar
  already computed. **Extract a shared pure helper** (e.g. `Core/Regulation/HypoarousalSignal.cs` or
  `Core/Detection`) returning `[0,1]` from `(rmssd, meanHr, baselineRmssd, baselineHr)`, and have BOTH
  `RegulationFieldCalculator` and `HypoarousalDetector` call it (DRY — today the formula lives inline in
  `RegulationFieldCalculator.cs`).
- **Why this signature and not more:** genuine relaxed rest is HR-down + HRV-**up**; collapse is HR-down +
  HRV-flat/down. That contrast is the only HRV-distinguishable hypoarousal pattern. Dorsal-vagal shutdown
  that *preserves* HRV is genuinely indistinguishable from rest by HRV alone — **do not try to detect it**;
  document the limitation. Be humble in the spec and in user-facing copy.
- **State machine:** mirror `RecoveryProgress`-era `DysregulationDetector`. Suggested:
  `HypoarousalState { Idle, Monitoring, LowArousal }` (or reuse the Watching/Warning vocabulary). Enter
  `LowArousal` after the signal exceeds an enter threshold sustained for a hold duration; exit after it
  clears for a recovery duration; cooldown to prevent chatter. Gate on `baselineIsWarm` and reset streaks
  on `SensorContactStatus.NotDetected` exactly like `DysregulationDetector`.
- **Thresholds:** add a `HypoarousalThresholds` record (or a section of `DetectionThresholds`) —
  enter/exit signal levels, hold, recovery, cooldown — serialisable with settings (System.Text.Json
  round-trips new members; the `MobileSettingsSerializerTests` round-trip is symmetric so it stays green).
- **Alert routing — GENTLER than a meltdown alert.** Jarring stimuli can deepen a shutdown (sensory
  overload). Add an alert *type/severity* so dispatchers can respond calmly. Recommended: add
  `AlertKind { Hyperarousal, Hypoarousal }` (or a severity flag) to `AlertPayload`
  (`Core/Detection/AlertPayload.cs`) — **additive, default `Hyperarousal`** so existing call sites and the
  persisted `alerts.trigger_reason` are unaffected. Route hypoarousal alerts through the same `AlertFired`
  event; let `AlertDispatcher` (desktop) and `MobileAlertDispatcher` choose a softer chime / non-
  interrupting notification for `Hypoarousal`. Honour existing mute/pause settings.
- **Baseline freeze during a hypoarousal episode:** the baseline must not re-normalise toward a shutdown.
  Today `BaselineHrvTracker.Update` freezes on `Warning/Alerting` (via `sample.State`). Gate the
  hypoarousal episode too — simplest is for each `Pipeline` to skip `_baseline.Update(...)` while the
  hypoarousal detector is in `LowArousal` (testable, no tracker contract change).
- **Validate before fully trusting:** run `DetectionEfficacyAnalyzer` (extended for `Shutdown` annotations)
  against real data to check the signature actually precedes felt shutdown.

### 4. B — cold-start "calibrate-during-symptom" guard
On a cold start with no anchor, the first sample becomes the baseline; if the user is already dysregulated,
the baseline normalises the symptom and the detector goes blind. **Fix:** seed the cold baseline from the
**median of the warm-up-window samples** rather than the first sample (buffer them in
`BaselineHrvTracker`, set baseline = median when warm-up completes), and surface a low-confidence/
"calibrating from a possibly-activated state" signal in the UI. Note the fundamental limit: if the *whole*
warm-up is symptomatic, no self-calibration can fix it — tell the user. Also revisit the Mobile HealthKit
`WarmStartAsync` RMSSD-from-sparse-HR approximation (it seeds a parasympathetic baseline from a non-
parasympathetic quantity). Watch interactions with `IsWarm`/`WarmUpProgress` and existing
`BaselineTrackerTests`/`BaselineSeedingTests`.

### 5. Display — stop rendering hypoarousal as the cool REST lobe
The single signed arousal index maps any below-baseline HR to REST, so a shut-down user is shown as *more
regulated*. Use the `Hypoarousal` field (and/or the new `HypoarousalState`) to make collapse visually
distinct (a distinct colour/badge or a third "shutdown" region — NOT the cool lobe). **Renderers are in
untested heads** — change carefully and compile-verify; `LemniscateGeometry`/calculator logic stays in
Core (testable).

---

## Integration map (from a read-only sweep — exact touch points)

**1. Alert dispatch.** Desktop: `App/AlertDispatcher.cs` `Dispatch(AlertPayload)` (chime ~33–51, toast
~53–68); wired in `App/Program.cs` (`pipeline.AlertFired += dispatcher.Dispatch`). Mobile:
`Mobile/Services/MobileAlertDispatcher.cs` `OnAlertFired(AlertPayload)` (~34–52) → `IChime.PlayAlertChime`,
`INotificationDispatcher.PostAlertAsync`. iOS impl: `iOS/Services/NotificationDispatcher.cs` (~58–79;
`AlertCategoryId`/`StatusCategoryId`, sound, interrupt level — add a hypoarousal category/sound here).

**2. DetectorState display.** Desktop: `App/TrayIcon.cs` `UpdateIcon`/`StateIconResources` (~31–38, 96–101),
`App/StatusWindow.cs` (~120), `App/Regulation/RegulationFieldView.cs` `DrawMarker`(~504–513)/`DrawLabelsAndLock`
(~614–639), `App/Regulation/MacchiatoPalette.cs` `State(DetectorState)`. Mobile: `Mobile/StateColors.cs`
(`ColorFor`/`BrushFor`/`LabelFor`/`HexFor`, constants ~13–18), `Mobile/ViewModels/NowViewModel.cs`
`State`/`OnStateChanged` (~127–141, 402), `Mobile/Views/NowView.axaml` state pill (~44–48),
`Mobile/Services/LiveActivityPublisher.cs` `BuildContent` (~62–77).

**3. Self check-in dialog (auto-enumerates the enum — appending `Shutdown` flows through).** Desktop:
`App/AnnotationDialog.cs` (~37 `foreach Enum.GetValues<AnnotationLabel>()`), launched from `App/TrayIcon.cs`
`OnLogFeeling` (~136–143); also `App/StatusWindow.cs` `DrawAnnotationsTab` (~912–929). Mobile:
`Mobile/ViewModels/NowViewModel.cs` `AnnotationLabels` (~296), `RecordAnnotationAsync` (~323–332);
`Mobile/Views/NowView.axaml` sheet (~122–163, `ItemsSource="{Binding AnnotationLabels}"`).

**4. RegulationReading consumers.** Desktop `App/Regulation/RegulationFieldView.cs`: `OnSampleUpdated`
(~64–102), `Draw`/`LerpReading` (~145–217, 431–436 — **`LerpReading` must interpolate the new
`Hypoarousal` field**), `DrawLemniscate`/`DrawMarker`/`DrawTrail`. Mobile `Mobile/Controls/RegulationField.cs`
(`ReadingProperty` ~43–45, render chain) and `Mobile/ViewModels/NowViewModel.cs` `Reading`/`OnReadingUpdated`
(~74–78, 430–446).

**5. Pipeline structure (add `HypoarousalDetector` alongside `_detector`).** Desktop `App/Pipeline.cs`
`RunAsync` (~159–232): `_hrv.AddBeat` (~192), `_baseline.Update` (~198 — gate during hypoarousal),
`_detector.Process` (~200), reading compute (~214–218). Mobile `Mobile/Pipeline.cs` `RunAsync` (~210–272):
`_baseline.Update` (~233 — gate), `_detector.Process` (~235), emits `ReadingUpdated`/`DynamicsUpdated`/
`RecoveryUpdated`. Mobile emits granular events; desktop exposes polled state + `BeatReceived`.

---

## Suggested task order (TDD, commit per task)
1. **C/D default flip** + test-fixture migration (above). Smallest, unblocks "best-practice defaults live".
2. **A(c)** append `AnnotationLabel.Shutdown` + a test; verify both check-in UIs enumerate it.
3. **Extract `HypoarousalSignal`** shared helper + tests; refactor `RegulationFieldCalculator` to use it (behaviour-preserving — existing `RegulationFieldCalculatorTests` stay green).
4. **`HypoarousalDetector`** + `HypoarousalThresholds` + thorough state-machine tests (mirror `DetectionStateMachineTests`).
5. **`AlertPayload.AlertKind`** (additive default Hyperarousal) + wire `HypoarousalDetector` into both pipelines (run, gate baseline, fire gentle alert); dispatcher softening.
6. **Display**: make hypoarousal visually distinct in both renderers (`LerpReading` Hypoarousal interpolation first); compile-verify heads.
7. **B** cold-start robust median seeding + low-confidence surfacing; revisit HealthKit warm-start.
8. Extend **`DetectionEfficacyAnalyzer`** for `Shutdown` to validate the new signature.

## Guardrails / caveats
- Tests cover Core + Mobile only; App/Ble.Windows compile-verify on Windows (`dotnet build MeltdownMonitor.App/...` — note a running app instance locks output DLLs; build to a temp `--output` to verify cleanly), iOS via `ios.yml`.
- Keep clinical humility in copy and docs: the hypoarousal signature is provisional and HRV-only; it will miss HRV-preserving shutdown. Surface uncertainty; never present a confident "calm" when confidence is low or data is stale.
- Don't hand-edit `VERSION.md`/`CHANGELOG.md`/`LICENSE.md`. Commit-tag versioning (`[minor]`/`[patch]`).
- No `Co-Authored-By` lines (user convention).
