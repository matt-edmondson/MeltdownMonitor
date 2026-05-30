# Raw ECG Waveform Acquisition — Design

Date: 2026-05-30
Status: Draft (pending spec review)

## Problem

Today the app never sees an actual electrocardiogram. Both BLE drivers
(`MeltdownMonitor.Ble.Windows/PolarHrSource.cs`,
`MeltdownMonitor.Ble.Apple/PolarHrSource.cs`) subscribe only to the **standard
GATT Heart Rate Service** (`0x180D`), reading the Heart Rate Measurement
characteristic (`0x2A37`). That characteristic delivers *derived* data: a BPM
integer and a list of pre-computed RR intervals quantised to 1/1024 s. The
sensor's firmware has already done the R-peak detection, artifact handling, and
quantisation for us — we receive its conclusions, not the signal.

The Polar H10 chest strap also exposes its proprietary **Polar Measurement Data
(PMD) service**, which streams the **raw single-lead ECG waveform at 130 Hz in
microvolts**. Reading that signal directly unlocks capabilities the derived RR
stream cannot provide:

1. **Live waveform display** — show the user their own ECG trace, not just a HR number.
2. **Our own R-peak / RR detection** — run QRS detection on the raw signal so we
   control fidelity and artifact handling, rather than trusting the sensor's
   black-box RR values (whose 1/1024 s quantisation also caps RMSSD precision).
3. **Derived base metrics beyond RR** — instantaneous HR from detected peaks,
   QRS morphology basics, and a foundation for richer analysis later.
4. **Record & export** — capture raw ECG to storage and export it (CSV first,
   EDF as a fast-follow) for offline or clinical review.
5. **Signal quality / lead-off detection** — use the raw waveform (and optionally
   the H10 accelerometer) to know *when HRV data is untrustworthy* (poor contact,
   motion artifact, lead-off), so the detector can suppress false alerts.

This is **Polar H10 only**. The Verity Sense is a PPG optical sensor and has no
ECG; it keeps the existing HR-service path unchanged.

## Goals

1. A **cross-platform PMD/ECG acquisition layer**: pure, unit-tested protocol
   logic in `Core`, with thin Windows (WinRT) and Apple (CoreBluetooth) drivers.
2. **Live ECG waveform** available to the UI on both heads.
3. **Our own R-peak detector** in Core that turns the raw waveform into the
   existing `Beat` stream, usable as a drop-in `IBeatSource` so the whole
   HRV → baseline → detection pipeline works unchanged downstream.
4. **Opt-in raw recording + CSV export.**
5. **Signal-quality / lead-off classification** surfaced to the pipeline.

## Non-goals (YAGNI for v1)

- 12-lead or multi-lead ECG (H10 is single-lead).
- Clinical diagnosis, arrhythmia classification (AF/PVC detection) — the data
  model should not *preclude* it, but it is explicitly out of scope.
- EDF/medical export formats (CSV first; EDF deferred).
- Streaming the H10 accelerometer for activity tracking (we only consider ACC as
  an *optional* future input to signal quality; v1 derives quality from ECG alone).
- Continuous always-on raw persistence by default (storage cost is ~33 MB/day; see
  "Recording & storage"). Raw capture is an explicit, bounded user action in v1.
- Replacing the HR-service path. The PMD path is **additive** and selectable.

## Background: the PMD protocol

Confirmed against the Polar BLE SDK technical documentation and community
reverse-engineering references (see "References"). Exact byte sequences MUST be
re-verified against `polar-ble-sdk` and real hardware during implementation —
this section is the design intent, not a substitute for the field-level spec.

**Service & characteristics**

| Role | UUID |
|---|---|
| PMD Service | `FB005C80-02E7-F387-1CAD-8ACD2D8DF0C8` |
| PMD Control Point (write + indicate) | `FB005C81-02E7-F387-1CAD-8ACD2D8DF0C8` |
| PMD Data (notify) | `FB005C82-02E7-F387-1CAD-8ACD2D8DF0C8` |

**Measurement type codes** (subset): `0x00` ECG, `0x01` PPG, `0x02` ACC,
`0x03` PPI, `0x05` GYRO, `0x06` MAG.

**Lifecycle**
1. Discover the PMD service + both characteristics.
2. Enable indications on the Control Point and notifications on Data.
3. (Optional) **Read** the Control Point → device returns a supported-features
   bitmask (confirms ECG is available before we try to start it).
4. **Write start-measurement** for ECG to the Control Point.
5. Receive ECG frames on the Data characteristic.
6. **Write stop-measurement** on teardown.

**Start-measurement command (ECG, H10)** — write to Control Point:

```
0x02  0x00  0x00 0x01 0x82 0x00  0x01 0x01 0x0E 0x00
 │     │     └─ sample-rate setting ─┘ └─ resolution setting ─┘
 │     │        type=0x00, len=1,         type=0x01, len=1,
 │     │        value=130 (0x0082 LE)     value=14  (0x000E LE)
 │     └─ measurement type = ECG (0x00)
 └─ opcode = start measurement (0x02)
```

Stop is opcode `0x03` followed by the measurement type byte. The Control Point
**indicates** a response with an opcode echo + error code; we must check it
succeeded before trusting the data stream.

**ECG data frame** (Data characteristic notification):

```
byte 0      : measurement type (0x00 = ECG)
bytes 1..8  : timestamp, uint64 little-endian, nanoseconds (device epoch,
              timestamp of the LAST sample in the frame)
byte 9      : frame type (0x00 = raw, uncompressed ECG samples)
bytes 10..N : ECG samples — each sample is a signed 24-bit (3-byte)
              little-endian integer in microvolts (µV), two's complement.
```

A full frame is ~73 samples (≈229 bytes) at 130 Hz, i.e. roughly one
notification every ~0.5 s. The device timestamp is monotonic and high-precision;
we anchor it to wall-clock once and interpolate per-sample timestamps from the
sample rate (see "Timestamping").

## Architecture

Preserve the existing layering. Everything that can be pure C# lives in `Core`
and is unit-tested without a device; the platform projects stay thin.

```
Core (net10.0, pure, tested)
  Pmd/PmdControlPoint.cs        command builder + response parser
  Pmd/EcgFrame.cs               record: device-ts, sample-rate, µV samples
  Pmd/EcgFrameParser.cs         raw bytes → EcgFrame (int24 LE decode)
  Pmd/IEcgSource.cs             IAsyncEnumerable<EcgFrame> abstraction
  Ecg/EcgRPeakDetector.cs       Pan–Tompkins-style QRS → R-peak timestamps
  Ecg/EcgBeatSource.cs          IEcgSource → IBeatSource (RR from peaks)
  Ecg/EcgSignalQuality.cs       per-window quality + lead-off classification
  Ecg/EcgRingBuffer.cs          fixed-window buffer/decimator for live display
  Persistence/ (extend)         ecg recording session table + CSV export

Ble.Windows (WinRT)            PolarEcgSource : IEcgSource   (PMD GATT)
Ble.Apple   (CoreBluetooth)    PolarEcgSource : IEcgSource   (PMD GATT)

App (Windows)                  EcgWaveformView (ImGui); Pipeline wiring;
                               settings: beat-source mode, recording controls
Mobile / iOS heads             waveform view (Avalonia / native) — fast-follow
```

### The key seam: ECG as an alternative `IBeatSource`

The existing pipeline consumes `IBeatSource.GetBeatsAsync()` and is entirely
sensor-agnostic downstream of it. We exploit that:

- `IEcgSource` is the new low-level stream of raw frames (for display + recording).
- `EcgBeatSource` *wraps* an `IEcgSource`, runs `EcgRPeakDetector`, and emits the
  existing `Beat` record (timestamp, RR ms, instantaneous HR, artifact flag). It
  is itself an `IBeatSource`.

So the user (via settings) selects the **beat source mode**:

| Mode | Beat source | ECG waveform available? |
|---|---|---|
| `HrService` (default, today's behaviour) | `PolarHrSource` | No |
| `EcgDerived` | `EcgBeatSource(PolarEcgSource)` | Yes |

In `EcgDerived` mode the *same* `EcgSource` instance feeds three consumers: the
waveform view, the recorder, and the R-peak detector — so we open one PMD stream,
not three. The `Pipeline` change is a one-line source-selection switch
(`Pipeline.cs:110`); nothing downstream changes.

This means **RR is no longer quantised to 1/1024 s** in ECG mode — we get RR at
the 130 Hz sample resolution (~7.7 ms), and finer with parabolic peak
interpolation. That is a direct RMSSD-fidelity win and a core reason to read raw
ECG at all.

### Data model

```csharp
// Core/Pmd/EcgFrame.cs
public readonly record struct EcgFrame(
    DateTimeOffset HostTimestamp,   // wall-clock anchor for the last sample
    long DeviceTimestampNs,         // raw device monotonic ns (last sample)
    int SampleRateHz,               // 130 for H10
    IReadOnlyList<int> SamplesMicrovolts);
```

`Beat` is unchanged — `EcgBeatSource` produces the existing record so persistence,
HRV, baseline, and detection are untouched.

### Timestamping

The device timestamp is monotonic ns from an arbitrary device epoch — good for
*relative* spacing, useless as wall-clock. On the first frame we record
`(deviceTs0, DateTimeOffset.UtcNow)` as an anchor and thereafter compute each
sample's host time as `anchor + (deviceTs - deviceTs0)`, distributing samples
within a frame by `1/SampleRateHz`. This keeps RR intervals derived from device
timing (jitter-free) while still being anchorable to the existing wall-clock
`beats`/`hrv_samples` tables. A large host/device drift over a long session is
acceptable for HRV (we care about *intervals*, not absolute time).

### R-peak detection (`EcgRPeakDetector`)

A standard **Pan–Tompkins** pipeline, implemented as a streaming filter so it
works on the live frame stream, not just batch:

1. Band-pass (≈5–15 Hz) to isolate QRS energy.
2. Derivative → square → moving-window integration.
3. Adaptive dual-threshold peak picking with a refractory period (~200 ms,
   physiological max ~300 bpm guard).
4. Parabolic interpolation around the integrated peak for sub-sample R timing.
5. Emit `Beat` per detected peak: RR = Δt to previous peak, instantaneous HR =
   60000/RR, `IsArtifact` set via the existing `RrArtifactFilter` (reused
   verbatim — the absolute 300–2000 ms + moving-median rule still applies).

Validation: replay the existing `beats` history is *not* enough (it has no raw
ECG). Instead, validate the detector against (a) synthetic ECG generated at known
RR sequences, and (b) the device's *own* RR stream as a cross-check during a live
session (the two should agree within a few ms; large disagreement is a detector
or signal-quality bug). The synthetic generator is the deterministic unit-test
oracle.

### Signal quality & lead-off (`EcgSignalQuality`)

Per analysis window (e.g. 2 s), classify the signal into
`Good / Marginal / Poor / LeadOff`:

- **Lead-off**: flat-line / rail-saturation / near-zero variance for the window
  (and/or the device's contact bit from the HR service if available).
- **Poor / motion**: out-of-band power dominates QRS-band power; baseline wander
  beyond a threshold; implausible peak rate.
- **Good**: clean QRS with a stable amplitude envelope.

The classification is exposed on the pipeline so the detector can **suppress
alerts** (or lower confidence) during `Poor`/`LeadOff` windows — a real
false-positive guard, since motion artifact otherwise looks like an RMSSD
collapse. Hooking it into `DysregulationDetector` confidence is staged
separately (see plan Phase 5) so the acquisition work can land first.

### Recording & storage

130 Hz × 3 B/sample ≈ **390 B/s ≈ 33 MB/day** raw — too much to persist
always-on by default, and not the product's purpose (the product persists
`beats`/`hrv_samples`, which stay tiny). Therefore:

- **Live display** uses an in-memory `EcgRingBuffer` (e.g. last 10 s); nothing
  persisted.
- **Recording** is an explicit, **bounded user action** ("Record ECG" → writes a
  capture session). Stored in a **new `ecg_sessions` + `ecg_samples` schema**
  (or, simpler, a per-session blob/file under the app data dir). Each session row
  has start/stop, sample rate, and a foreign-key to its samples.
- **Export** writes a session to **CSV** (`timestamp_ms, microvolts`) under a
  user-chosen path. EDF is a deferred fast-follow.
- The existing `beats`/`hrv_samples` pipeline runs regardless of recording — RR
  is always derived; only the *raw waveform* is gated behind recording.

This keeps the default footprint unchanged and makes raw capture a deliberate
opt-in, consistent with the app's privacy-respecting, append-only ethos.

## Platform drivers

Both implement `IEcgSource` and mirror the existing HR-source structure:

**Windows (`Ble.Windows/PolarEcgSource.cs`)** — WinRT. Same scan/connect/backoff
machinery as `PolarHrSource` (factor the shared connect+reconnect logic if
convenient), then: get PMD service, write Control Point start command, subscribe
to Data notifications, push bytes through `EcgFrameParser`, surface
`IAsyncEnumerable<EcgFrame>` via a `Channel`. Restrict to `PolarDeviceType.H10`.

**Apple (`Ble.Apple/PolarEcgSource.cs`)** — CoreBluetooth. Reuse the existing
background-safe restoration pattern (restore identifier, service-scoped scans,
`WillRestoreState`). Discover PMD service + characteristics, `SetNotifyValue` on
Data, `WriteValue` the start command on Control Point, decode in the shared Core
parser. iOS background streaming of PMD at 130 Hz needs the existing background
modes; verify against the data-protection/SQLite notes in the iOS design doc
(§4.7) if recording is enabled while locked.

Because all decoding/detection lives in Core, the two drivers contain **only**
GATT plumbing and no protocol logic — exactly like the HR sources today.

## Settings & UX

- New setting: **beat-source mode** (`HrService` | `EcgDerived`), default
  `HrService` (zero behaviour change unless opted in). Surfaced in the Settings
  surface alongside `DeviceType`. `EcgDerived` is only valid for the H10.
- New **ECG waveform** tab/view in the Windows status window (ImGui draw-list
  scrolling trace, Macchiato palette, reusing the `RegulationFieldView` rendering
  approach). Marker for detected R-peaks; a quality/lead-off chip.
- **Record / Stop / Export** controls for raw capture.
- iOS waveform view is a fast-follow (Avalonia/native), reusing the Core ring
  buffer + parser.

## Error handling / edge cases

- **Non-H10 device selected** in `EcgDerived` mode → fall back to HR service with
  a surfaced notice (Verity Sense has no ECG). Never silently produce no data.
- **PMD start rejected** (Control Point error response / unsupported) → log,
  surface, and fall back to the HR-service `IBeatSource` so monitoring continues.
- **Disconnect / out-of-range** → same exponential-backoff reconnect as the HR
  source; on reconnect, re-issue the start command and re-anchor timestamps.
- **Frame gaps / dropped notifications** → detector tolerates gaps; the RR for the
  first peak after a gap is dropped (no valid predecessor) rather than emitting a
  giant artifact RR.
- **Lead-off mid-session** → quality classifier flags it; beats during lead-off
  are marked artifact and (Phase 5) suppress alerts.
- **Recording storage full / write error** → stop recording, surface, keep the
  live pipeline running. Recording failure must never take down monitoring.

## Testing

Pure-Core unit tests (MSTest, no device), mirroring the existing convention that
all testable logic lives in Core:

- `EcgFrameParser`: int24 LE two's-complement decode (positive, negative, zero,
  boundary), frame header parse, multi-sample frames, truncated/short frames.
- `PmdControlPoint`: start-command bytes for ECG match the spec exactly;
  response parser distinguishes success vs error opcodes.
- `EcgRPeakDetector`: synthetic ECG at known RR sequences → detected RR matches
  within tolerance; refractory period rejects double-counts; noise injection
  doesn't fabricate peaks; recovers after a signal gap.
- `EcgBeatSource`: end-to-end synthetic `IEcgSource` → `Beat` stream with correct
  RR + HR; artifact filter applied; first-after-gap RR dropped.
- `EcgSignalQuality`: flat-line → LeadOff; clean QRS → Good; high out-of-band
  power → Poor.
- `EcgRingBuffer`: windowing/decimation correctness and thread-safety contract.
- Persistence: ECG session round-trip; CSV export format.

Platform drivers (Windows/Apple) and the ImGui/Avalonia views are **not**
unit-tested (the test project references Core only and is cross-platform) — they
are verified by building and running against a real Polar H10, cross-checking our
derived RR against the device's HR-service RR in the same session.

## Rollout

Phased so each phase is independently valuable and reviewable (see the companion
implementation plan):

1. **Core protocol + parser** (no device): `EcgFrame`, `EcgFrameParser`,
   `PmdControlPoint`, `IEcgSource`. Fully tested.
2. **Core detection**: `EcgRPeakDetector`, `EcgBeatSource`, synthetic ECG
   generator + acceptance tests.
3. **Windows driver + waveform view + beat-source setting**: first end-to-end
   live ECG on a real H10.
4. **Recording + CSV export.**
5. **Signal quality + detector confidence integration.**
6. **Apple driver + iOS waveform view** (mirrors Phase 3 on CoreBluetooth).

## References

- Polar BLE SDK — PMD technical documentation and H10 product docs
  (`github.com/polarofficial/polar-ble-sdk`). Authoritative for byte-exact
  command/frame formats; re-verify against the installed SDK version.
- Polar Measurement Data Specification (community mirror) — PMD service
  `FB005C80-…`, ECG 130 Hz µV, 3-byte signed-LE samples, ~73 samples/frame.
- Pan, J. & Tompkins, W.J. (1985), "A Real-Time QRS Detection Algorithm" —
  the R-peak detector basis.
