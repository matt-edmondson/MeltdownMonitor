# MeltdownMonitor

A real-time monitor for **autonomic nervous system (ANS)** dysregulation. It streams **RR intervals** (the time between consecutive heartbeats) from a Polar H10 / Verity Sense or a Garmin HRM-Dual / HRM-Pro chest strap over **Bluetooth Low Energy (BLE)**, computes rolling **heart rate variability (HRV)** metrics, maintains a personal adaptive baseline, detects stress/dysregulation events, and surfaces calm, non-jarring alerts.

It ships as two front-ends over a shared, platform-neutral core:

- **Windows desktop** — a tray-resident **Dear ImGui** app with a live status window, a translucent always-on-top **Regulation Field** overlay, chime, and Windows toast.
- **iOS / mobile** — a cross-platform **Avalonia** app (`MeltdownMonitor.Mobile` + the `MeltdownMonitor.iOS` head) with the full metrics chart suite (Metrics tab), HealthKit baseline warm-start, episode write-back, a Lock-Screen Live Activity, and local notifications.

Intended for people with nervous system dysregulation conditions — **PTSD** (post-traumatic stress disorder), **C-PTSD** (complex PTSD), autism, and similar — who want passive real-time awareness without adding to sensory load.

> **New to HRV?** Skip ahead to the [Glossary](#glossary) for plain-language definitions of every acronym and concept used below.

### What this app is actually watching

The autonomic nervous system has two main branches:
- The **sympathetic** branch ("fight, flight, freeze") — speeds the heart up, narrows attention, mobilises the body for threat.
- The **parasympathetic** branch ("rest and digest") — slows the heart down, supports recovery, signalled through the **vagus nerve**.

In a healthy, relaxed state the two branches are constantly negotiating, so the gap between heartbeats varies from beat to beat — this variation is **HRV**. When the sympathetic branch takes over (stress, dysregulation, the lead-up to a meltdown or shutdown), HRV collapses: the beats become metronome-like and heart rate climbs. MeltdownMonitor watches for that signature shift against *your own* baseline and aims to give you a quiet heads-up — ideally before you'd consciously notice. It can't promise to, though, and it can't catch every form of dysregulation — see the caution below.

> **⚠️ Silence is not safety.** MeltdownMonitor reads **one signal** — heart-rate variability — from your heart. It can miss dysregulation that doesn't show up in HRV, **especially shutdown, freeze, or dissociation**, where the body collapses *inward* and HRV can stay normal or even read as "calm". A quiet app — or a calm "REST" reading — is **never** a guarantee that you're fine. **Trust your own felt sense over the screen:** if you feel off, you're off, whatever the app says. (And whether its alerts actually arrive *before* your own awareness is still unproven, and varies from person to person.)

---

## Features

- **Passive monitoring** — lives in the system tray (desktop) or runs as a background-BLE iOS app; no interaction needed during normal operation
- **Real-time HRV pipeline** — **RMSSD** (root mean square of successive differences), **pNN50** (percent of beat-to-beat gaps differing by >50 ms), mean **HR** (heart rate) on a 60-second rolling window, plus extended frequency-domain and Poincaré metrics on a 5-minute window
- **Personalised, history-seeded baseline** — an **EWMA** (exponentially weighted moving average) adapts to the individual; on startup it is **warm-started** from the median of recent persisted history and pinned within a guardrail band of a long-term anchor, and it is **frozen during active alert states** and **while the sensor is off-body** so neither an alert nor a dropout can drag your baseline along with it
- **Detection state machine** — Idle → Watching → Warning → Alerting → Cooldown, with configurable thresholds, **sensor-contact gating**, and a physiological-recovery exit
- **Regulation Field** — a signature figure-8 ("window of tolerance") instrument whose marker slides from the cool REST lobe through baseline to the warm MELTDOWN lobe as arousal rises; rendered on both the desktop overlay and the mobile Now tab
- **Sensor health surfacing** — battery level (BLE Battery Service), skin/electrode contact status, and device identity (Device Information Service: manufacturer, model, serial, firmware)
- **Low-friction self check-ins** — log subjective states (**Fine, Edged, Escalating, Blown, Shutdown**) with optional notes
- **Persistent SQLite log** — beats, HRV samples (incl. extended metrics), alerts, annotations, and battery readings, append-only
- **Overlay mode (desktop)** — turns the whole window into a borderless, translucent, always-on-top overlay; shows a compact HUD with the Regulation Field and a user-selectable metric set, or expands to the full UI; optional click-through
- **iOS extras** — HealthKit warm-start and opt-in episode write-back, a Lock-Screen / Dynamic Island Live Activity, time-sensitive local notifications, and BLE state restoration for background reconnection
- **Single instance enforced (desktop)** — one tray icon per user session

---

## Supported Hardware

| Device | Sensor type | Notes |
|---|---|---|
| Polar H10 | **ECG** (electrocardiogram) chest strap | Most accurate RR source; the directly-measured electrical signal of the heart enables reliable **LF/HF** (low-frequency/high-frequency power) and Poincaré extended metrics |
| Polar Verity Sense | **PPG** (photoplethysmography) optical arm/wrist | Same **GATT** (Generic Attribute Profile) Heart Rate Service interface; provides RR intervals derived from blood-flow optical readings rather than electrical activity |
| Garmin HRM-Dual | **ECG** chest strap | Broadcasts over the standard BLE Heart Rate Service with RR intervals, same as the H10. Note it allows only a limited number of simultaneous BLE connections, so disconnect it from any watch or phone app first |
| Garmin HRM-Pro / HRM-Pro Plus | **ECG** chest strap | Same standard BLE RR path; the `GarminHrmPro` setting also matches the Pro Plus |

Default `Auto` mode scans for any device implementing the standard Heart Rate Service (`0x180D`) with RR intervals; the named settings pin the scan to a specific strap by its BLE advertisement name.

> **Why a chest strap is preferred:** ECG measures the heart's electrical signal directly, picking out each R-wave cleanly. Optical sensors infer beats from changes in blood volume at the wrist, which is fine for average heart rate but introduces small timing errors that degrade short-term HRV metrics. Both work; an ECG strap (Polar H10, Garmin HRM-Dual/Pro) is the research-grade option.
>
> **Garmin watches are not supported.** The Forerunner 935 broadcasts heart rate over ANT+ only (this app is BLE-only), and Forerunner watches that do broadcast over BLE send a smoothed BPM with no RR-interval field — Garmin computes wrist HRV internally and never exposes it. Without RR intervals there is nothing for the HRV pipeline to work on. Use a Garmin chest strap instead.

---

## HRV Pipeline

### Signal ingestion
Standard Bluetooth GATT characteristic `0x2A37` (Heart Rate Measurement, part of the Heart Rate Service `0x180D`). The Flags byte is parsed for HR width (bit 0), sensor-contact support/status (bits 1–2), energy present (bit 3), and RR present (bit 4). Multiple RR intervals can arrive per notification; raw units are 1/1024 s (a Bluetooth convention — multiply by `1000/1024` to convert to milliseconds). RR intervals arrive **batched** (several at once, sharing a notification), not one beat at a time.

### Artifact rejection
Raw RR streams contain occasional spurious values from missed beats, ectopic beats (premature contractions), or sensor noise. These are filtered out before any metric is computed:
- **Absolute bounds: 300–2000 ms** — corresponds to a plausible physiological range of roughly 30–200 BPM
- **25% moving-median rule** over a 5-beat sliding window — once at least two clean beats are buffered, any interval more than 25% away from the local median of its neighbours is rejected as artifact

### Short-window metrics (60 s window, emitted every 5 s)
- **RMSSD** — root mean square of successive differences between adjacent RR intervals; the standard short-term marker of vagal/parasympathetic tone
- **pNN50** — proportion of adjacent RR pairs differing by more than 50 ms; another vagal tone marker, less sensitive but more intuitive
- **Mean HR** — average heart rate over the window

### Extended metrics (5-min window, recomputed every 30 s)
- **Frequency-domain (spectral) analysis** — the RR series is treated as a signal and decomposed into its frequency components via a **Fast Fourier Transform (FFT)**. Computed once at least **2 minutes** of data are present in the window. Specific bands carry physiological meaning:
  - **LF power** (low frequency, 0.04–0.15 Hz) — a mix of sympathetic and parasympathetic influence, often associated with baroreflex activity
  - **HF power** (high frequency, 0.15–0.40 Hz) — almost purely parasympathetic; tracks respiratory sinus arrhythmia (the natural quickening on inhale, slowing on exhale)
  - **LF/HF ratio** — a coarse proxy for sympathetic/parasympathetic balance; rises under stress
  - Pipeline: unevenly-spaced RR series → **linear** interpolation onto a 4 Hz grid → mean removal → Hanning window (reduces spectral leakage) → zero-pad to a power of two → radix-2 Cooley–Tukey FFT → one-sided PSD integrated over each band, reported in ms²
- **Poincaré plot** — a scatter of each RR interval against the next (RR<sub>n</sub> vs RR<sub>n+1</sub>); available once ≥3 intervals are present. The cloud of points forms an ellipse whose shape captures HRV geometry:
  - **SD1** = RMSSD/√2 — short-term variability (width of the ellipse, parasympathetic)
  - **SD2** = √(2·SDNN² − SD1²) — long-term variability (length of the ellipse)
  - **SD1/SD2 ratio** — autonomic balance indicator
  - **SDNN** — standard deviation of all NN intervals ("normal-to-normal", i.e. RR intervals with artifacts excluded); the most general overall HRV measure

### Personal baseline (EWMA + history seeding + anchor guardrail)
An **exponentially weighted moving average** is a recursive smoothing filter: `baseline ← α·new + (1−α)·baseline`. Smaller α = longer memory. Each user gets a personal baseline rather than being compared against a population norm, because resting HRV varies enormously between individuals (genetics, age, fitness, medication, chronic conditions).

Responsiveness is expressed as a **memory window in minutes** and converted to a per-sample α from the active sample cadence (`α ≈ cadence ÷ window`, clamped to `[0.0001, 1.0]`):

| Metric | Memory window | Effective α at default cadence |
|---|---|---|
| RMSSD / HR | ~15 min (5 s cadence) | ≈ 0.0056 |
| LF/HF | ~17 min (30 s cadence) | ≈ 0.029 |

On startup the baseline is **warm-started** from persisted history rather than starting cold:
- a robust **long-term anchor** is taken as the median over a multi-day window (default 7 days),
- the **live EWMA is seeded** from the median of the most recent hour (when ≥12 clean recent samples exist),
- and the live EWMA is then **clamped to ±40% of the anchor** so a long sub-threshold rough patch can't silently re-normalise the baseline.

The baseline is considered "warm" after a cold-start warm-up (default 10 minutes of data; instant when a warm-start succeeds), is **frozen during Warning/Alerting states**, and is **not updated while the sensor reports no contact** — both so an in-progress stress event or an off-body dropout can't normalise itself into the baseline.

### Why RMSSD?
RMSSD is the gold-standard short-window **parasympathetic** HRV marker — it's dominated by beat-to-beat differences, which are driven mostly by vagal tone. A sudden drop in RMSSD (combined with rising HR) indicates **sympathetic activation** — the physiological signature of a stress / dysregulation / pre-meltdown response. This shift can in principle register before conscious awareness, but **whether MeltdownMonitor's alerts actually precede your felt experience, and by how much, is unproven and personal** — the app stores the data needed to measure it (alert timestamps vs. self check-in `annotations`, via `DetectionEfficacyAnalyzer`), but that validation has not yet been run. The 60-second window gives responsiveness without excess noise; the personalised EWMA baseline means the threshold for "abnormal for you" is built from your own data rather than a textbook range.

---

## Detection State Machine

```
Idle → Watching → Warning → Alerting → Cooldown → Watching
```

The detector stays **Idle** until the baseline tracker reports it is warm, then moves to **Watching**.

| Transition | Condition |
|---|---|
| Watching → Warning | RMSSD ≥ 30% below baseline **and** HR ≥ 15% above baseline, sustained 30 s |
| Watching/Warning → Alerting (immediate) | RMSSD ≥ 50% below baseline — fires an alert from either state at once |
| Warning → Alerting | Warning conditions held for the 60 s escalation window |
| Warning → Watching | Warning conditions cleared |
| Alerting → Cooldown | **Physiological recovery**: RMSSD back to within 10% of baseline **and** HR within 5% above baseline, held continuously for 60 s |
| Cooldown → Watching | 10 minutes elapsed |

The two-step Warning → Alerting design exists to reduce false positives: a brief HRV dip (a sigh, a stretch, a startle) won't trip the alert; the signal has to persist or deepen. Exiting an alert requires a genuine vagal rebound — not just a single sample drifting back toward baseline — so the alert doesn't flicker off while the body is still settling. The cooldown then prevents alert chatter during recovery.

**Sensor-contact gating.** When the sensor reports it is off-body (`NotDetected`), the current sample is treated as untrustworthy: the detector holds its state and resets any in-progress Warning or recovery streak, so a dropped strap can neither raise an alert nor be mistaken for recovery. Sensors that don't report contact at all are never gated.

**LF/HF corroboration is on by default.** Once a personal LF/HF baseline exists and ≥2 minutes of clean extended metrics are available, Warning entry additionally requires LF/HF to be elevated (≥50% above its baseline) — the more specific signal. During warm-up (before the LF/HF baseline exists) the detector falls back to the RMSSD+HR condition alone, so early warnings are never suppressed. An immediate ≥50% RMSSD drop always alerts regardless of this gate. This can be disabled in settings.

---

## Regulation Field

The Regulation Field is the app's signature glanceable instrument: a lemniscate (figure-8) "window of tolerance" with a needle that slides from the cool **REST** lobe, through baseline at the centre, to the warm **MELTDOWN** lobe as arousal rises.

`RegulationFieldCalculator` (in Core, pure and unit-tested) turns each HRV sample into a `RegulationReading`:
- an **arousal index** in `[-1, 1]`, combining the RMSSD drop and HR rise each normalised by their Warning thresholds (RMSSD weighted 0.6, HR 0.4); a combined deviation exactly at the Warning thresholds maps to the recovery-target boundary (0.6),
- a **variability quality** (current RMSSD ÷ baseline),
- a **confidence** that ramps from 0 to 1 during baseline warm-up,
- a **lobe roundness** derived from the Poincaré SD1/SD2 ratio, and
- a signed **LF/HF balance** relative to the LF/HF baseline.

The renderers are platform-specific — `MeltdownMonitor.App/Regulation/RegulationFieldView.cs` (ImGui draw-list) and `MeltdownMonitor.Mobile/Controls/RegulationField.cs` (Avalonia + a SkiaSharp additive-blend draw op for the glow layers) — while the geometry (`LemniscateGeometry`), the calculation, and the RR-texture playhead live in Core. Both renderers now draw the full layer set (LF/HF halo, dwell heatmap + peak crosshair + region box, vagal axis with marker/trail tone travel, recovery target, RR-textured trace).

---

## Architecture

```
MeltdownMonitor.sln
├── MeltdownMonitor.Core            # net10.0 — HRV math, artifact filter, detection state
│                                   #           machine, EWMA baseline + seeding, regulation-field
│                                   #           calculator, beat-source abstractions, SQLite
│                                   #           persistence. No platform dependencies.
├── MeltdownMonitor.Ble.Windows     # net10.0-windows10.0.19041.0 — BLE scan + GATT streaming via
│                                   #           WinRT; implements IBeatSource/IBatterySource/
│                                   #           IContactSource/IDeviceInfoSource.
├── MeltdownMonitor.Ble.Apple       # net10.0-ios — CoreBluetooth equivalent, with BLE state
│                                   #           restoration for background reconnection.
├── MeltdownMonitor.App             # net10.0-windows10.0.19041.0 — Dear ImGui status window, tray
│                                   #           icon, overlay chrome, alert dispatcher. Windows entry point.
├── MeltdownMonitor.Mobile          # net10.0 — Avalonia UI, view models, and platform-neutral
│                                   #           service interfaces (notifications, chime, health,
│                                   #           live activity). Shared mobile base.
├── MeltdownMonitor.iOS             # net10.0-ios — iOS head: composition root + native service
│                                   #           implementations (HealthKit, UserNotifications,
│                                   #           AVFoundation, ActivityKit). iOS entry point.
└── MeltdownMonitor.Tests           # net10.0 — MSTest suite covering Core + Mobile.
                                    #           Runs on Linux/macOS/Windows without BLE.
```

A Swift `MeltdownMonitor.iOS.WidgetExtension` (Live Activity UI) lives in the tree but is built in Xcode and is not part of the .NET solution.

The desktop and mobile heads each own a `Pipeline` that wires the same Core flow:

```
IBeatSource → artifact filter → ShortWindowHrvCalculator → BaselineHrvTracker (gated)
            → DysregulationDetector (gated) → RegulationFieldCalculator → UI + MeltdownRepository
```

### Key dependencies

| Package | Used by | Role |
|---|---|---|
| `ktsu.ImGui.App` / `ktsu.ImGui.Widgets` | App | Dear ImGui status window |
| `ktsu.ThemeProvider(.ImGui)` | App | Catppuccin Macchiato theming |
| `ktsu.AppDataStorage` | App | JSON settings persistence |
| `ktsu.SingleAppInstance` | App | Mutex guard — one tray icon per user |
| `ktsu.IntervalAction` | App | Periodic sparkline backfill |
| `ktsu.Containers` (RingBuffer) | Core | 5-beat sliding median window in the artifact filter |
| `CommunityToolkit.WinUI.Notifications` | App | Windows toast notifications |
| `Avalonia` / `Avalonia.Themes.Fluent` / `Avalonia.iOS` | Mobile / iOS | Cross-platform UI |
| `Microsoft.Data.Sqlite` | Core | Append-only local database |

---

## Persistence

A single SQLite database (one file) stores everything append-only. The schema is created on open with `CREATE TABLE IF NOT EXISTS`, and older databases are migrated forward by `ALTER TABLE ADD COLUMN`.

| Table | Columns |
|---|---|
| `beats` | `ts` (Unix ms PK), `rr_ms`, `hr_bpm`, `artifact` |
| `hrv_samples` | `ts` (PK), `rmssd`, `pnn50`, `mean_hr`, `baseline_rmssd`, `baseline_hr`, `state`, and migrated extended columns `lf_power_ms2`, `hf_power_ms2`, `lf_hf_ratio`, `sd1`, `sd2`, `sd1_sd2_ratio`, `sdnn` |
| `alerts` | `ts` (PK), `trigger_reason`, `rmssd_at_trigger`, `baseline_at_trigger` |
| `annotations` | `ts` (PK), `label`, `notes` |
| `battery` | `ts` (PK), `percent` |

Writes on the live connection are serialised behind a lock (the pipeline writes beats/samples on a background thread while battery readings arrive on a BLE thread). User-initiated annotation writes and all history reads use short-lived independent connections (read-only, or with a `busy_timeout`) to avoid concurrent use of the live connection. On iOS the repository opens with a sandbox profile (`journal_mode=TRUNCATE`, `fullfsync=ON`) so background BLE writes still commit under data-protection encryption when the device is locked.

---

## Building

**Requirements:** **.NET 10 SDK**.

- **Core / Mobile / Tests** build and test on Linux, macOS, or Windows.
- **Windows desktop (`MeltdownMonitor.App`)** requires Windows 10 1903+ (build 18362) and Bluetooth LE hardware.
- **iOS (`MeltdownMonitor.iOS`, `MeltdownMonitor.Ble.Apple`)** requires macOS with Xcode and the `ios` workload; deployment target is iOS 17.0+.

```
dotnet build MeltdownMonitor.Core/MeltdownMonitor.Core.csproj
dotnet test  MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj   # no BLE required
dotnet run   --project MeltdownMonitor.App                        # Windows only
```

> **Note:** the test project references **Core and Mobile**; the Windows `App`, both BLE backends, and the iOS head are not currently covered by automated tests.

CI runs two GitHub Actions workflows: `dotnet.yml` (Windows build/test via the KtsuBuild pipeline, SonarQube, coverage, NuGet release) and `ios.yml` (macOS build/test, simulator artifact, and a TestFlight upload gated on `ios-v*` tags with signing secrets).

---

## Configuration

### Desktop (`AppSettings`)
Settings are persisted as JSON via `ktsu.AppDataStorage` (which manages the on-disk location) and written by the app on first run.

| Key | Default | Description |
|---|---|---|
| `DeviceType` | `Auto` | BLE scan target — `Auto` \| `H10` \| `VeritySense` \| `GarminHrmDual` \| `GarminHrmPro` |
| `Thresholds.RmssdWarningDropFraction` | `0.30` | RMSSD drop required for Warning |
| `Thresholds.HrWarningRiseFraction` | `0.15` | HR rise required for Warning |
| `Thresholds.UseLfHfCorroboration` | `true` | Require an LF/HF rise to corroborate a Warning (once warm) |
| `EnableChime` | `true` | Play a sound on alert |
| `ChimeWavPath` | _(empty)_ | Path to a custom WAV file (falls back to a system beep) |
| `EnableToast` | `true` | Windows toast notification on alert |
| `AlertSuggestion` | _"Step away. Five minutes. Find something quiet."_ | Calm suggestion shown in the toast |
| `PausedUntil` | _(unset)_ | When set, monitoring is paused until this time (tray "Pause 1 hour") |
| `DatabasePath` | `%LOCALAPPDATA%\MeltdownMonitor\meltdown.db` | SQLite database location |
| `HrvEmitIntervalSeconds` | `5.0` | Minimum gap between HRV sample emissions (0.5–30 s) |
| `SparklineWindowMinutes` | `60` | How much history the status-window sparklines display (1–360 min) |
| `BaselineTuning` | _(record)_ | Anchor window, warm-start window/sample count, drift guardrail, memory windows, warm-up |
| `HrvTuning` | _(record)_ | Short/extended window lengths and extended recompute interval |
| `ChartTuning` | _(record)_ | Status-window chart layout |
| `Overlay` | _(record)_ | Overlay mode configuration (see below) |

The tray icon right-click menu provides quick access to: log a self check-in, pause monitoring for 1 hour, show/hide the status window, toggle overlay mode, toggle overlay click-through, open the log folder, and quit.

#### Overlay mode (desktop)
Overlay mode turns the entire status window into a borderless, translucent, always-on-top overlay you can float over other apps. It locks to a selectable screen corner with a configurable offset and is resizable. By default it shows a **compact HUD**: the Regulation Field plus a chosen set of metrics (state, HR, RMSSD, RMSSD-vs-baseline, LF/HF, and more). A slim toolbar lets you nudge the corner offset, **Expand** to the full tabbed UI, adjust opacity, toggle click-through, or exit. Because the overlay's own controls are unclickable while click-through is on, a **Toggle overlay click-through** tray item is provided. Configuration is persisted under `Overlay` in the settings file.

### Mobile (`MobileSettings`)
The mobile app stores settings through a platform settings store (NSUserDefaults on iOS, serialised JSON), not the desktop file path. In addition to the shared thresholds and `DeviceType`/`EnableChime`/`AlertSuggestion`, it adds: `EnableNotifications` (default `true`), `WriteEpisodesToHealthKit` (opt-in, default `false`), `EnableLiveActivity` (opt-in, default `false`), `PeripheralIdentifier` (saved after the first BLE connection for fast reconnection), and `IsDisclaimerAccepted` (a first-run gate that blocks the app until accepted).

---

## iOS / mobile specifics

- **HealthKit warm-start** — before live BLE flows, the pipeline reads recent heart-rate samples from HealthKit and seeds the baseline, breaking the cold-start calibration wait.
- **Episode write-back** — when opted in, dysregulation episodes are written back to HealthKit (backdated slightly so the record lines up with the felt event).
- **Live Activity** — a Lock-Screen / Dynamic Island surface reflecting current state, throttled to ~1 Hz with state changes bypassing the throttle. The ActivityKit bridge is resolved lazily, so the .NET app links and runs cleanly even when the Swift widget extension isn't present.
- **Local notifications** — time-sensitive alerts plus optional status notifications, with notification categories registered up front.
- **BLE state restoration** — the CoreBluetooth central uses a restore identifier and a persisted peripheral GUID to reattach in the background after the OS relaunches the app.
- **Background modes** — the app declares `bluetooth-central`, `audio`, and `processing` background modes so monitoring continues while backgrounded.

---

## Project documentation

| Document | Contents |
|---|---|
| [`docs/ios-design.md`](docs/ios-design.md) | Full iOS port specification — Avalonia, HealthKit, Live Activity, App Store submission phases |
| [`docs/live-activity.md`](docs/live-activity.md) | Lock-Screen / Dynamic Island Live Activity spec — managed/native boundary, `dlsym` lazy binding, throttling |
| [`docs/store-submission/`](docs/store-submission/) | App Store collateral — disclaimer, privacy nutrition label, screenshot plan |
| [`assets/branding/README.md`](assets/branding/README.md) | SVG masters, palette, and ImageMagick raster regeneration commands |
| `docs/superpowers/` | Point-in-time design specs and plans (historical record; not living docs) |

---

## Glossary

### Medical / physiological

| Term | Meaning |
|---|---|
| **ANS** | **Autonomic nervous system** — the involuntary control system for heart rate, breathing, digestion, etc. Has sympathetic and parasympathetic branches. |
| **Sympathetic nervous system** | The "fight, flight, freeze" branch of the ANS. Activation raises HR, drops HRV, narrows attention, and mobilises the body for perceived threat. |
| **Parasympathetic nervous system** | The "rest and digest" branch. Lowers HR, raises HRV, supports recovery and digestion. Signalled primarily via the vagus nerve. |
| **Vagal tone** | A measure of parasympathetic activity via the vagus nerve. Higher vagal tone correlates with better emotional regulation, recovery from stress, and HRV. |
| **Dysregulation** | A loss of the normal sympathetic/parasympathetic balance — typically a stuck sympathetic state. Manifests as anxiety, panic, meltdown, shutdown, dissociation, or rage depending on the person. |
| **Meltdown / shutdown** | Common terms in autistic and trauma communities for the overt expression of an overwhelmed nervous system — meltdown is the high-arousal form, shutdown the low-arousal form. Both have measurable HRV signatures. |
| **Window of tolerance** | The arousal range within which a person can function and self-regulate. The Regulation Field visualises movement toward the edges of this window. |
| **PTSD / C-PTSD** | **Post-traumatic stress disorder** and **complex PTSD**. Both involve chronic ANS dysregulation; C-PTSD typically arises from prolonged/repeated trauma rather than a single event. |
| **ECG (EKG)** | **Electrocardiogram** — direct measurement of the heart's electrical activity. Each beat shows a characteristic P-QRS-T waveform; the sharp R-wave is what RR intervals are measured between. Gold standard for HRV. |
| **PPG** | **Photoplethysmography** — optical measurement of blood-volume changes (e.g. wrist-worn sensors). Cheaper and more comfortable than ECG, but less precise for HRV. |
| **R-wave / RR interval** | The R-wave is the tallest, sharpest peak in each ECG heartbeat. An **RR interval** is the time (in milliseconds) between two consecutive R-waves — the fundamental input to all HRV math. |
| **NN interval** | "Normal-to-normal" interval — an RR interval that has passed artifact filtering (ectopic beats, missed beats, and noise removed). Most HRV metrics are computed on NN, not raw RR. |
| **Ectopic beat** | A premature or out-of-place beat originating outside the heart's normal pacemaker. Common and usually benign, but distorts HRV math, so the artifact filter removes them. |
| **Respiratory sinus arrhythmia (RSA)** | The natural speeding of the heart on inhalation and slowing on exhalation. The dominant driver of HF-band HRV power and a marker of vagal tone. |

### HRV metrics

| Term | Meaning |
|---|---|
| **HR** | **Heart rate**, in beats per minute (BPM). |
| **HRV** | **Heart rate variability** — the variation in time between heartbeats. Higher HRV generally indicates a healthier, more adaptive nervous system; sudden drops signal stress. |
| **RMSSD** | **Root mean square of successive differences** — the standard short-window vagal-tone metric. Compute beat-to-beat differences, square them, average, square-root. Higher = more parasympathetic activity. |
| **pNN50** | **Proportion of NN intervals differing by more than 50 ms** — fraction of adjacent beat pairs with a >50 ms gap. Another vagal-tone marker. |
| **SDNN** | **Standard deviation of NN intervals** — overall HRV "amount" across the window. Captures both sympathetic and parasympathetic contributions. |
| **LF power** | **Low-frequency power** (0.04–0.15 Hz band of the RR spectrum, ms²). Mixed sympathetic/parasympathetic origin; tied to baroreflex regulation. |
| **HF power** | **High-frequency power** (0.15–0.40 Hz, ms²). Almost purely parasympathetic; closely tracks respiratory sinus arrhythmia. |
| **LF/HF ratio** | Ratio of LF to HF power. Often interpreted as a coarse sympathetic/parasympathetic balance indicator (rises under stress). The interpretation is debated in the literature but useful as a corroborating signal. |
| **Poincaré plot** | A scatter plot of each RR interval (x) vs the next RR interval (y). A relaxed nervous system produces a fat cloud; a stressed one produces a thin, elongated one. |
| **SD1 / SD2** | The two principal axes of the Poincaré ellipse. SD1 = short-term beat-to-beat variability (≈ RMSSD/√2, parasympathetic); SD2 = long-term variability. SD1/SD2 gives the ellipse aspect ratio. |

### Signal processing & math

| Term | Meaning |
|---|---|
| **EWMA** | **Exponentially weighted moving average** — a recursive smoothing filter: `new_avg = α·sample + (1−α)·old_avg`. Smaller α = longer effective memory. Used here to build the personal baseline. |
| **FFT** | **Fast Fourier Transform** — efficient algorithm (here, Cooley–Tukey radix-2) for decomposing a signal into its frequency components. Used to compute LF and HF power. |
| **Hanning window** | A bell-shaped weighting applied to a signal before FFT to reduce "spectral leakage" — i.e. to stop the artificial edges of a finite window from smearing energy across frequency bins. |
| **Linear interpolation (4 Hz)** | RR intervals arrive irregularly (whenever a beat occurs); FFT needs evenly-spaced samples. We resample to 4 Hz using two-point linear interpolation before spectral analysis. |
| **Sliding/moving median** | A running median over the most recent N samples. Robust to outliers (unlike a mean), so it's used in the artifact filter to detect intervals that disagree with their local neighbourhood. |
| **Anchor / drift guardrail** | A long-term median of HRV history that the live EWMA baseline is clamped near (±40%), so a prolonged sub-threshold rough patch can't silently re-normalise "normal for you". |

### Software / protocol

| Term | Meaning |
|---|---|
| **BLE** | **Bluetooth Low Energy** — the low-power Bluetooth variant used by fitness sensors. Distinct from "Bluetooth Classic" used by audio devices. |
| **GATT** | **Generic Attribute Profile** — the BLE convention for exposing data as a hierarchy of services, characteristics, and descriptors with standardised UUIDs. |
| **HRS** | **Heart Rate Service** (`0x180D`) — the standard Bluetooth GATT service for heart-rate sensors. Defined by the Bluetooth SIG and implemented identically by all conforming devices. |
| **Characteristic `0x2A37`** | The **Heart Rate Measurement** characteristic within HRS. Sends notifications containing HR, sensor-contact status, and optionally RR intervals. |
| **Device Information Service (`0x180A`)** | Standard GATT service exposing manufacturer, model, serial, and firmware/hardware revisions — surfaced as the connected sensor's identity. |
| **Battery Service (`0x180F`)** | Standard GATT service exposing the sensor's battery level (0–100%). |
| **WinRT** | **Windows Runtime** — the modern Windows API surface used here to talk to BLE devices from .NET. |
| **CoreBluetooth** | Apple's BLE framework, used by the iOS BLE backend (including background state restoration). |
| **Dear ImGui** | An immediate-mode GUI library, used for the desktop status window. Lightweight, code-driven, no XAML. |
| **Avalonia** | A cross-platform .NET UI framework, used for the mobile/iOS app. |
| **Live Activity** | An iOS Lock-Screen / Dynamic Island surface (ActivityKit) that shows live, glanceable state. |
| **Tray icon** | The small icon in the Windows notification area. MeltdownMonitor's primary desktop UI surface — it changes colour to reflect detector state. |

---

## Disclaimer

MeltdownMonitor is a self-awareness tool, not a medical device. It is not cleared by any regulatory body (FDA, MHRA, CE-MDR, etc.) and must not be used to diagnose, treat, or manage any medical condition. If you experience distressing symptoms, consult a qualified clinician.
