# Shutdown / Hypoarousal Visualization — Design

**Date:** 2026-06-02
**Status:** Approved (brainstorm), pending implementation plan
**Branch:** `claude/shutdown-hypo-visualization`
**Relates to:** `docs/clinical-audit.md` finding **A** (the detector/display are single-axis; hypoarousal is invisible and shown as "calm"). Tier 3 (PR #57) closed the *latched-state* indication; this closes the *trajectory* (movement-toward) gap on the low-arousal axis.

---

## Problem

The Regulation Field is a horizontal figure-8 ("window of tolerance"): the marker's **X** = signed arousal `Index` (cool/left ↔ warm/right), its **Y** = vagal tone (`VariabilityQuality`: low HRV rides **up** = FRAGILE, high HRV rides **down** = STEADY).

Tier 3 added a **latched** shutdown indication (a Lavender `SHUTDOWN` tag on desktop, a "Low arousal · shutdown — not calm rest" line on mobile), driven by the debounced `HypoarousalDetector` reaching `HypoarousalState.LowArousal`. That part works.

Two gaps remain on the *approach* to shutdown — the period before the detector latches:

1. **The continuous `Hypoarousal` scalar drives no visual.** `RegulationReading.Hypoarousal ∈ [0,1]` (computed via `HypoarousalSignal.Compute`, shared with the detector) is interpolated into the comet trail but rendered nowhere. So a developing shutdown shows only as the marker drifting toward the cool/REST lobe — geometrically indistinguishable from genuine calming.
2. **The velocity arrow actively mis-cues it.** `RegulationDynamics.Velocity` is `d(Index)/dt` (`+` = escalating). Sliding into shutdown lowers `Index`, so `DrawVelocityArrow` renders a calming **Sky "de-escalating"** arrow pointing toward REST — reinforcing the false-calm exactly when the user is collapsing.

Both gaps push in the audit's "dominant risk" direction: the app reads *calm* while the user is dysregulated.

## Goal & scope

Make low arousal a **first-class axis** on the field via a **quadrant treatment** — without replacing the signature figure-8. Land on **both heads** (desktop ImGui + mobile Avalonia) over a shared, tested Core change.

Key physiological basis for the quadrant choice: **rest and shutdown both sit on the cool/left side** (both have HR below baseline → negative `Index`), but they already separate vertically — genuine rest is **cool + steady** (lower-left), shutdown is **cool + fragile** (upper-left). The instrument already pulls them apart; it just never *labels or cues* the difference.

## Decisions (settled in brainstorm)

| Decision | Choice |
|---|---|
| Geometry | **Quadrant treatment** — label/shade the cool+fragile (upper-left) region as SHUTDOWN, distinct from cool+steady (lower-left) TRUE REST. No new lemniscate geometry. |
| Approach cue | **Deepening zone + marker halo** — both scale with the continuous `Hypoarousal` scalar. |
| Velocity arrow | **Hypoarousal-aware** — a slide into collapse draws a collapse-coloured WARNING arrow toward the shutdown zone, not a calming Sky arrow. |
| Collapse colour | **A distinct collapse hue** (dim, desaturated slate/indigo — reads *cold/withdrawn/frozen*), reused across zone + halo + arrow + latched tag. **Leave Lavender on the WoT ellipse + crossover.** Exact shade live-tuned. |
| Scope | **Both heads**; Core change done once and tested. |

## Architecture

### Core (shared, unit-tested — no render risk)

- **Second `RegulationVelocityTracker` instance for the `Hypoarousal` scalar.** The tracker is signal-agnostic (it tracks any scalar's rate of change → `RegulationDynamics{Velocity, Trend, NormalizedSpeed}`). Both pipelines already run one on `reading.Index`; add a second fed `reading.Hypoarousal`, exposed as `LatestHypoarousalDynamics` (desktop polled property; mobile event, mirroring how `LatestDynamics`/`DynamicsUpdated` work today). **No change to the tracker class.**
  - For the hypoarousal tracker, `Trend.Escalating` means *the collapse signal is rising* (deepening toward shutdown) — that is the warning condition the arrow keys off.
  - **Reset both trackers together** at the existing reset sites (sensor off-contact, baseline not warm) so a resumed stream never produces a spurious spike.
- Reuse existing signals as-is: `RegulationReading.Hypoarousal` (zone + halo intensity), `HypoarousalState.LowArousal` (latched tag/alert). No new Core types beyond wiring the second tracker.

### Shared visual language

- **Collapse colour:** a new palette entry (dim slate/indigo) used for the shutdown zone fill, the marker's hypoarousal halo, the hypoarousal warning arrow, and the (recoloured) latched tag. The WoT ellipse and crossover dot keep Lavender — collapse gets its own identity, and the regulated-zone visuals are untouched.
- **Marker halo layering:** the existing **inner** halo keeps pulsing at HR in the **state** colour. The hypoarousal halo is an **outer, non-pulsing** ring in the **collapse** colour, radius/opacity scaling with the `Hypoarousal` scalar. Distinct colour + layer + motion, so the two never blur.
- **Shutdown zone:** a fill over the **upper-cool** region of the field (cool side, above the steady/crossover line), opacity = `Hypoarousal` scalar. Faint on approach, vivid near latch.

### Desktop — `MeltdownMonitor.App/Regulation/RegulationFieldView.cs`

- Split the cool-side label into **SHUTDOWN** (upper-cool) and **REST** (lower-cool); keep **WINDOW OF TOLERANCE**.
- New `DrawShutdownZone` (collapse-colour fill over the upper-cool region; opacity = scalar), drawn in the zone layer (with `DrawWindowOfTolerance`, before the lemniscate).
- `DrawMarker`: add the outer hypoarousal halo (collapse colour, scaled by scalar) outside the existing pulsing state halo.
- `DrawVelocityArrow`: when `LatestHypoarousalDynamics.Trend == Escalating` and the scalar is above a floor, draw the collapse-colour warning arrow toward the shutdown zone instead of the Index-derived Sky de-escalation arrow.
- Recolour the existing latched `SHUTDOWN` tag to the collapse hue; keep the cold-calibration badge.

### Mobile — `MeltdownMonitor.Mobile/Controls/RegulationField.cs` + `ViewModels/NowViewModel.cs` + `Views/NowView.axaml`

- Mirror the desktop: shutdown-zone fill, outer hypoarousal halo (layered over the `RegulationFieldAnimator` HR-pulsed state halo), hypoarousal-aware arrow branch.
- Add the REST/SHUTDOWN cool-side labels for parity; recolour the existing "not calm rest" line to the collapse hue.
- `NowViewModel` consumes `LatestHypoarousalDynamics` (new event) alongside the existing `IsShutdown`.

### Halo-audit cleanups (in-scope, in the files we're already touching)

- **Rename the "breathing" halo to a pulse halo.** It pulses at **heart rate** (`_breathPhase += dt·max(40,HR)/60·τ`; mobile `_animator.HaloPulse`), not respiration. Fix the misleading `breath`/`breathing` naming in comments and field/animator names so it reads as the heartbeat-cadence indicator it is. Behaviour unchanged.
- **Fix the stale LF/HF halo comment.** `DrawLfHfHalo`'s comment says LF/HF is *"off by default"*, but `UseLfHfCorroboration` defaults **true** (2026-06-01 audit flip). Correct the comment. No behaviour change.

## Data flow

```
IBeatSource → … → RegulationFieldCalculator.Compute → RegulationReading{Index, …, Hypoarousal}
   ├─ _indexVelocity.Update(Index, ts)      → LatestDynamics
   └─ _hypoVelocity.Update(Hypoarousal, ts) → LatestHypoarousalDynamics   ← NEW
HypoarousalDetector.Process → HypoarousalState (latched)

Renderer (each head):
   shutdown zone opacity        ← reading.Hypoarousal
   marker outer halo            ← reading.Hypoarousal           (collapse colour)
   marker inner pulsing halo    ← HR + detector state           (unchanged)
   velocity arrow               ← if LatestHypoarousalDynamics rising & scalar>floor:
                                     collapse warning arrow toward shutdown
                                  else: existing Index arrow
   latched SHUTDOWN tag/line    ← HypoarousalState.LowArousal    (recoloured)
```

## Testing strategy

- **Core (unit-tested):** the second-tracker wiring in both pipelines — rising `Hypoarousal` → rising `LatestHypoarousalDynamics`; both trackers reset on contact-loss / not-warm; the cold→warm jump does not register as a spike (the tracker's existing seed behaviour covers this — assert it for the new instance). Mirror `RegulationVelocityTrackerTests` patterns.
- **Renderers (not unit-tested):** zones, halo layering, arrow re-cue, and the collapse colour are a **first cut to tune live with a real Polar sensor** — per the BLE/visual rule (`CLAUDE.md`), real-time visual/timing behaviour cannot be validated from tests. Treat shade, opacity ramp, halo radius, and arrow thresholds as tunable.

## Clinical humility (carry through copy & comments)

The hypoarousal signature is **provisional and HRV-only**: a dorsal-vagal shutdown that *preserves* HRV is indistinguishable from rest by HRV alone and will be missed. Nothing here should present a confident reading; the visualization makes a *candidate* collapse legible, it does not certify one.

## Out of scope / future

- Reworking the field to a vertical polyvagal ladder (considered, rejected — preserves the signature figure-8).
- A real respiration/coherence-breathing pacer (the current halo is HR, not breath; a true pacer is a separate feature and we don't compute respiration).
- Re-validating the `HypoarousalThresholds.EnterSignal` level — that is the separate `AnalyzeHypoarousal`-vs-real-`Shutdown`-data task.

## Risks

- **Visual busyness** on the marker (two halos + arrow + zone). Mitigated by distinct colour/layer/motion; final balance is a live-tuning judgement.
- **Collapse-vs-Lavender legibility** at low opacity against the dark overlay — live-tune the shade and minimum opacity.
- **Arrow flip-flop flicker** if the hypoarousal trend oscillates near the floor — reuse the tracker's existing EWMA smoothing + deadband, and gate the re-cue on a scalar floor.
