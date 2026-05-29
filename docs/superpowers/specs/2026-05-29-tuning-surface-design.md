# Runtime Tuning Surface — Design & Tuning Reference

Date: 2026-05-29
Status: Approved (pending spec review)

## Goal

Expose every hardcoded tuning constant as a persisted, in-app knob with a `(?)`
help marker describing what it does, how to tune it, and the impact of raising or
lowering it. Add a "Re-seed baseline now" button so seeding changes can be applied
without a restart. This document doubles as the human-readable tuning reference.

## Design principles

- **Don't change behaviour, expose it.** The EWMA math (per-sample fixed alpha) is
  unchanged. Constants become settable properties whose *defaults equal today's
  constants*, so existing tests and behaviour are preserved. The Pipeline overrides
  them from settings.
- **Window-minutes is a UI representation.** Internally the baseline still uses a
  fixed per-sample alpha. The responsiveness knob shows "window (min)"; the Settings
  UI / Pipeline convert to alpha using the emit cadence: `alpha = cadenceSeconds /
  (windowMinutes * 60)` (RMSSD/HR uses the HRV emit interval; LF/HF uses the 30 s
  extended-compute cadence). This keeps the math intact and the knob intuitive.
- **Follow the existing pattern.** Mirrors `DetectionThresholds`: a nested record on
  `AppSettings`, edited via `with` in the Settings tab, persisted via `Save()`.

## Settings model

- **Core** `BaselineTuning` record (so `BaselineHrvTracker` needs no App dependency).
- **App** `ChartTuning` and `HrvTuning` records on `AppSettings`.
- Existing `DetectionThresholds`, `HrvEmitIntervalSeconds`, `SparklineWindowMinutes`
  remain (already knobbed) — they get help markers too.

## Wiring

- `BaselineHrvTracker`: the `Alpha`, `AlphaExtended`, `WarmUpMinutes`,
  `MaxAnchorDrift`, `MinWarmStartSamples`, `WarmStartWindowMinutes` constants become
  settable properties (same defaults). `AnchorWindowDays` moves to settings (the
  Pipeline uses it for the `ReadHistory` range, not the tracker). EWMA math unchanged.
- `Pipeline.ApplyBaselineTuning()` sets the tracker properties from
  `_settings.BaselineTuning`, converting window-minutes → alpha. Called before
  `SeedFromHistory` (in `Start`) and once per sample in the loop (cheap), alongside
  the existing `_hrv.EmitIntervalSeconds = ...` line.
- `ShortWindowHrvCalculator`: `WindowSeconds`, `ExtendedWindowSeconds`,
  `ExtendedComputeIntervalSeconds` consts become settable properties (same defaults);
  Pipeline pushes them from `_settings.HrvTuning`.
- `StatusWindow` chart constants read from `_settings.ChartTuning`.
- `Pipeline.ReseedBaseline()` re-applies tuning and re-reads history to re-seed live.
- **Thread safety:** `BaselineHrvTracker` gets an internal lock guarding `Update` and
  `SeedFromHistory`, because the re-seed runs on the UI thread while `Update` runs on
  the pipeline thread.

## Settings tab layout

Sections, each knob followed by a `(?)` help marker (hover → tooltip text below):
Refresh · Detection thresholds (existing) · Baseline seeding (+ **Re-seed baseline
now** button) · Baseline responsiveness · Charts · Advanced HRV windows (with ⚠).

`HelpMarker(string text)` helper: a `TextDisabled("(?)")` that shows the text as a
tooltip on hover.

## Tunable values (defaults, ranges, help text)

### Baseline seeding *(applies on re-seed / next launch)*
| Knob | Default | Range | Help (what · how/impact) |
|---|---|---|---|
| Anchor look-back (days) | 7 | 1–30 | Days of history feeding your long-term "normal" (anchor). Higher = more stable, slower to reflect lifestyle change; lower = adapts faster but noisier. |
| Warm-start window (min) | 60 | 5–240 | Recent window whose median seeds the live baseline at startup. Higher = smoother seed; lower = reflects the last few minutes. |
| Min warm-start samples | 12 | 1–120 | Clean recent samples required to skip the cold warm-up. Higher = only warm-start with solid recent data; lower = warm-start more eagerly. |

### Baseline responsiveness *(applies live)*
| Knob | Default | Range | Help |
|---|---|---|---|
| Guardrail drift (%) | 40 | 10–100 | How far the live baseline may stray from the anchor. Lower = tightly tethered to your normal (catches slow declines, may clip real shifts); higher = freer to follow recent data (less protection against a long rough patch re-normalising it). |
| RMSSD/HR window (min) | 15 | 1–120 | Memory of the live RMSSD/HR baseline. Higher = smoother, slower baseline (deviations read larger); lower = chases recent values (deviations read smaller). |
| LF/HF window (min) | 17 | 1–120 | Memory of the live LF/HF baseline. Same trade-off as above, for the frequency-domain balance. |
| Warm-up (min) | 10 | 0–60 | Cold-start delay before the detector arms (only when not warm-started from history). Higher = more cautious; lower = arms sooner. |

### Charts *(applies live)*
| Knob | Default | Range | Help |
|---|---|---|---|
| Plot height (px) | 256 | 80–500 | Height of each chart. Taller = more vertical detail; shorter = more charts fit without scrolling. |
| Overview cell width (px) | 700 | 200–1200 | Target width of each Overview grid cell. Smaller = more columns (denser); larger = fewer, wider charts. |
| Max aspect ratio | 4.0 | 1.0–8.0 | Cap on a chart's width:height before it stops widening — prevents unreadable ribbons on wide windows. |
| Poincaré size (px) | 512 | 200–900 | Max size of the square Poincaré scatter. |

### Advanced HRV windows *(applies live — changes metric definitions)*
| Knob | Default | Range | Help |
|---|---|---|---|
| ⚠ Short window (s) | 60 | 30–120 | NN window for RMSSD/pNN50/mean-HR. 60 s is a common standard — changing it alters the metrics and comparability with references. |
| ⚠ Extended window (s) | 300 | 120–600 | Window for frequency-domain & Poincaré metrics. 300 s (5 min) is the clinical standard — changing it breaks comparability with published norms. |
| Extended recompute (s) | 30 | 5–120 | How often the extended metrics recompute. Lower = fresher, more CPU. |

Detection thresholds (existing knobs) keep their controls and gain help markers:
RMSSD warn-drop, HR rise, RMSSD alert-drop, warning hold, escalate, cooldown, LF/HF
corroboration toggle + rise.

## Error handling / edge cases

- Re-seed with no/locked DB → best-effort, same try/catch as startup seeding; baseline
  unchanged on failure.
- Window-minute → alpha conversion clamps alpha to `(0, 1]`; a 0/empty window guards
  against divide-by-zero (treated as the minimum sensible alpha).
- Settings load with missing fields → record defaults apply (current behaviour).

## Testing

- `BaselineHrvTracker` honours injected tuning: a low `MaxAnchorDrift` clamps tighter;
  a custom alpha changes convergence; `MinWarmStartSamples`/`WarmStartWindowMinutes`
  change warm-start arming. Existing tests pass unchanged (defaults preserved).
- Re-seed is thread-safe: concurrent `Update` + `SeedFromHistory` don't corrupt state.
- `AppSettings` round-trips the new records through save/load.
- Window↔alpha conversion: a known window+cadence yields the expected alpha.
