# Motion corroboration (Polar PMD + device IMU)

Movement is the dominant confounder for this app: physical exertion drops HRV and raises HR — the
exact signature `DysregulationDetector` looks for — so without a motion signal a brisk walk reads as
a meltdown. This feature streams an accelerometer, classifies movement, and uses it to **defer
alerts** and **freeze the baseline** during exertion. It is opt-in (`EnableMotionCorroboration`,
default off) and a no-op when no motion source is present.

## Sources, in priority order

1. **Polar strap accelerometer over PMD** — first-class. Polar's proprietary *Polar Measurement
   Data* service (`FB005C80-…`) exposes the chest/arm accelerometer the standard BLE Heart Rate
   service never surfaces. On the torso, it tracks the body directly.
2. **Device IMU fallback** — the phone/PC's own accelerometer (CoreMotion / Android `SensorManager`;
   desktop has none). A coarser proxy — it only moves when the device does — used when the connected
   sensor is non-Polar.

When both feed (a Polar strap *and* the phone IMU running together), `MovementMonitor` prefers the
strap and suppresses the IMU for `StrapPreferenceWindow` after each strap sample.

## Architecture

All PMD binary parsing lives in **Core** (`Core/Beats/Polar/`), platform-neutral and unit-tested,
mirroring how `HrMeasurementParser` splits the standard HR service. The BLE heads only subscribe to
two extra characteristics and hand the raw bytes over.

```
IMotionSource (strap PMD or device IMU)
   → MotionSample (g, tri-axial)
   → MovementMonitor (rolling AC-RMS intensity → MovementLevel: Still/Light/Moderate/Vigorous)
   → DysregulationDetector gate (defer escalation at/above MovementGateLevel)
   → BaselineHrvTracker freeze (skip updates at/above MovementFreezeLevel)
```

- **`MovementMonitor`** computes intensity as the RMS of acceleration magnitude *about its rolling
  mean* — the AC component — so it reads ~0 at rest whether or not the source includes gravity (raw
  accelerometer vs. Android linear-acceleration). Gate default is **Moderate** (walking): light
  fidgeting must NOT suppress alerts, because agitation is part of what we want to catch.
- The gate mirrors the existing **contact gate** (`SensorContactStatus.NotDetected`): hold state,
  clear streaks. `MovementLevel.Unknown` (no source) never gates, so behaviour is identical to a
  build with no accelerometer.

## PMD wire format (the subset we use)

| | |
|---|---|
| Service / Control Point / Data | `FB005C80…` / `…81` (write+indicate) / `…82` (notify) |
| Measurement types | ECG=0, PPG=1, **ACC=2**, **PPI=3** |
| Control op codes | get-settings=0x01, start=0x02, stop=0x03 |
| Frame header | `[type][8B ns timestamp][frame-type]` — frame-type bit 7 = delta-compressed |
| ACC | delta-compressed: 16-bit ref sample ×3 channels, then `[bits][count]` delta groups (LSB-first) |
| PPI | flat 6-byte samples: HR, PPI ms, error-estimate ms, flags (blocker / contact) |
| ECG | flat int24 LE microvolt samples (H10) |

Feature negotiation: read the control point once for the supported-type bitmask (bit *n* = type
*n*), then start only what the device offers (H10: ACC+ECG; Verity Sense: ACC+PPI).

## What's wired vs. deferred

- **Wired now:** ACC → motion → detector gate + baseline freeze, on all three heads + device-IMU
  fallback. This captures the headline value (killing the exercise false-positive).
- **Surfaced in the UI:** both pipelines raise `MovementUpdated` (level + g-RMS intensity) per sample
  and expose `CurrentMovement` / `CurrentMovementIntensity`. The desktop status header shows
  `Movement <level> (<g>) — gating` (warning-coloured when above the gate); the mobile Now screen
  shows the level and a "Moving — alerts deferred, baseline paused" cue. The intensity readout is
  there specifically to help tune `DetectionThresholds.MovementGateLevel` against a real sensor.
- **Selectable interval source (PPI + ECG), wired:** `IntervalSource` (HeartRateService / PolarPpi /
  PolarEcg, in settings) picks the one stream that supplies beats — never more than one, or beats
  would double-count. **PPI** (Verity Sense) becomes the source via `PolarPpi.ToBeat`, folding its
  per-beat blocker / error-estimate / contact flags into the artifact decision. **ECG** (H10) feeds
  the streaming `EcgRPeakDetector` (Core/Hrv, Pan–Tompkins-style, unit-tested), whose RR intervals
  become beats. Anti-double-count rule: HRS RR keeps flowing until the chosen Polar stream is
  *actually producing intervals*, then the head suppresses HRS — so an unsupported device (PPI on an
  H10) or a slow PMD start never goes silent. Like all BLE behaviour, only fully verifiable on the
  live app with a real sensor.
- **Live ECG waveform (dedicated view, both heads):** when the ECG source is active the heads forward
  raw samples (`IEcgSource` / `EcgSamples`, with per-batch R-peak offsets) into the Core
  `EcgWaveformBuffer` — a thread-safe rolling window that the pipeline exposes as a snapshot
  (`EcgWaveform`) and a `EcgUpdated` event. The buffer also derives a `EcgSignalQuality` cue
  (flatline/saturation/peak-rate). A dedicated **ECG tab** on each head (desktop `DrawEcgTab` via
  ImPlot; mobile `EcgView` + the `EcgStrip` Avalonia control) draws the scrolling trace with R-peak
  dots, a peak-derived heart rate, and the quality cue. It is a signal view, not a diagnostic ECG.

## Verification

Per the repo's BLE gotcha, real-time streaming/timing can only be confirmed on the live app + a real
Polar sensor. Core proves protocol correctness via unit tests (frame decode, delta decompression,
movement classification, gating); CI compiles the heads. The on-device gate is the user's to run.
