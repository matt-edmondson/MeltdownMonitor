# MeltdownMonitor

A Windows desktop app that monitors autonomic nervous system (ANS) dysregulation in real time by streaming RR intervals from a Polar H10 or Polar Verity Sense over Bluetooth LE. It computes rolling HRV metrics, maintains a personal adaptive baseline, detects stress/dysregulation events, and surfaces calm, non-jarring alerts via tray icon colour change, optional chime, and optional Windows toast notification.

Intended for people with nervous system dysregulation conditions — PTSD, C-PTSD, autism, and similar — who want passive real-time awareness without adding to sensory load.

---

## Features

- **Passive monitoring** — lives in the system tray; no interaction needed during normal operation
- **Real-time HRV pipeline** — RMSSD, pNN50, mean HR (60 s rolling), plus extended frequency-domain and Poincaré metrics (5-min window)
- **Personalised baseline** — EWMA adapts to the individual over ~15 minutes; frozen during active alert states
- **Detection state machine** — Idle → Watching → Warning → Alerting → Cooldown, with configurable thresholds
- **Low-friction annotations** — log subjective states (Calm, Activated, Overwhelmed, Recovering) with optional notes
- **Persistent SQLite log** — beats, HRV samples, alerts, and annotations stored append-only
- **Dear ImGui status window** — live RMSSD-vs-baseline sparklines, current state, HR readout
- **Single instance enforced** — one tray icon per user session

---

## Supported Hardware

| Device | Sensor type | Notes |
|---|---|---|
| Polar H10 | ECG chest strap | Most accurate RR source; enables LF/HF and Poincaré extended metrics |
| Polar Verity Sense | Optical arm/wrist | Same GATT HRS interface; RR intervals without ECG |

Default `Auto` mode scans for either device by BLE advertisement name.

---

## HRV Pipeline

### Signal ingestion
GATT characteristic `0x2A37` (Heart Rate Measurement). The Flags byte is parsed for HR width (bit 0), energy present (bit 3), and RR present (bit 4). Up to 9 RR intervals arrive per notification; raw units are 1/1024 s.

### Artifact rejection
- Absolute bounds: 300–2000 ms
- 25% moving-median rule over a 5-beat sliding window

### Short-window metrics (60 s, emitted every 5 s)
- RMSSD, pNN50, mean HR

### Extended metrics (5-min window, recomputed every 30 s when ≥120 s available)
- **Frequency domain** — LF power (0.04–0.15 Hz), HF power (0.15–0.40 Hz), LF/HF ratio in ms² via 4 Hz interpolation → Hanning window → radix-2 Cooley-Tukey FFT
- **Poincaré plot** — SD1 = RMSSD/√2, SD2 = √(2·SDNN² − SD1²), SD1/SD2 ratio, SDNN

### EWMA baseline
| Metric | Alpha | Effective window |
|---|---|---|
| RMSSD / HR | 0.005 | ~15 min |
| LF/HF | 0.030 | ~30 s (extended cadence) |

Baseline is warm after 10 minutes. Frozen during Warning/Alerting states.

### Why RMSSD?
RMSSD is the gold-standard short-window parasympathetic HRV marker. A sudden drop indicates sympathetic activation — the physiological signature of a stress/dysregulation response. The 60-second window gives responsiveness without excess noise; the EWMA baseline personalises the threshold to the individual.

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

Optional LF/HF corroboration is available but disabled by default; enable after the baseline has calibrated.

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

The tray icon right-click menu provides quick access to: log annotation, pause monitoring for 1 hour, open the status window, open the log folder, and quit.
