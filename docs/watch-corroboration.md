# Apple Watch metric collection & corroboration

Status: **managed scaffold + tests landed** (Core + Mobile, no Mac required). The
watch→phone `WCSession` relay and the watchOS metric publisher are device-only
work (a Mac + paired Watch), deferred like the Phase W2 haptic transport.
Scope: collect heart-rate (and HRV-SDNN) metrics **from** an Apple Watch and use
the watch HR as a **second, independent witness** to cross-check the chest strap.

This is the *input* counterpart to `docs/watch-haptics.md` (the *output* path):
both ride the one phone↔watch `WCSession` link, in opposite directions. It opens
the watch-haptics doc's standing **Phase W3 — watch as HR source** as
*corroboration first* rather than *replacement*: the strap stays the source of
truth; the watch only confirms or contradicts it.

---

## 1. Why corroboration, and why HR

The dominant false positive in this app is an **artifact that mimics
dysregulation**: motion or poor electrode contact corrupts the strap's RR
intervals, RMSSD collapses and HR jumps, and the detector reads a meltdown that
isn't there. Motion corroboration (`docs/motion-corroboration.md`) already
catches the motion case via the accelerometer. The Apple Watch catches a wider
class: it is a **second heart-rate sensor on the same body**, and two sensors on
one body should agree on heart rate.

When they **disagree** — the strap says HR is racing but the wrist reads calm —
the strap signal is suspect, so the detector defers escalation, exactly as it
does while the body is moving or the sensor is off-body. This is the
conservative, calm-not-alarming choice: a missed alert is the more harmful error
for an awareness tool (the same principle the 2026-06-01 clinical audit applied
to LF/HF), so the tolerance is generous and only a **large, sustained**
disagreement gates.

Heart rate — not HRV — is the cross-check:

- HR is the robust, low-latency quantity both devices measure directly.
- watchOS exposes HRV as **SDNN**, not the strap's beat-to-beat **RMSSD**;
  cross-checking different HRV metrics on different windows would be noise.
- The watch's HRV-SDNN is still **collected** (carried on the sample) for future
  use, but the gate only consults HR.

Wrist-optical HR lags and smooths chest-ECG HR on fast transients, so the watch
is never treated as ground truth — it corroborates, it does not replace.

---

## 2. The managed / native boundary

```
 watchOS app (Swift)                         device-only (Mac + paired Watch)
   • HKWorkoutSession / HKAnchoredObjectQuery → HR + HRV-SDNN
   • WCSession.transferUserInfo / sendMessage → phone
   ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ native boundary ─ ─ ─ ─ ─
                                            ▼
 IWatchMetricSource (Core/Beats)            managed seam — same shape as IMotionSource
   │ WatchMetricReceived(WatchMetricSample)
   ▼
 WatchCorroborationMonitor (Core/Detection) managed, fully unit-tested — holds the
   │ Evaluate(strapHr, ts) → verdict        latest watch reading, cross-checks the
   │                                         strap HR at evaluation time
   ▼
 DysregulationDetector.Process(…, watch)    managed — gates escalation on Conflicted
                                            (UseWatchCorroboration), Unknown never gates
```

Everything above the native boundary is built and unit-tested now, on Linux/CI,
with no watch present — the same discipline the haptic publisher (W1) and the
motion corroboration shipped under.

---

## 3. What landed (Core + Mobile)

| Type | Location | Role |
|---|---|---|
| `WatchMetricSample` (HR, HRV-SDNN, contact, ts) | `Core/Beats/WatchMetricSample.cs` | The reading collected on the watch. |
| `IWatchMetricSource` | `Core/Beats/WatchMetricSample.cs` | Optional capability seam, mirrors `IMotionSource`. The iOS head implements it over `WCSession`; every other host contributes nothing. |
| `WatchCorroboration` (`Unknown` / `Confirmed` / `Conflicted`) | `Core/Detection/WatchCorroborationMonitor.cs` | The verdict. `Unknown` is the no-watch sentinel — like `MovementLevel.Unknown`, it never gates. |
| `WatchCorroborationMonitor` (+ `WatchCorroborationSnapshot`) | `Core/Detection/WatchCorroborationMonitor.cs` | Holds the latest watch reading; `Evaluate(strapHr, ts)` produces the verdict. Timestamp-driven (no wall clock) → deterministic/replay-safe. |
| `DetectionThresholds.UseWatchCorroboration` (default **on**) | `Core/Detection/DetectionThresholds.cs` | Whether a `Conflicted` verdict gates. Consulted only when a verdict is present, so a no-watch build is unaffected. |
| `DetectionThresholds.FreezeBaselineOnWatchConflict` (default **off**) | `Core/Detection/DetectionThresholds.cs` | Opt-in: also freeze the baseline on a `Conflicted` verdict (`BaselineHrvTracker.FreezeOnWatchConflict`), for movement-style symmetry. |
| `DysregulationDetector.Process(…, watch)` | `Core/Detection/DysregulationDetector.cs` | New optional param; `Conflicted` holds state + clears streaks, alongside the off-body and movement gates. |
| `MobileSettings.EnableWatchCorroboration` (default **off**) | `Mobile/MobileSettings.cs` | Master opt-in for the feature wiring. |
| `Pipeline` wiring + `CurrentWatchCorroboration` + `WatchCorroborationUpdated` | `Mobile/Pipeline.cs` | Subscribes the monitor when enabled and a source is present; evaluates per sample; fans the snapshot out for the UI. |

### Verdict logic (the gate)

`Evaluate(strapHr, strapTimestamp)` returns:

- **`Unknown`** (never gates) when there is no watch reading, the reading is stale
  relative to the strap sample (`Staleness`, default 30 s, either direction), the
  watch is off-wrist, or either HR is non-positive.
- **`Conflicted`** when `|watchHr − strapHr| ≥ ConflictToleranceBpm`
  (default **12 bpm**).
- **`Confirmed`** otherwise.

Tolerance and staleness live on the monitor (its "classification" knobs, like the
`MovementMonitor`'s g-thresholds); the on/off gate (`UseWatchCorroboration`) lives
on `DetectionThresholds` (like `MovementGateLevel`).

---

## 4. Scope notes & deliberate non-changes

- **Mobile pipeline only.** The Apple Watch is an iOS concept, so — unlike most
  changes, which touch both `Pipeline.cs` copies — the desktop (`App`) pipeline is
  untouched. Its `Process` call passes the default `Unknown`, so the Core change
  is behaviour-neutral there.
- **Detector gate by default; baseline freeze is opt-in.** A `Conflicted` verdict
  always defers escalation. Freezing the baseline on conflict (the movement gate's
  stricter treatment) is **off by default** behind
  `DetectionThresholds.FreezeBaselineOnWatchConflict`: the EWMA baseline's
  minutes-long memory absorbs a few suspect samples, so the gate alone is the
  lighter-touch default, but the option is there for the stricter symmetry. The
  Mobile pipeline sets `BaselineHrvTracker.FreezeOnWatchConflict` from that flag in
  `ApplyTuning`, gated on `UseWatchCorroboration` also being on; the verdict is
  evaluated before the baseline update so the freeze sees the current sample.
- **No UI toggle yet.** Unlike motion corroboration (which works fully on-device
  today), watch corroboration does nothing until the watch app + `WCSession`
  relay exist (a Mac-only phase), so a Settings toggle would govern a dormant
  feature. Deferred to the device phase, mirroring the watch-haptics §11 W1 note.
- **Composition roots pass null.** Until the iOS `WCSession`-backed
  `IWatchMetricSource` lands, every head constructs the pipeline with no watch
  source, so detection is byte-identical to before — exactly how the haptic
  publisher ran against `NoOpWatchSession`.

---

## 5. Tests (managed, run anywhere)

- **`WatchCorroborationMonitorTests`** — Unknown with no/stale/off-wrist/
  non-positive readings, Confirmed within tolerance, Conflicted at/beyond it,
  latest-wins on out-of-order delivery, snapshot, reset.
- **`WatchCorroborationGatingTests`** — Conflicted defers a severe drop and clears
  a building warning streak; Confirmed and Unknown alert normally;
  `UseWatchCorroboration = false` ignores conflict. Plus the opt-in baseline freeze:
  Conflicted freezes only when `FreezeOnWatchConflict` is set, never on
  Confirmed/Unknown, and the threshold defaults off.
- **`MobileSettingsSerializerTests`** — `EnableWatchCorroboration` round-trips and
  defaults off.

The watch/Swift half (real HR over a real `WCSession`) is validated on-device,
like the haptic transport and the Live Activity presentation.

---

## 6. Remaining device-only work

1. **iOS `IWatchMetricSource`** over `WCSession` (the same session object the
   haptic `IWatchSession` uses, receiving instead of sending), wired into
   `IosCompositionRoot` next to `_motionFallback`.
2. **watchOS metric publisher** — `HKWorkoutSession` / `HKAnchoredObjectQuery`
   for HR + HRV-SDNN, relayed via `WCSession.transferUserInfo` (queued) or
   `sendMessage` (live).
3. **Settings ▸ a watch section** exposing `EnableWatchCorroboration`, gated on a
   paired watch.
4. **On-device validation** of the tolerance/staleness defaults against real
   wrist-optical lag, and of whether `FreezeBaselineOnWatchConflict` (§4) should
   become the default once the conflict rate on a real watch is known.
