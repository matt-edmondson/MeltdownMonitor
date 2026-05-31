# Regulation Field — escalation / de-escalation velocity indicator

**Date:** 2026-05-31
**Status:** Design, approved in brainstorming (combined three-layer approach), pending spec review.

## Overview

Add a directional, velocity-aware indicator to the Regulation Field that shows, at a
glance, whether the user's arousal is **escalating** (moving toward the warm MELTDOWN
lobe), **de-escalating** (easing back toward the cool REST lobe), or **steady**, and
*how fast*. The marker already shows *where* arousal sits; this adds *which way it's
heading and how quickly* — the leading indicator that matters most for a "warn before
the meltdown" tool.

The quantity is the rate of change of the existing arousal index
(`RegulationReading.Index` ∈ [−1, 1]; + = sympathetic activation). Velocity is computed
**once** in a shared, unit-tested Core component and surfaced in **both** front-ends
(desktop ImGui `RegulationFieldView` and mobile Avalonia `RegulationField`) through three
complementary visual layers that read as one coherent "comet":

1. **Comet trail (behind the marker)** — *where you've been.* Existing trail; its leading edge brightens/stretches with speed and is tinted by direction.
2. **Directional arrow (ahead of the marker)** — *where you're heading, how fast.* New arrowhead along the major axis; length + opacity ∝ speed; hidden inside a deadband; warm (escalating) / cool (de-escalating) tint.
3. **Readout (chrome, outside the lemniscate)** — *the exact words + number.* Trend glyph + word + signed rate + a small magnitude bar.

**Consistent semantics everywhere:** warm = escalating, cool = de-escalating, neutral = steady. This reuses the field's existing warm/cool lobe palette.

## Goals

- A single source of truth for "velocity" and "trend", computed in Core and unit-tested.
- Both renderers gain the same indicator, sharing the Core computation; only the draw differs.
- Robust to the realities of the pipeline: batched/irregular samples, baseline warm-up, and the cold→warm `Index` jump (no spurious spike).
- No regression to the existing field behaviour (marker, trail, trace, halo, recovery target).

## Non-goals / out of scope

- Velocity of anything other than the arousal index (not HR-slope, not LF/HF trend). The marker *is* the index; its velocity is the natural quantity.
- A new overlay HUD metric for velocity on desktop, and any change to the detection state machine. (Both are noted as possible follow-ups.)
- Predictive "time to threshold" estimates. (Possible follow-up; this spec is descriptive only.)

## Core model (`MeltdownMonitor.Core/Regulation`)

### `RegulationTrend` (enum)

```
DeEscalating,   // arousal falling toward REST
Steady,         // within the deadband
Escalating,     // arousal rising toward MELTDOWN
```

### `RegulationDynamics` (readonly record struct)

```
double Velocity;          // signed d(Index)/dt, index-units per second (+ = escalating)
RegulationTrend Trend;    // tri-state, derived via a deadband
double NormalizedSpeed;   // |Velocity| mapped to [0,1] for driving visual magnitude
```

`RegulationReading` stays a pure single-sample value (no history). Dynamics is a separate
value because velocity is inherently stateful (needs the previous reading + timestamp).

### `RegulationVelocityTracker` (stateful class)

```
RegulationDynamics Update(double index, DateTimeOffset timestamp);
void Reset();
```

Behaviour:
- **First update (or first after `Reset`)**: store `(index, timestamp)`, return `Steady`, `Velocity = 0`, `NormalizedSpeed = 0` — *seed, do not emit*. This is what prevents a spurious spike when the baseline flips cold→warm and `Index` jumps from 0 to its real value.
- **Subsequent updates**: `dt = clamp((timestamp − prevTimestamp).TotalSeconds, MinDtSeconds, MaxDtSeconds)`; `rawVelocity = (index − prevIndex) / dt`; EWMA-smooth: `velocity = α·rawVelocity + (1−α)·velocity` (smooths over a couple of batched samples so a single noisy reading doesn't twitch the arrow).
- **Trend** via deadband: `velocity > Deadband → Escalating`; `velocity < −Deadband → DeEscalating`; else `Steady`. The deadband gives hysteresis so the trend doesn't flicker around zero.
- **NormalizedSpeed** = `clamp(|velocity| / ReferenceSpeed, 0, 1)`.
- Non-finite `index`/`dt` guards: treat as a no-op that returns the last dynamics (or Steady/0 before any seed).

### Tuning constants (initial values; **validate against a live sensor** — per the project's "BLE is batched, real-time behaviour is only gated by the live app" rule)

| Constant | Initial | Meaning |
|---|---|---|
| `α` (EWMA) | 0.5 | Smoothing of the per-sample derivative (~2-sample memory at the 5 s cadence) |
| `Deadband` | 0.01 /s | Below this |velocity|, trend is Steady (≈1% of the field per second) |
| `ReferenceSpeed` | 0.05 /s | |velocity| that maps to full visual magnitude (≈ crossing baseline→saturate in ~20 s) |
| `MinDtSeconds` / `MaxDtSeconds` | 0.5 / 30 | dt clamp, matching the desktop view's existing interval clamp |

These live as defaults on the tracker and are not user-facing in v1.

## Pipeline integration

Each head's `Pipeline` owns one `RegulationVelocityTracker` and updates it on the same
timeline it already computes `LatestReading` (App `Pipeline.cs` RunAsync ≈ L187; Mobile
`Pipeline.cs` RunAsync ≈ L230), exposing `RegulationDynamics LatestDynamics { get; }`.

Gating to avoid garbage and the cold→warm spike:
- Update the tracker **only when the baseline is warm** (`_baseline.IsWarm`). While calibrating, leave dynamics at the default `Steady`/0.
- On the **first warm sample** (warm just became true, or after a contact-loss gap), the tracker's seed-don't-emit behaviour yields `Steady`/0 — no spike.
- On a `SensorContactStatus.NotDetected` sample (RR untrustworthy — the detector already holds state here), **do not update** the tracker and hold the last dynamics; `Reset()` the tracker's `previous` so the next in-contact sample re-seeds rather than computing across the gap.

Mobile additionally fires its existing `ReadingUpdated` per sample; the dynamics ride
alongside via `LatestDynamics` (the `NowViewModel` reads it when handling that event — same
timeline). No change to the event signature is required.

## Renderer integration — desktop (`MeltdownMonitor.App/Regulation/RegulationFieldView.cs`)

The view reads `_pipeline.LatestDynamics` (it already holds the `Pipeline` and reads
`Baseline`, `LatestThresholds`, `LatestSample`). The **trend** (direction) switches
discretely (deadband gives hysteresis); the **displayed arrow magnitude** is eased with a
small inline exp-ease float toward `NormalizedSpeed`, consistent with the existing
`_hrDisplay` easing, so it grows/shrinks smoothly rather than stepping per sample.

1. **Arrow** — new `DrawVelocityArrow`, called from `Draw()` after `DrawMarker`. Emanates from the marker's drawn position (including its vagal-tone Y offset) horizontally: +X when Escalating, −X when De-escalating. Length = `markerRadius + easedSpeed · maxArrowLen`; opacity ∝ confidence · easedSpeed. Tint: warm (`Peach`/`Maroon`) escalating, cool (`Sky`/`Sapphire`) de-escalating. Not drawn when Steady or while calibrating (`confidence < ~1`).
2. **Trail** — in `DrawTrail`, scale the leading-edge width/alpha by `easedSpeed` and blend the head colour toward the trend tint. Subtlest layer; must not overpower the arrow.
3. **Readout** — extend `DrawReadout` (bottom-left) with a trend glyph + word + signed rate (e.g. `▲ escalating  +0.03 /s`) and a short magnitude bar. Steady shows `– steady`. Hidden/disabled while calibrating, matching the existing "Waiting for beats…" gate.

## Renderer integration — mobile (`MeltdownMonitor.Mobile`)

- **`RegulationField` control**: add a `RegulationDynamics Dynamics` styled property (registered in `AffectsRender`). `DrawMarker` gains the same horizontal arrow; `DrawTrail` gains the same leading-edge tint/scale. Arrow easing is added to the pure, **unit-testable** `RegulationFieldAnimator` as an eased "displayed speed" toward `Dynamics.NormalizedSpeed`, mirroring its existing `MarkerPos` easing.
- **`NowViewModel`**: expose the dynamics for binding — set the control's `Dynamics`, and expose readout strings/values (`TrendLabel`, `VelocityText`, `NormalizedSpeed`) derived from `pipeline.LatestDynamics`. These string/derivation getters are themselves unit-testable.
- **`NowView.axaml`**: bind the control's `Dynamics`, and add a compact readout (glyph + word + rate + a thin bar) near the field — consistent with the desktop readout.

## Visual semantics (shared, both renderers)

- Direction: arrow toward MELTDOWN (escalating) / REST (de-escalating); none when Steady.
- Colour: warm escalating, cool de-escalating, neutral/hidden steady — same palette as the lobes.
- Magnitude: arrow length + opacity, trail leading-edge brightness, and the readout bar all driven by the one `NormalizedSpeed`.
- Calibration: the whole indicator hides while the baseline is warming (reuse the existing confidence dimming/gate), so it never implies a trend it can't yet measure.

## Testing

**Core (`MeltdownMonitor.Tests`, fully covered):**
- `RegulationVelocityTracker`: first update → Steady/0 (seed, no emit); rising index over successive timestamps → Escalating with positive velocity matching Δindex/Δt after EWMA; falling → De-escalating; sub-deadband change → Steady; `dt` clamp at both ends; `NormalizedSpeed` clamped to [0,1] and monotonic in |velocity|; `Reset()` then update → Steady/0 (no spike); non-finite guards.
- `RegulationFieldAnimator`: displayed-speed easing converges toward target, clamps long `dt`, no-ops on non-positive `dt` (mirrors existing `MarkerPos` tests).
- `NowViewModel`: `TrendLabel`/`VelocityText`/`NormalizedSpeed` map correctly from a given `RegulationDynamics`.

**Per-renderer draw code:** build + the live app on a real Polar sensor (the only gate for real-time visual/timing behaviour, per the project rule). No claim of visual correctness from tests alone.

## Risks & edge cases

- **Cold→warm `Index` jump** → handled by seed-don't-emit + warm-gating.
- **Contact loss / batched gaps** → tracker not updated off-contact; `Reset()` re-seeds on return; `dt` clamp bounds a long gap.
- **Trend flicker near zero** → deadband hysteresis.
- **Arrow vs trail double-encoding** → positioned on opposite sides of the marker (trail behind, arrow ahead), arrow length capped, trail enhancement kept subtle.
- **Tuning constants are guesses** → must be validated/adjusted on a live sensor; they are isolated defaults on the tracker so tuning is a one-line change.

## Follow-ups (not in this spec)

- A selectable velocity/trend **overlay HUD metric** on desktop (`OverlayMetric`).
- Predictive "time to Warning boundary" hint.
- Optionally feeding trend into the detector (e.g. faster escalation → shorter Warning hold). Detection-logic change; out of scope here.
