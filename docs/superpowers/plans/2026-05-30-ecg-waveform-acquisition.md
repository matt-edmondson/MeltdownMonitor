# Raw ECG Waveform Acquisition — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Read the Polar H10's raw single-lead ECG waveform (130 Hz, µV) via its
PMD service and build on it: live waveform display, our own R-peak/RR detection,
raw recording + CSV export, and signal-quality/lead-off classification — across
Windows and Apple, with all protocol/analysis logic pure and tested in `Core`.

**Design:** See `docs/superpowers/specs/2026-05-30-ecg-waveform-acquisition-design.md`.
Read it before starting; it carries the protocol details, the data model, the
`IEcgSource` → `EcgBeatSource` → `IBeatSource` seam, and the storage strategy.

**Architecture in one line:** new low-level `IEcgSource` (raw frames) feeds (a)
the waveform view, (b) the recorder, and (c) `EcgBeatSource`, which runs the
R-peak detector and emits the *existing* `Beat` record — so the entire
HRV → baseline → detection pipeline downstream is untouched and the change at
`Pipeline.cs:110` is a one-line source switch.

**Tech stack:** C# / .NET 10, MSTest; WinRT (`Windows.Devices.Bluetooth.*`) on
Windows, CoreBluetooth on Apple; `Hexa.NET.ImGui` draw-list for the Windows
waveform view; `Microsoft.Data.Sqlite` for recording.

**Scope notes:** Polar **H10 only** (Verity Sense PPG has no ECG). The PMD path
is **additive** — default behaviour (HR-service RR) is unchanged until the user
opts into `EcgDerived` mode. Exact PMD byte sequences MUST be re-verified against
`polar-ble-sdk` and real hardware during Phase 3.

---

## Phase map

| Phase | Outcome | Device needed? |
|---|---|---|
| 1 | Core PMD protocol + frame parser | No (unit tests) |
| 2 | Core R-peak detector + `EcgBeatSource` + synthetic ECG | No (unit tests) |
| 3 | Windows driver + waveform view + beat-source setting (first live ECG) | **Yes (H10)** |
| 4 | Raw recording + CSV export | H10 to verify |
| 5 | Signal quality + detector confidence integration | H10 to verify |
| 6 | Apple driver + iOS waveform view | iOS + H10 |

Phases 1–2 are pure Core and can be implemented and merged with full test
coverage before any hardware is involved. Each phase is independently reviewable.

---

## Phase 1 — Core: PMD protocol + ECG frame parser

**Files:**
- Create: `MeltdownMonitor.Core/Pmd/EcgFrame.cs`
- Create: `MeltdownMonitor.Core/Pmd/IEcgSource.cs`
- Create: `MeltdownMonitor.Core/Pmd/PmdControlPoint.cs`
- Create: `MeltdownMonitor.Core/Pmd/EcgFrameParser.cs`
- Test: `MeltdownMonitor.Tests/EcgFrameParserTests.cs`
- Test: `MeltdownMonitor.Tests/PmdControlPointTests.cs`

- [ ] **Step 1: Define `EcgFrame` and `IEcgSource`**

`EcgFrame.cs`:
```csharp
namespace MeltdownMonitor.Core.Pmd;

/// <summary>One PMD ECG data frame: a contiguous run of µV samples plus timing.</summary>
public readonly record struct EcgFrame(
    DateTimeOffset HostTimestamp,   // wall-clock anchor for the LAST sample
    long DeviceTimestampNs,         // raw device monotonic ns (LAST sample)
    int SampleRateHz,               // 130 for the H10
    IReadOnlyList<int> SamplesMicrovolts);
```

`IEcgSource.cs` (mirrors `IBeatSource`):
```csharp
namespace MeltdownMonitor.Core.Pmd;

public interface IEcgSource
{
    IAsyncEnumerable<EcgFrame> GetFramesAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Write failing parser tests**

`EcgFrameParserTests.cs` — cover int24 little-endian two's-complement decode
(positive, negative, zero, ±boundary), the frame header (measurement type byte,
8-byte ns timestamp, frame-type byte), a multi-sample frame, and a truncated
frame (must throw or return empty, decide and assert). Use hand-built byte arrays
from the spec's frame layout.

- [ ] **Step 3: Implement `EcgFrameParser`**

`EcgFrameParser.cs`: `static EcgFrame Parse(ReadOnlySpan<byte> payload, DateTimeOffset host)`.
- Validate `payload[0] == 0x00` (ECG) and frame type `payload[9] == 0x00` (raw).
- Read `DeviceTimestampNs` = `BinaryPrimitives.ReadUInt64LittleEndian(payload[1..9])`.
- From offset 10, decode 3-byte signed LE samples: combine bytes, sign-extend bit 23.
- `SampleRateHz` is a constant for v1 (130); keep it a parameter/const so a future
  settings read can drive it.

- [ ] **Step 4: Write failing `PmdControlPoint` tests**

`PmdControlPointTests.cs`:
- `StartEcg()` returns exactly `02 00 00 01 82 00 01 01 0E 00` (assert byte-for-byte).
- `StopEcg()` returns `03 00`.
- `ParseResponse(bytes)` distinguishes a success response from an error opcode and
  surfaces the error code (build expected response frames from the spec).

- [ ] **Step 5: Implement `PmdControlPoint`**

`PmdControlPoint.cs`: pure builders (`byte[] StartEcg()`, `byte[] StopEcg()`) and a
`PmdControlResponse ParseResponse(ReadOnlySpan<byte>)` returning
`(opcode, measurementType, status)`. Add named constants for opcodes (`Start=0x02`,
`Stop=0x03`), measurement type (`Ecg=0x00`), setting types (`SampleRate=0x00`,
`Resolution=0x01`), and H10 values (`130`, `14`). **Re-verify these bytes against
`polar-ble-sdk` before Phase 3 goes near hardware.**

- [ ] **Step 6: Run tests, commit**

```bash
dotnet test --filter "FullyQualifiedName~EcgFrameParserTests|FullyQualifiedName~PmdControlPointTests"
git add MeltdownMonitor.Core/Pmd MeltdownMonitor.Tests/EcgFrameParserTests.cs MeltdownMonitor.Tests/PmdControlPointTests.cs
git commit -m "feat: add PMD ECG frame parser and control-point command builder"
```

---

## Phase 2 — Core: R-peak detector + `EcgBeatSource`

**Files:**
- Create: `MeltdownMonitor.Tests/SyntheticEcgGenerator.cs` (test helper)
- Create: `MeltdownMonitor.Core/Ecg/EcgRPeakDetector.cs`
- Create: `MeltdownMonitor.Core/Ecg/EcgBeatSource.cs`
- Test: `MeltdownMonitor.Tests/EcgRPeakDetectorTests.cs`
- Test: `MeltdownMonitor.Tests/EcgBeatSourceTests.cs`

- [ ] **Step 1: Synthetic ECG generator (test oracle)**

A helper that, given an RR sequence (ms) and sample rate, emits a plausible ECG
sample stream (Gaussian-ish QRS complexes at the specified beat times, optional
baseline wander + Gaussian noise). This is the deterministic ground truth for
detector tests — the detector's recovered RR must match the input RR.

- [ ] **Step 2: Failing detector tests**

`EcgRPeakDetectorTests.cs`:
- Constant 60 bpm (RR=1000 ms) synthetic → detected RR within tolerance (≤ ~8 ms).
- Varying RR ramp → recovered RR tracks input monotonically.
- Refractory period: closely-spaced spikes don't double-count (no RR < ~200 ms).
- Added noise doesn't fabricate peaks (count matches expected within ±1).
- Signal gap (zeros mid-stream) → detector recovers, drops the bridging RR.

- [ ] **Step 3: Implement `EcgRPeakDetector`**

Streaming Pan–Tompkins (see design §"R-peak detection"): band-pass → derivative →
square → moving-window integration → adaptive dual-threshold + refractory →
parabolic interpolation for sub-sample R timing. API: feed samples (with their
interpolated host timestamps), get an event/list of R-peak `DateTimeOffset`s.
Keep it allocation-light and state-carrying across frames (it must work live).

- [ ] **Step 4: Failing `EcgBeatSource` tests**

`EcgBeatSourceTests.cs`: wrap a synthetic `IEcgSource` (replays generated frames),
assert the produced `Beat` stream has correct RR + instantaneous HR, applies
`RrArtifactFilter`, and drops the first RR after a gap (no valid predecessor).

- [ ] **Step 5: Implement `EcgBeatSource`**

`EcgBeatSource(IEcgSource source) : IBeatSource`. In `GetBeatsAsync`: iterate
frames, interpolate per-sample host timestamps from `(SampleRateHz, frame anchor)`
per the design's "Timestamping" section, run the detector, and for each detected
peak emit `new Beat(ts, rrMs, hrBpm, isArtifact)` using the **existing**
`RrArtifactFilter`. This is the drop-in `IBeatSource` for the pipeline.

- [ ] **Step 6: Run tests, commit**

```bash
dotnet test --filter "FullyQualifiedName~EcgRPeakDetectorTests|FullyQualifiedName~EcgBeatSourceTests"
git add MeltdownMonitor.Core/Ecg MeltdownMonitor.Tests/SyntheticEcgGenerator.cs MeltdownMonitor.Tests/EcgRPeakDetectorTests.cs MeltdownMonitor.Tests/EcgBeatSourceTests.cs
git commit -m "feat: add ECG R-peak detector and EcgBeatSource (raw ECG -> beats)"
```

---

## Phase 3 — Windows driver + waveform view + beat-source setting

First end-to-end live ECG. **Requires a real Polar H10.**

**Files:**
- Create: `MeltdownMonitor.Ble.Windows/PolarEcgSource.cs`
- Create: `MeltdownMonitor.Core/Ecg/EcgRingBuffer.cs`
- Test: `MeltdownMonitor.Tests/EcgRingBufferTests.cs`
- Create: `MeltdownMonitor.App/Ecg/EcgWaveformView.cs`
- Modify: `MeltdownMonitor.App/Pipeline.cs` (source selection)
- Modify: settings (`AppSettings`) — add `BeatSourceMode`
- Modify: `MeltdownMonitor.App/StatusWindow.cs` (add the waveform tab)

- [ ] **Step 1: `EcgRingBuffer` (Core, tested)**

Fixed-window thread-safe buffer (e.g. last 10 s of µV samples) with optional
decimation for display, plus a snapshot method for the render thread. Unit-test
windowing/decimation correctness (`EcgRingBufferTests.cs`).

- [ ] **Step 2: `PolarEcgSource` (WinRT)**

Implement `IEcgSource` over the PMD service. Reuse the scan/connect/backoff
approach from `PolarHrSource` (factor shared connect+reconnect helpers if clean):
discover PMD service (`FB005C80-…`) + Control Point (`…81`) + Data (`…82`); enable
Control Point indications and Data notifications; write `PmdControlPoint.StartEcg()`;
verify the indicated response with `PmdControlPoint.ParseResponse`; push Data bytes
through `EcgFrameParser.Parse` into a `Channel<EcgFrame>`. Restrict to
`PolarDeviceType.H10`. On teardown write `StopEcg()`. **Verify byte sequences on
hardware here.**

- [ ] **Step 3: Beat-source setting + pipeline switch**

Add `BeatSourceMode { HrService, EcgDerived }` to `AppSettings` (default
`HrService`). In `Pipeline.RunAsync` (`Pipeline.cs:110`), select the source:
```csharp
IBeatSource source = _settings.BeatSourceMode == BeatSourceMode.EcgDerived
    ? new EcgBeatSource(_ecgSource = new PolarEcgSource(_settings.DeviceType))
    : new PolarHrSource(_settings.DeviceType);
```
Keep a reference to the live `IEcgSource` so the waveform view/recorder can share
the one stream (or expose frames via a pipeline event, mirroring `BeatReceived`).
Add a `FrameReceived` event to the pipeline for the view to subscribe to.

- [ ] **Step 4: `EcgWaveformView` (ImGui)**

Scrolling waveform using the ImGui draw list (same rendering approach as
`RegulationFieldView`): subscribe to the pipeline's `FrameReceived`, append to an
`EcgRingBuffer`, draw the µV trace each frame with auto-scaled amplitude, mark
detected R-peaks, show current HR + a placeholder quality chip. Use
`MacchiatoPalette`.

- [ ] **Step 5: Wire the tab in `StatusWindow`**

Add an "ECG" tab (after Regulation Field / Overview). Construct/dispose the view
like the other tabs; only meaningful in `EcgDerived` mode (show a hint otherwise).

- [ ] **Step 6: Build, run on hardware, verify, commit**

```bash
dotnet build MeltdownMonitor.App/MeltdownMonitor.App.csproj -o "$env:TEMP\mm_build"
dotnet run --project MeltdownMonitor.App
```
Verify with a real H10 in `EcgDerived` mode: waveform scrolls, R-peak markers land
on QRS complexes, HR is sane, and **our derived RR cross-checks against the H10's
own HR-service RR within a few ms** (the key acceptance check). Then commit.

---

## Phase 4 — Raw recording + CSV export

**Files:**
- Modify: `MeltdownMonitor.Core/Persistence/MeltdownRepository.cs` (ECG schema)
- Create: `MeltdownMonitor.Core/Persistence/EcgRecorder.cs`
- Create: `MeltdownMonitor.Core/Persistence/EcgCsvExporter.cs`
- Test: `MeltdownMonitor.Tests/EcgRecorderTests.cs`, `EcgCsvExporterTests.cs`
- Modify: `MeltdownMonitor.App/StatusWindow.cs` (Record/Stop/Export controls)

- [ ] **Step 1: Schema + recorder (Core, tested)**

Add `ecg_sessions` (id, started_ts, stopped_ts, sample_rate) and `ecg_samples`
(session_id, ts_ms, microvolts) to `EnsureSchema`. `EcgRecorder` opens a session,
batches sample inserts (transaction per N frames for throughput), and closes the
session. Round-trip test. Note the ~33 MB/day footprint in the recorder's doc
comment — recording is explicit and bounded, never always-on.

- [ ] **Step 2: CSV export (Core, tested)**

`EcgCsvExporter.Export(repository, sessionId, path)` → `timestamp_ms,microvolts`
rows. Assert format/round-trip in a unit test.

- [ ] **Step 3: App controls**

Record / Stop / Export buttons on the ECG tab; recording only available in
`EcgDerived` mode. Recording failure must surface and never stop live monitoring.

- [ ] **Step 4: Build, verify, commit.**

---

## Phase 5 — Signal quality + detector confidence

**Files:**
- Create: `MeltdownMonitor.Core/Ecg/EcgSignalQuality.cs`
- Test: `MeltdownMonitor.Tests/EcgSignalQualityTests.cs`
- Modify: `EcgBeatSource` / `Pipeline` to surface quality
- Modify: `MeltdownMonitor.Core/Detection/DysregulationDetector.cs` (suppression)

- [ ] **Step 1: `EcgSignalQuality` (Core, tested)**

Per-window classifier → `Good / Marginal / Poor / LeadOff` from variance,
in-band vs out-of-band power, baseline wander, and plausible peak rate (design
§"Signal quality"). Tests: flat-line→LeadOff, clean QRS→Good, out-of-band→Poor.

- [ ] **Step 2: Surface quality to the pipeline**

Compute quality alongside detection in `EcgBeatSource`; expose current quality on
the pipeline (event or property) for the view's quality chip.

- [ ] **Step 3: Alert suppression**

When quality is `Poor`/`LeadOff`, mark beats artifact and reduce detector
confidence so motion artifact can't trigger a false meltdown alert. Add a unit
test driving a quality-degraded window through detection and asserting no alert.

- [ ] **Step 4: Build, verify on hardware (induce motion/lead-off), commit.**

---

## Phase 6 — Apple driver + iOS waveform view

**Files:**
- Create: `MeltdownMonitor.Ble.Apple/PolarEcgSource.cs`
- Modify: iOS composition root (source selection mirroring Windows)
- Create: iOS/Avalonia waveform view (reuses Core `EcgRingBuffer` + parser)

- [ ] **Step 1: `PolarEcgSource` (CoreBluetooth)**

`IEcgSource` over PMD, reusing the existing background-safe pattern (restore
identifier, service-scoped scans, `WillRestoreState`). Discover PMD service +
characteristics, `SetNotifyValue` on Data, `WriteValue` `StartEcg()` on Control
Point, decode via the shared Core `EcgFrameParser`. H10 only.

- [ ] **Step 2: iOS source selection**

Mirror the Windows beat-source switch in the iOS composition root. Verify PMD
background streaming against the iOS design doc's background/data-protection notes
(§4.7), especially if recording while the device is locked.

- [ ] **Step 3: iOS waveform view**

Reuse the Core ring buffer + parser; render the trace in the iOS/Avalonia UI.

- [ ] **Step 4: Build, verify on iOS + H10, commit.**

---

## Self-review / coverage notes

- **Downstream untouched:** the pipeline consumes `IBeatSource`; `EcgBeatSource`
  is one. HRV, baseline, detection, persistence of `beats`/`hrv_samples` are
  unchanged — only the *source* and the *new raw/waveform/quality surfaces* are added.
- **Default behaviour preserved:** `BeatSourceMode` defaults to `HrService`; no
  user sees any change until they opt into ECG. Verity Sense is unaffected.
- **Fidelity win:** ECG-derived RR escapes the HR service's 1/1024 s quantisation,
  improving RMSSD precision — a primary reason to read raw ECG.
- **Test altitude:** all protocol/detection/quality/recording logic is pure Core
  and unit-tested; platform drivers + views are hardware/visually verified, matching
  the repo's existing "Core-only test project" convention.
- **Protocol risk:** the PMD byte sequences are from spec + community references;
  Phase 3 Step 2 re-verifies them against `polar-ble-sdk` and a real H10 before
  trusting the stream. The Control Point response check guards against silent
  mis-starts.
- **Deferred (conscious):** EDF export, arrhythmia/AF classification, H10
  accelerometer as a quality input, multi-lead — all noted non-goals.
