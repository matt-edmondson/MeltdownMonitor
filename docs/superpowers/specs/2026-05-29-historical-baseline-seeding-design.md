# Historical Baseline Seeding & Long-Term Anchor — Design

Date: 2026-05-29
Status: Approved (pending spec review)

## Problem

`BaselineHrvTracker` is a fresh in-session EWMA. On every launch it starts empty,
re-runs a 10-minute warm-up before the detector arms, and only "remembers" ~15
minutes of data (`Alpha = 0.005`). It ignores the persisted sample history
entirely, so it is neither warm-started nor personalised to the user's longer-term
"normal." A sustained sub-threshold rough patch can also slowly re-normalise the
rolling baseline, blinding the detector to a genuine slow decline.

The data needed to fix this already exists: `MeltdownRepository.ReadHistory(path,
from, to)` returns persisted `HrvSample`s (already used by `StatusWindow` to
backfill sparklines).

## Goals

1. **Warm-start** the baseline at launch from recent clean history, skipping the
   cold 10-minute warm-up when enough recent data exists.
2. Maintain a **long-term personalised anchor** (the user's "normal") used as a
   **guardrail** that bounds how far the fast adaptive baseline may drift.

## Non-goals (YAGNI for v1)

- Time-of-day-aware baselines.
- Periodic in-session anchor recomputation (anchor is computed once per launch).
- A separately persisted baseline snapshot (the samples DB is the source of truth).

## Architecture

Keep the layering clean:

- **`BaselineHrvTracker` (Core)** stays database-agnostic. It gains a
  `SeedFromHistory(IReadOnlyList<HrvSample> history)` method that takes
  already-loaded samples and computes the statistics. All stat logic lives here
  and is unit-testable without a database.
- **`Pipeline` (App)** owns the `MeltdownRepository`. At startup (in `Start()`,
  before live samples flow) it calls `ReadHistory` for the last 7 days and passes
  the result to `_baseline.SeedFromHistory(...)`.

Detection is unchanged in shape: the detector still compares live values against
`BaselineRmssd` / `BaselineHr` / `BaselineLfHfRatio`, which remain the fast EWMA.

## Statistics (both robust — median)

From the supplied history, filtered to **clean** samples only:
- Exclude samples whose `State` is `Warning` or `Alerting` (do not learn "normal"
  from episodes).
- Exclude non-positive metric values.
- For LF/HF, use only samples with a positive `Extended.LfHfRatio`.

Compute two medians per metric (RMSSD, HR, LF/HF):
- **Anchor** = median over the **last 7 days** of clean samples → personalised
  "normal," used as the guardrail.
- **Warm-start seed** = median over the **most recent 1 hour** of clean samples →
  current state; becomes the initial EWMA value so the baseline starts "where you
  are now" and then adapts.

Median (not mean) so a past episode or artifact spike cannot skew either value.

## Guardrail (anchor bounds the EWMA)

The fast ~15-min EWMA continues to drive detection exactly as today. After each
EWMA update in `Update(...)`, clamp each baseline to within ±`MaxAnchorDrift` of
its anchor:

```
baselineRmssd = Clamp(baselineRmssd, anchorRmssd * (1 - MaxAnchorDrift),
                                     anchorRmssd * (1 + MaxAnchorDrift))
```

- `MaxAnchorDrift = 0.40` (tunable constant). So the baseline may not drift more
  than 40% from the personalised anchor in either direction.
- The clamp applies per metric and only when that metric's anchor is set
  (`> 0`). With no anchor (no history), behaviour is identical to today.

## Warm-up

- If the **recent-hour** window yields at least `MinWarmStartSamples` clean
  samples (proposed: 12, i.e. ~1 minute at 5s cadence — generous lower bound),
  the tracker **starts warm**: `_isWarm = true`, EWMA seeded from the warm-start
  median, `_firstSampleTime` set to the latest historical sample timestamp.
- Otherwise (stale data / long gap / no history) it falls back to the current
  live 10-minute warm-up. The anchor is still set as a guardrail if the 7-day
  window had data.

## Persistence

None added. The anchor is recomputed from `ReadHistory` at each launch and
improves as history accumulates. It is fixed for the duration of a session.

## Tunable constants (on `BaselineHrvTracker`)

| Constant | Proposed value | Meaning |
|---|---|---|
| `AnchorWindowDays` | 7 | Look-back for the anchor median |
| `WarmStartWindowMinutes` | 60 | Look-back for the warm-start seed median |
| `MinWarmStartSamples` | 12 | Min recent clean samples to start warm |
| `MaxAnchorDrift` | 0.40 | Guardrail band (fraction) around the anchor |

The `Pipeline` read window matches `AnchorWindowDays` (7 days).

## Data flow

1. `Pipeline.Start()` → `ReadHistory(path, now - 7d, now)` → `IReadOnlyList<HrvSample>`.
2. `_baseline.SeedFromHistory(history)`:
   - filter clean samples,
   - compute anchor medians (full window) and warm-start medians (last hour),
   - set EWMA values + anchor fields + warm state.
3. Live `Update(sample)` proceeds as today, with the new guardrail clamp applied
   after the EWMA step.

## Error handling / edge cases

- Empty or all-dirty history → no seed, no anchor; identical to current cold-start
  behaviour (live warm-up, no guardrail).
- `ReadHistory` throws (e.g. locked/missing DB) → caught in `Pipeline`; proceed
  cold (same as today). Seeding is best-effort and must never block startup.
- Long gap (recent hour empty but 7-day window has data) → anchor set, guardrail
  active, but live warm-up still required.
- Reset() clears anchor fields and warm-start state alongside the existing fields.

## Testing

Unit tests on `BaselineHrvTracker.SeedFromHistory` (Core, no DB needed):

- Median correctness for anchor and warm-start from a synthetic sample list.
- Episode exclusion: `Warning`/`Alerting` samples are ignored in both medians.
- Warm-start arming: warm when ≥ `MinWarmStartSamples` recent clean samples,
  cold otherwise.
- Guardrail clamp: after seeding with an anchor, repeatedly feeding very low (or
  high) values via `Update` cannot push the baseline beyond the anchor band.
- No history: tracker behaves exactly as the current cold-start implementation.
