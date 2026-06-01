# Time-relative plot axes

**Date:** 2026-06-01
**Status:** Design, approved in brainstorming, pending spec review.

## Overview

Every live trend chart currently plots its data against the **sample index** — when
`ImPlot.PlotLine` is handed a single value array it auto-generates x as `0, 1, 2, …`.
The x-axis tick labels are hidden (`NoTickLabels`) precisely because the indices are not
meaningful. The result: a stretch of sparse data (sensor dropout, batched/irregular
beats) occupies the same horizontal space as a dense stretch. The x position tells you
"how many samples ago," not "how long ago."

This change makes the time-series charts **time-relative**: each point is positioned by
its real timestamp, the axis is a fixed scrolling window anchored to *now*, and dropouts
read as visible gaps. The desktop ImGui Status window and the mobile Avalonia sparkline
are both converted. The Regulation Field (a 2-D phase-space trace, where time is encoded
by the comet-trail fade rather than an x-axis) and the Poincaré scatter (RR[i] vs
RR[i+1], not a time series) are explicitly out of scope.

## Why this is a presentation-layer change

The key realisation that keeps this contained: **the HRV-sample series already carry
accurate wall-clock timestamps.**

- BLE delivers RR intervals in **batches** that all share one notification timestamp
  (`PolarHrSource.OnValueChanged` stamps every beat in a batch with the same
  `now = DateTimeOffset.UtcNow`).
- But `ShortWindowHrvCalculator.AddBeat` emits at most one `HrvSample` per batch: the
  emit gate is `if ((beat.Timestamp - _lastEmitTime).TotalSeconds < EmitIntervalSeconds)
  return null;`. The first beat in a batch that passes the gate sets
  `_lastEmitTime = now`; every other beat in that same batch is then `0 < EmitInterval`
  and returns null. With the default `EmitIntervalSeconds = 5` and Polar notifying ~1/s,
  that's one sample every ~5 s, on real wall-clock time, with ≤~1 s of batch jitter.

So for RMSSD, baselines, HR, pNN50, SDNN, LF/HF, LF, HF, SD1, SD2, SD1/SD2, contact, and
battery, the fix is simply to **thread the existing timestamp through** alongside each
value. No reconstruction, no interpolation.

The **only** series where "multiple per query" bites is the per-beat **RR-interval**
plot (`_recentRr`, pushed in `OnBeatReceived`, so all beats in a batch stack at one
timestamp). There the derivation is exact: RR *is* the inter-beat duration, so a running
cumulative sum is the correct time axis — no guesswork.

### What we deliberately do NOT touch

Core HRV windowing, the detection state machine, the baseline tracker, and SQLite
persistence are unchanged. They compute from RR **values**, not timestamps; reconstructing
per-beat times in that path would change persisted data and detection behaviour, carries
real risk, buys nothing for the metrics (RMSSD/pNN50/LF-HF are value-based), and — per the
repo's own batching gotcha — could not be verified from tests anyway. The
`EvictOldBeats` error from batch-quantised timestamps is ≤~1 s on a 60 s window:
negligible.

## Goals

- Time-series charts position points by real time, not sample index.
- A fixed scrolling window anchored to *now* (width = the existing "History (min)" knob),
  so dropouts and sparse data show as visible gaps.
- Relative tick labels (`now`, `−1 min`, `−30 s`).
- Per-beat RR derivation lives in a pure, unit-tested Core helper.
- No regression to Core math, detection, baseline, or persistence.
- The Extended-vs-base length-mismatch (Extended buffers are shorter because they're only
  pushed when `Extended != null`) stops mattering once each series carries its own x.

## Non-goals / out of scope

- The **Regulation Field** trace (phase-space; time via trail fade/colour, no time x-axis).
- The **Poincaré scatter** and its identity line (RR[i] vs RR[i+1]).
- Any change to the beat → HRV → baseline → detection → persistence pipeline, or to
  persisted beat/sample timestamps.
- Absolute clock-time tick labels (chose relative; could be a later toggle).

## Core helpers (`MeltdownMonitor.Core/Hrv`, pure + unit-tested)

### `RrTimeAxis.CumulativeSeconds(IReadOnlyList<double> rrMs) : double[]`

Returns x positions in **seconds**, one per beat, with the newest beat at `0` and older
beats negative:

```
total      = sum(rrMs) / 1000
cumulative = running sum up to and including beat i, in seconds
x[i]       = cumulative[i] - total      // newest beat → 0, oldest → -total + rr0
```

Self-consistent on its own (the RR plot shares no axis with the sample-cadence plots).
Equals real elapsed time when no beats were dropped; underestimates slightly when beats
were dropped — the best available "from the data we have."

### `RelativeTimeAxis.Ticks(double windowSeconds) : (double[] positions, string[] labels)`

Picks a "nice" tick step from the window width and returns tick positions (all ≤ 0) with
human labels. Banding (initial):

| window           | step    | label form        |
|------------------|---------|-------------------|
| ≤ 2 min          | 30 s    | `−30 s`, `−1 min` |
| ≤ 10 min         | 1–2 min | `−2 min`          |
| ≤ 60 min         | 10 min  | `−10 min`         |
| > 60 min         | 30 min  | `−30 min`         |

Position `0` always labels `now`. Sub-minute steps label in seconds; whole-minute
positions label in minutes.

Both helpers are platform-neutral and covered by `MeltdownMonitor.Tests`.

## Desktop (`MeltdownMonitor.App/StatusWindow.cs`)

### Data model

Introduce an App-local `TimedSeries`: a `RingBuffer<float>` of values plus a parallel
`RingBuffer<double>` of **epoch-seconds** timestamps, with:

- `PushBack(DateTimeOffset timestamp, float value)`
- `Count`, `Resize(int)`, `Resample(int)` (apply to both buffers)
- `Snapshot(out double[] epochSeconds, out float[] values)`

Replace the 14 parallel `RingBuffer<float>` value buffers (`_rmssd`, `_baselineRmssd`,
`_pnn50`, `_sdnn`, `_meanHr`, `_baselineHr`, `_lfPower`, `_hfPower`, `_lfHfRatio`,
`_baselineLfHf`, `_sd1`, `_sd2`, `_sd1Sd2`, `_contact`) and `_battery` with `TimedSeries`.

- `OnSampleUpdated` / `BackfillFromRepository`: push `sample.Timestamp` with each value.
  Backfill uses the persisted `HrvSample` timestamps, so warm-start points land in the
  correct place on the scrolling window. Contact uses `sample.Timestamp`.
- `OnBatteryUpdated` / battery backfill: push `BatteryReading.Timestamp`.
- `_recentRr` **stays** a value-only `RingBuffer<double>` (per-beat RR ms); its x comes
  from `RrTimeAxis.CumulativeSeconds` at snapshot time.

### Rendering

All time-series plots switch to the two-array ImPlot overloads (`PlotLine(xs, ys, n)`,
`PlotStairs(xs, ys, n)` for the contact strip). A shared helper applied inside each
`BeginPlot`, every frame:

```
SetupTimeAxis(double windowSec):
    SetupAxisLimits(X1, -windowSec, +rightPad, ImPlotCond.Always)   // scrolling fixed window
    var (pos, labels) = RelativeTimeAxis.Ticks(windowSec)
    SetupAxisTicks(X1, pos, pos.Length, labels)                     // relative labels, X1 no longer NoTickLabels
```

`windowSec = _settings.SparklineWindowMinutes * 60`. Per-frame, xs are computed as
`epochSeconds[i] - nowEpoch` (captured once per render) so the window scrolls in real
time even when no new sample arrives. Y-axis keeps its current `AutoFit`. `rightPad` is a
small fraction of the window so the newest point doesn't hug the right edge.

The plot helpers (`DrawOverviewChart`, `PlotPair`, `PlotRow`, the contact strip, the RR
row) are updated to compute/accept xs and call `SetupTimeAxis`. Baseline and primary
series in a pair share the same xs (pushed together on the same sample).

**Unchanged:** `DrawScatterPlot` / `DrawPoincareScatter` (Poincaré + identity line).

## Mobile (`MeltdownMonitor.Mobile`)

### `ViewModels/NowViewModel.cs`

Add a timestamps list alongside `_rmssdHistory` / `_baselineHistory`, appended from
`sample.Timestamp` in `OnSampleUpdated`, trimmed in lockstep with the value lists in
`TrimHistory`. Expose it (e.g. `RmssdTimestamps`) for binding. This path is exercised by
the existing Mobile test harness.

### `Controls/Sparkline.cs`

Add styled properties `Timestamps` (`IReadOnlyList<double>?`, **absolute epoch
seconds** — same convention as desktop) and `WindowSeconds` (default `60`). `DrawSeries`
captures `now = DateTimeOffset.UtcNow` once per `Render`, then positions x by
`1 - (now - t) / window` clamped to `[0, 1]` of the control width (newest at the right
edge), instead of `i / (count - 1)`. When `Timestamps` is null — or its length doesn't
match the value series — it falls back to the current even index spacing, so the control
degrades gracefully. No tick labels (it's a glance widget on `NowView`).

## Data flow (desktop, per frame)

```
HrvSample (real Timestamp)
        │  OnSampleUpdated: TimedSeries.PushBack(Timestamp, value)
        ▼
TimedSeries (value + epoch-seconds ring buffers)
        │  DrawXxxTab (per render): Snapshot → xs = epoch - now
        ▼
SetupTimeAxis(windowSec): limits [-windowSec, +pad] Always; ticks = RelativeTimeAxis.Ticks
        │
        ▼
PlotLine(xs, ys, n)            (RR row: xs = RrTimeAxis.CumulativeSeconds(rr))
```

## Testing

- **`RrTimeAxisTests`** — newest at 0; strictly increasing; spacing equals RR/1000;
  empty/single-element edge cases.
- **`RelativeTimeAxisTests`** — tick count and step per window band; `0 → "now"`;
  sub-minute vs minute label forms.
- **`NowViewModelTests`** — `OnSampleUpdated` records a timestamp per value; value and
  timestamp lists stay the same length through `TrimHistory`.
- **Desktop visual scroll/gap behaviour** — verified only on the **live app with a real
  Polar sensor**, per the repo's batching gotcha (batched/replay sources cannot reproduce
  real-time scroll/gap rendering). Not claimed from automated tests.

## Risks / notes

- `App`, `Ble.Windows` have no automated tests (TFM + DI constraints), so the desktop
  wiring is verified by build + live run. Keeping the derivation logic in Core helpers
  maximises what *is* covered.
- `SetupAxisTicks` and the two-array `PlotLine`/`PlotStairs` overloads must be confirmed
  present in the project's `Hexa.NET.ImPlot` binding during implementation; if the
  custom-tick API differs, fall back to a tick formatter callback.
- `RingBuffer<T>.Resample` semantics on the new timestamp buffer should be confirmed
  (linear interpolation of timestamps on capacity change is acceptable).
