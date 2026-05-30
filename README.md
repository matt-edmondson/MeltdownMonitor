# MeltdownMonitor

A Windows desktop app that monitors **autonomic nervous system (ANS)** dysregulation in real time by streaming **RR intervals** (the time between consecutive heartbeats) from a Polar H10 or Polar Verity Sense over **Bluetooth Low Energy (BLE)**. It computes rolling **heart rate variability (HRV)** metrics, maintains a personal adaptive baseline, detects stress/dysregulation events, and surfaces calm, non-jarring alerts via tray icon colour change, optional chime, and optional Windows toast notification.

Intended for people with nervous system dysregulation conditions — **PTSD** (post-traumatic stress disorder), **C-PTSD** (complex PTSD), autism, and similar — who want passive real-time awareness without adding to sensory load.

> **New to HRV?** Skip ahead to the [Glossary](#glossary) for plain-language definitions of every acronym and concept used below.

### What this app is actually watching

The autonomic nervous system has two main branches:
- The **sympathetic** branch ("fight, flight, freeze") — speeds the heart up, narrows attention, mobilises the body for threat.
- The **parasympathetic** branch ("rest and digest") — slows the heart down, supports recovery, signalled through the **vagus nerve**.

In a healthy, relaxed state the two branches are constantly negotiating, so the gap between heartbeats varies from beat to beat — this variation is **HRV**. When the sympathetic branch takes over (stress, dysregulation, the lead-up to a meltdown or shutdown), HRV collapses: the beats become metronome-like and heart rate climbs. MeltdownMonitor watches for that signature shift against *your own* baseline and gives you a quiet heads-up before you would normally notice it consciously.

---

## Features

- **Passive monitoring** — lives in the system tray; no interaction needed during normal operation
- **Real-time HRV pipeline** — **RMSSD** (root mean square of successive differences), **pNN50** (percent of beat-to-beat gaps differing by >50 ms), mean **HR** (heart rate) on a 60-second rolling window, plus extended frequency-domain and Poincaré metrics on a 5-minute window
- **Personalised baseline** — **EWMA** (exponentially weighted moving average) adapts to the individual over ~15 minutes; frozen during active alert states so an alert can't drag your baseline along with it
- **Detection state machine** — Idle → Watching → Warning → Alerting → Cooldown, with configurable thresholds
- **Low-friction annotations** — log subjective states (Calm, Activated, Overwhelmed, Recovering) with optional notes
- **Persistent SQLite log** — beats, HRV samples, alerts, and annotations stored append-only
- **Dear ImGui status window** — live RMSSD-vs-baseline sparklines, current state, HR readout
- **Overlay mode** — turns the whole window into a borderless, translucent, always-on-top overlay; shows a compact HUD with the **Regulation Field** figure-8 and a user-selectable set of metrics, or expands to the full UI; optional click-through
- **Single instance enforced** — one tray icon per user session

---

## Supported Hardware

| Device | Sensor type | Notes |
|---|---|---|
| Polar H10 | **ECG** (electrocardiogram) chest strap | Most accurate RR source; the directly-measured electrical signal of the heart enables reliable **LF/HF** (low-frequency/high-frequency power) and Poincaré extended metrics |
| Polar Verity Sense | **PPG** (photoplethysmography) optical arm/wrist | Same **GATT** (Generic Attribute Profile) Heart Rate Service interface; provides RR intervals derived from blood-flow optical readings rather than electrical activity |

Default `Auto` mode scans for either device by BLE advertisement name.

> **Why a chest strap is preferred:** ECG measures the heart's electrical signal directly, picking out each R-wave cleanly. Optical sensors infer beats from changes in blood volume at the wrist, which is fine for average heart rate but introduces small timing errors that degrade short-term HRV metrics. Both work; the H10 is the research-grade option.

---

## HRV Pipeline

### Signal ingestion
Standard Bluetooth GATT characteristic `0x2A37` (Heart Rate Measurement, part of the Heart Rate Service `0x180D`). The Flags byte is parsed for HR width (bit 0), energy present (bit 3), and RR present (bit 4). Up to 9 RR intervals arrive per notification; raw units are 1/1024 s (a Bluetooth convention — divide by 1024 to convert to seconds).

### Artifact rejection
Raw RR streams contain occasional spurious values from missed beats, ectopic beats (premature contractions), or sensor noise. These are filtered out before any metric is computed:
- **Absolute bounds: 300–2000 ms** — corresponds to a plausible physiological range of roughly 30–200 BPM
- **25% moving-median rule** over a 5-beat sliding window — any interval more than 25% away from the local median of its neighbours is rejected as artifact

### Short-window metrics (60 s, emitted every 5 s)
- **RMSSD** — root mean square of successive differences between adjacent RR intervals; the standard short-term marker of vagal/parasympathetic tone
- **pNN50** — proportion of adjacent RR pairs differing by more than 50 ms; another vagal tone marker, less sensitive but more intuitive
- **Mean HR** — average heart rate over the window

### Extended metrics (5-min window, recomputed every 30 s when ≥120 s available)
- **Frequency-domain (spectral) analysis** — the RR series is treated as a signal and decomposed into its frequency components via a **Fast Fourier Transform (FFT)**. Specific bands carry physiological meaning:
  - **LF power** (low frequency, 0.04–0.15 Hz) — a mix of sympathetic and parasympathetic influence, often associated with baroreflex activity
  - **HF power** (high frequency, 0.15–0.40 Hz) — almost purely parasympathetic; tracks respiratory sinus arrhythmia (the natural quickening on inhale, slowing on exhale)
  - **LF/HF ratio** — a coarse proxy for sympathetic/parasympathetic balance; rises under stress
  - Pipeline: unevenly-spaced RR series → 4 Hz cubic interpolation → Hanning window (reduces spectral leakage) → radix-2 Cooley–Tukey FFT → band-power integration, reported in ms²
- **Poincaré plot** — a scatter of each RR interval against the next (RR<sub>n</sub> vs RR<sub>n+1</sub>); the cloud of points forms an ellipse whose shape captures HRV geometry:
  - **SD1** = RMSSD/√2 — short-term variability (width of the ellipse, parasympathetic)
  - **SD2** = √(2·SDNN² − SD1²) — long-term variability (length of the ellipse)
  - **SD1/SD2 ratio** — autonomic balance indicator
  - **SDNN** — standard deviation of all NN intervals ("normal-to-normal", i.e. RR intervals with artifacts excluded); the most general overall HRV measure

### EWMA baseline
An **exponentially weighted moving average** is a recursive smoothing filter: `baseline ← α·new + (1−α)·baseline`. Smaller α = longer memory. Each user gets a personal baseline rather than being compared against a population norm, because resting HRV varies enormously between individuals (genetics, age, fitness, medication, chronic conditions).

| Metric | Alpha (α) | Effective window |
|---|---|---|
| RMSSD / HR | 0.005 | ~15 min |
| LF/HF | 0.030 | ~30 samples (extended cadence) |

Baseline is considered "warm" after 10 minutes of data and is **frozen during Warning/Alerting states** so an in-progress stress event can't normalise itself into the baseline.

### Why RMSSD?
RMSSD is the gold-standard short-window **parasympathetic** HRV marker — it's dominated by beat-to-beat differences, which are driven mostly by vagal tone. A sudden drop in RMSSD (combined with rising HR) indicates **sympathetic activation** — the physiological signature of a stress / dysregulation / pre-meltdown response, often appearing seconds to minutes before the person consciously registers it. The 60-second window gives responsiveness without excess noise; the personalised EWMA baseline means the threshold for "abnormal for you" is built from your own data rather than a textbook range.

---

## Detection State Machine

```
Idle → Watching → Warning → Alerting → Cooldown → Watching
```

| Transition | Condition |
|---|---|
| Watching → Warning | RMSSD ≥ 30% below baseline AND HR ≥ 15% above baseline, sustained 30 s |
| Warning → Alerting | 60 s in Warning state, or RMSSD ≥ 50% drop |
| Alerting → Cooldown | Alert resolved |
| Cooldown → Watching | 10 minutes elapsed |

The two-step Warning → Alerting design exists to reduce false positives: a brief HRV dip (a sigh, a stretch, a startle) won't trip the alert; the signal has to persist or deepen. The cooldown then prevents alert chatter while the body is still settling.

Optional **LF/HF corroboration** is available but disabled by default — the frequency-domain calculation needs at least 2 minutes of clean data to be meaningful, so it's only useful once the baseline has fully calibrated.

---

## Architecture

```
MeltdownMonitor.sln
├── MeltdownMonitor.Core            # net8.0 — HRV math, artifact filter, state machine,
│                                   #          EWMA baseline, SQLite persistence. No platform deps.
├── MeltdownMonitor.Ble.Windows     # net8.0-windows10.0.19041.0 — BLE scanning + GATT streaming
│                                   #          via WinRT. Produces IAsyncEnumerable<Beat>.
├── MeltdownMonitor.App             # net8.0-windows10.0.19041.0 — ImGui status window, tray icon,
│                                   #          alert dispatcher, annotation dialog. Entry point.
└── MeltdownMonitor.Tests           # net8.0 — 54 MSTest unit tests covering Core only.
                                    #          Runs on Linux/macOS/Windows without BLE.
```

### Key dependencies

| Package | Role |
|---|---|
| `ktsu.ImGuiApp` | Dear ImGui status window |
| `ktsu.AppDataStorage` | JSON settings persistence |
| `ktsu.SingleAppInstance` | Mutex guard — one tray icon per user |
| `ktsu.IntervalAction` | Periodic sparkline backfill |
| `ktsu.Containers` (RingBuffer) | 5-beat sliding median window in artifact filter |
| `Microsoft.Data.Sqlite` | Append-only local database |

---

## Building

**Requirements:** Windows 10 1903+ (build 18362), Bluetooth LE hardware, .NET 8 SDK.

```
dotnet build
dotnet test                                     # 54 unit tests — no BLE or Windows required
dotnet run --project MeltdownMonitor.App        # Windows only
```

---

## Configuration

Settings are stored at `%APPDATA%\MeltdownMonitor\AppSettings.json` and written by the app on first run.

| Key | Values / default | Description |
|---|---|---|
| `DeviceType` | `Auto` \| `H10` \| `VeritySense` | BLE scan target |
| `Thresholds.RmssdWarningFraction` | `0.30` | RMSSD drop required for Warning |
| `Thresholds.HrWarningFraction` | `0.15` | HR rise required for Warning |
| `EnableChime` | `false` | Play WAV on alert |
| `ChimeWavPath` | _(empty)_ | Path to custom WAV file |
| `EnableToast` | `false` | Windows toast notification on alert |
| `PausedUntil` | _(datetime)_ | Set via tray "Pause 1 hour" menu item |
| `DatabasePath` | `%APPDATA%\MeltdownMonitor\data.db` | SQLite database location |

The tray icon right-click menu provides quick access to: log annotation, pause monitoring for 1 hour, show/hide the status window, toggle overlay mode, toggle overlay click-through, open the log folder, and quit.

### Overlay mode

Overlay mode turns the entire status window into a borderless, translucent, always-on-top overlay you can float over other apps. It locks to a selectable screen corner with a configurable offset and is resizable. By default it shows a **compact HUD**: the **Regulation Field** (a figure-8 "window of tolerance" with a needle that slides from the cool REST lobe through baseline to the warm MELTDOWN lobe as arousal rises) plus a chosen set of metrics (state, regulation index, HR, RMSSD, RMSSD/HR deltas vs baseline, LF/HF, pNN50, SDNN, LF/HF power, Poincaré SD1/SD2, baseline warm-up). A slim toolbar lets you drag the `:::` handle to nudge the corner offset, **Expand** to the full tabbed UI, adjust opacity, toggle click-through, or exit overlay mode; a grip in the bottom-right corner resizes it.

Toggle it from the tray menu (**Toggle overlay mode**) or **Settings → Overlay mode**, which also controls expanded/compact, opacity, click-through, the locked corner, offset, size, whether the Regulation Field is shown, and which metrics appear in the HUD. Because the overlay's own controls are unclickable while click-through is on, a **Toggle overlay click-through** tray item is provided. Configuration is persisted under `Overlay` in `AppSettings.json`.

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
| **Cubic interpolation (4 Hz)** | RR intervals arrive irregularly (whenever a beat occurs); FFT needs evenly-spaced samples. We resample to 4 Hz using a cubic interpolator before spectral analysis. |
| **Sliding/moving median** | A running median over the most recent N samples. Robust to outliers (unlike a mean), so it's used in the artifact filter to detect intervals that disagree with their local neighbourhood. |

### Software / protocol

| Term | Meaning |
|---|---|
| **BLE** | **Bluetooth Low Energy** — the low-power Bluetooth variant used by fitness sensors. Distinct from "Bluetooth Classic" used by audio devices. |
| **GATT** | **Generic Attribute Profile** — the BLE convention for exposing data as a hierarchy of services, characteristics, and descriptors with standardised UUIDs. |
| **HRS** | **Heart Rate Service** (`0x180D`) — the standard Bluetooth GATT service for heart-rate sensors. Defined by the Bluetooth SIG and implemented identically by all conforming devices. |
| **Characteristic `0x2A37`** | The **Heart Rate Measurement** characteristic within HRS. Sends notifications containing HR and optionally RR intervals. |
| **WinRT** | **Windows Runtime** — the modern Windows API surface used here to talk to BLE devices from .NET. |
| **Dear ImGui** | An immediate-mode GUI library, used for the status window. Lightweight, code-driven, no XAML. |
| **Tray icon** | The small icon in the Windows notification area (bottom-right of the taskbar). MeltdownMonitor's primary UI surface — it changes colour to reflect detector state. |

---

## Disclaimer

MeltdownMonitor is a self-awareness tool, not a medical device. It is not cleared by any regulatory body (FDA, MHRA, CE-MDR, etc.) and must not be used to diagnose, treat, or manage any medical condition. If you experience distressing symptoms, consult a qualified clinician.
