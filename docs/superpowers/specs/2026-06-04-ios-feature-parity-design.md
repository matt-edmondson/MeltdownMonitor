# iOS (Avalonia Mobile) feature parity with the desktop head — design

**Date:** 2026-06-04
**Status:** Approved for planning

## Context

The "iOS app" UI is the Avalonia **`MeltdownMonitor.Mobile`** project; the
`MeltdownMonitor.iOS` head is only a composition root + native services. So
"make the iOS app render all the same features the desktop app has" means
bringing the Mobile/Avalonia UI to parity with the desktop **`MeltdownMonitor.App`**
(Dear ImGui) head.

The desktop head renders, across 8 tabs + an overlay:
Regulation Field, Overview (13-chart grid), Heart Rate, Time-Domain HRV,
Frequency-Domain, Poincaré, Annotations, Settings, plus overlay/tray.

The Mobile head currently renders: a Now page (Regulation Field + a single
RMSSD sparkline + readouts + check-in sheet + connect), a History list, a
Settings page (a subset of desktop knobs), and a first-run Disclaimer.

The Mobile `Pipeline` already receives the full `HrvSample` (including
`.Extended`: SDNN, LF/HF, LF power, HF power, SD1, SD2, SD1/SD2), battery, and
contact — so the **data exists**; the gap is almost entirely UI. The one
exception: the Mobile `Pipeline` does **not** expose a per-beat `BeatReceived`
event (the App pipeline does, `App/Pipeline.cs:296`), which the real
RR-textured trace and the RR/Poincaré charts need.

## Goal & definition of done

Bring the Mobile UI to feature parity with the desktop head, in a
phone-optimized layout.

**Done = code-complete, building clean (Core/Mobile/Tests + the iOS-specific
csproj edits), with Core/Mobile logic tests passing.** Visual/timing fidelity
(glow bloom, RR playhead scroll, chart/layout density) is verified and tuned in
a later macOS/device session, because **no Mobile head runs on Windows** — the
only head for the Mobile UI is `net10.0-ios` (macOS + Xcode), and per CLAUDE.md
the field's glow/animation/timing are verifiable only on the live app + a real
Polar sensor.

### Out of scope (desktop-only, no mobile analogue)

Overlay mode, tray icon, resize grips, the always-on status header strip (the
Now page already carries state/HR/RMSSD/battery/contact).

## Decisions (locked with the user)

1. **Scope:** full metric set, phone-optimized layout (every metric the desktop
   shows, grouped into scrolling sections rather than a dense grid).
2. **Charts home:** one scrolling **"Metrics"** tab in the bottom-nav shell.
3. **Charting tech:** **hand-rolled** chart controls on `DrawingContext`
   (no stable charting library supports Avalonia 12; only LiveCharts2's
   `2.1.0-dev-*` prerelease does, and a dev prerelease on a health app + the
   blast radius of an Avalonia downgrade were both rejected).
4. **Field glow:** **true additive** via a SkiaSharp custom draw operation
   (`SKBlendMode.Plus`), matching the desktop's bracketed additive regions.
5. **Done:** code-complete + tests now; visual tuning deferred to device.

## Architecture — three workstreams

The work splits into three workstreams sharing the same data spine. The
implementation plan sequences them; workstream B is the riskiest and
least-verifiable from this environment.

### Workstream A — Metrics tab (hand-rolled charts)

New controls in `Mobile/Controls`, all rendered via `DrawingContext`, extending
the existing `Sparkline` pattern. **Coordinate mapping** (data→pixel, time-window
clipping, padded auto-fit) is extracted into **pure static methods** so it is
unit-testable without a render surface:

- `MetricChart` — multi-series, time-windowed line chart with an optional
  **baseline overlay** series, padded auto-fit Y axis, title, and light
  axis/tick chrome. Supports a stairs mode for the contact strip.
- `ScatterChart` — Poincaré plot: equal axes, faint identity line
  (RR[i] = RR[i+1]), and the consecutive-pair point cloud.

New `MetricsViewModel` holds ring-buffer histories for every metric, mirroring
the desktop `StatusWindow`'s `TimedSeries` set:
RMSSD (+ baseline), pNN50, SDNN, mean HR (+ baseline), LF/HF ratio (+ baseline),
LF power, HF power, SD1, SD2, SD1/SD2, raw RR, battery, sensor contact. It is
fed from `Pipeline.SampleUpdated`, the new `Pipeline.BeatReceived` (RR), and
`Pipeline.BatteryUpdated`, and **backfills from `MeltdownRepository.ReadHistory`
/ `ReadBatteryHistory`** on load, exactly as the desktop `BackfillFromRepository`
does. History window length is driven by a settings value
(`SparklineWindowMinutes`).

New `MetricsView.axaml` — a single `ScrollViewer` with grouped sections:
- **Time-Domain:** RMSSD vs baseline, pNN50, SDNN
- **Heart Rate:** HR vs baseline, RR intervals
- **Frequency:** LF/HF ratio vs baseline, LF power, HF power
- **Poincaré:** scatter, SD1, SD2, SD1/SD2
- **Sensor:** battery, sensor-contact strip

A "Metrics" tab is added to `RootView` (between Now and History) and a
`Metrics` property to `RootViewModel`.

### Workstream B — Regulation Field rendering parity

Refactor `Mobile/Controls/RegulationField` so its **glow layers render through a
SkiaSharp `ICustomDrawOperation`** obtained via `ISkiaSharpApiLeaseFeature`,
enabling `SKBlendMode.Plus` on exactly the regions the desktop brackets as
additive. Crisp chrome (labels, marker core, axis baselines) may stay in
`DrawingContext` interleaved with the custom op, or be drawn in Skia — chosen at
implementation time for clean z-ordering.

Layers to add (porting desktop `App/Regulation/RegulationFieldView.cs` logic
against the same Core helpers — `LemniscateGeometry`,
`RegulationFieldCalculator`, `RegulationFieldHistogram`, `HypoarousalVisual`):

- **LF/HF balance halo** — soft additive concentric discs biased toward the
  dominant autonomic pole; gated on `UseLfHfCorroboration`.
- **Dwell density heatmap** + **peak crosshair** + **high-density region box** —
  reuses Core `RegulationFieldHistogram.FieldDensity` →
  `RegulationFieldDensity` (`PeakCount`, `Count`, `PeakX/Y`,
  `HighDensityBounds(threshold)`), with the magma Catppuccin ramp.
- **Real RR-signal-textured ribbon trace** with a free-running playhead — port
  the desktop `RrTexturePlayhead` + `BuildRrDeviations`, replacing today's
  synthetic `RegulationFieldAnimator` jitter. Requires the new `BeatReceived`
  event feeding a recent-RR buffer in the control.
- **Vagal-tone Y travel** — the marker, the comet trail, and the Y-axis
  histogram must offset vertically by `VagalTone` (desktop `VagalToneOffsetY`).
  Today's Mobile marker/trail position purely on the index curve and ignore
  vagal tone on Y — a real fidelity bug to fix.
- Comet trail as a smooth Catmull-Rom spline (vs today's discrete dots);
  additive marker halos; a `drawScale` factor so chrome stays legible as the
  field grows.

`Mobile.csproj` gains managed **`SkiaSharp`** + **`Avalonia.Skia`** package
references (the iOS head already ships `SkiaSharp.NativeAssets.iOS` 3.119.4 /
matching HarfBuzz, so these are managed-only deps here; versions track
Avalonia.Skia 12.0.4's managed SkiaSharp).

### Workstream C — Settings & pipeline parity

`MobileSettings` gains the knobs the desktop exposes but Mobile lacks:
`LobeOpacity`, `TrailOpacity`, `HeatmapOpacity`, `HeatmapPeakOpacity`,
`HeatmapRegionOpacity`, `HeatmapRegionThreshold`, `HistogramOpacity`,
`RegulationHeatmapLength`; the detection knobs `RmssdAlertingDropFraction`,
`WarningHoldDuration`, `AlertingEscalationDuration`, `CooldownDuration`; and
refresh tuning `HrvEmitIntervalSeconds` + `SparklineWindowMinutes` (the Metrics
history window). These are surfaced as `SettingsView` sliders via
`SettingsViewModel`, wired into the field control and `MetricsViewModel`, and
round-tripped by `MobileSettingsSerializer`.

`Pipeline` gains `public event Action<Beat>? BeatReceived;`, raised for each
non-artifact beat in the beat loop (mirror `App/Pipeline.cs:296`).

## Data flow (unchanged spine)

```
IBeatSource → RrArtifactFilter → ShortWindowHrvCalculator
  → BaselineHrvTracker (gated) → DysregulationDetector (gated)
  → RegulationFieldCalculator → Pipeline events → UI + MeltdownRepository
```

New consumers of existing/added events:
- `MetricsViewModel` ← `SampleUpdated` + `BeatReceived` + `BatteryUpdated` + repo backfill.
- upgraded `RegulationField` ← `BeatReceived` (recent-RR buffer for the trace).

No Core math changes — only a new pipeline event and reuse of existing Core
types and helpers.

## Testing (what is verifiable from this environment)

- **Pure chart coordinate-mapping** helpers (data→pixel, window clip, padded
  auto-fit, scatter identity/extent) — unit tests in `MeltdownMonitor.Tests`.
- **`MetricsViewModel`** ingestion, windowing, and repository backfill — unit
  tests (Tests already references Mobile).
- **`Pipeline.BeatReceived`** raised once per non-artifact beat — pipeline test.
- **`MobileSettings`** new fields round-trip through `MobileSettingsSerializer` —
  serializer test.
- **Not** unit-testable (deferred to device): Skia additive rendering, RR
  playhead motion, chart/field visual layout and density.

## Risks

- Avalonia 12 `ISkiaSharpApiLeaseFeature` / `ICustomDrawOperation` API surface —
  verify the exact API against Avalonia 12.0.4 at implementation time.
- iOS trim/AOT with new managed SkiaSharp usage — the iOS head already sets
  `ILLinkTreatWarningsAsErrors=false`; watch for new trim warnings.
- Glow bloom and animation timing can only be confirmed on a real device +
  Polar sensor.

## Build matrix reminder

- Core / Mobile / Tests build & test on Windows — the default loop here.
- The iOS head (`net10.0-ios`) builds only on macOS + Xcode; csproj edits
  (SkiaSharp deps) are made here but compiled on CI/macOS.
