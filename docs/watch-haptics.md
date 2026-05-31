# Apple Watch companion — somatic feedback (iOS v1.1+)

Status: **Draft / Pre-implementation**
Author: design pass, May 2026
Scope: a watchOS companion that turns detected dysregulation into gentle,
non-jarring **haptic co-regulation cues on the wrist** — the first *output*
peripheral in a system that has so far been sensing-and-screen only.

This promotes the iOS design doc's standing item — §1 goal "eventual Apple
Watch companion" and the "out of scope for v1 (logged for v1.1+): WatchOS
companion (would benefit from native Swift; revisit then)" line. It deliberately
mirrors the Live Activity work (`docs/live-activity.md`, design doc Phase 8):
all testable logic stays in managed `MeltdownMonitor.Mobile`, and Swift is
confined to the one place watchOS forces it.

---

## 1. Why a watch, and why now

MeltdownMonitor today is **input + screen/audio only**. It senses the autonomic
nervous system (Polar BLE → HRV → `RegulationReading` / detection state machine)
and surfaces what it finds visually (Regulation Field, state pill, Live Activity)
or audibly (chime, notification). It has **no somatic output** — nothing that
acts *on* the body to help it settle.

A watch closes that loop: **sense → deliver a quiet bodily cue that nudges the
ANS back toward the parasympathetic "rest" branch.** Three things make the wrist
the right first actuator:

- It is the one always-on haptic device most users already wear.
- The Pipeline already emits exactly the signal a *proportional* haptic wants —
  a continuous, signed arousal-vs-baseline value (`RegulationReading.Index`),
  with a confidence gate — so the watch can render a **felt** version of the
  Regulation Field, not just a binary buzz.
- The slowest, highest-evidence lever for raising HRV is **paced breathing at
  resonance (~6 breaths/min)**; a wrist haptic that paces breath is the textbook
  somatic intervention and needs no screen attention.

### Goals

- A gentle, **opt-in** haptic that guides the user toward regulation when arousal
  rises against their own baseline, and gets out of the way otherwise.
- Reuse the Pipeline's existing signals verbatim — **no `MeltdownMonitor.Core`
  changes**, no second copy of any maths.
- Keep all decision logic in managed, unit-tested `MeltdownMonitor.Mobile`,
  following the Live Activity boundary exactly. Confine Swift to the watch app
  itself (watchOS gives no managed alternative).
- Stay inside Apple's non-medical wellness posture (design doc §4.4 / §11): the
  watch *guides*, it does not *treat*.

### Non-goals (this milestone)

- Haptics as an **alarm**. A sudden strong buzz on a meltdown can startle →
  *more* sympathetic activation. The watch down-regulates or stays silent; it
  never jolts. (§2.)
- A standalone watch app that runs the HRV pipeline on-device. The iPhone stays
  the brain; the watch is an actuator (and, as a stretch, a sensor — §11 W3).
- Replacing the Polar strap. Watch-as-HR-source is a separate stretch phase with
  its own fidelity caveats (§11 W3).
- iCloud sync, multi-watch, or complications beyond a minimal session surface.

---

## 2. Guiding principle — calm, not alarming

The whole product ethos is "calm, non-jarring" (README), and the audience —
PTSD / C-PTSD / autism — is precisely the population a startle response harms
most. That dictates the haptic design more than any API does:

1. **Cadence is always calming.** Paced-breath guidance targets the
   down-regulating resonance rate (~6 breaths/min). Rising arousal **never speeds
   the cue up** — only its *salience* (intensity) grows so it stays noticeable.
2. **Silent until trustworthy.** While the baseline is cold
   (`RegulationReading.Confidence` below a floor) the watch is **silent**,
   mirroring how the Regulation Field dims itself during calibration. A cue the
   app isn't sure about is worse than no cue.
3. **Guidance over interruption.** The default is a slow breath pacer, not a
   "you are melting down" tap.
4. **Always escapable, always opt-in.** Off by default; an intensity ceiling; a
   mode switch (some autistic users want only discrete taps, some only the
   continuous pacer, some neither); and a one-gesture stop on the watch itself.
5. **No claims.** In-app and on-watch copy says "regulation", "breathe",
   "settle" — never "treat", "therapy", or a clinical promise.

---

## 3. What the watch consumes — no Core changes

`MeltdownMonitor.Mobile/Pipeline.cs` already exposes everything the watch needs.
The companion is a *new consumer* of existing events, exactly like
`LiveActivityPublisher`:

| Pipeline surface | Type | Use on the watch |
|---|---|---|
| `ReadingUpdated` | `Action<RegulationReading>` | Drives the **proportional** paced-breath haptic — the felt Regulation Field. |
| `LatestReading` | `RegulationReading` | Latest value for a freshly-started session / state-only update. |
| `StateChanged` | `Action<DetectorState>` | Discrete escalation / recovery cues (bypass the throttle). |
| `AlertFired` | `Action<AlertPayload>` | Optional "must-arrive" cue on a confirmed alert. |
| `LatestContact` | `SensorContactStatus` | Suppress cues when the sensor isn't on-skin (no real signal). |
| `PausedUntil` (settings) | `DateTimeOffset?` | Honour pause — no cues while paused. |

`RegulationReading(double Index, double VariabilityQuality, double Confidence)`:

- `Index` ∈ [-1, 1] — signed arousal-vs-baseline. > 0 = sympathetic activation
  (toward the warm "meltdown" lobe); ≤ 0 = at/below baseline (calm). **This is
  the value the haptic intensity maps from.**
- `Confidence` ∈ [0, 1] — 0 while the baseline is cold, ramping to 1 once warm.
  **The silence gate (§2.2).**
- `VariabilityQuality` ∈ [0, 1] — collapsed (metronomic, stressed) → healthy.
  Available for a future texture/sharpness mapping; not required for v1.1.

---

## 4. Platform constraints — read before designing

These are the watchOS facts that actually drive the architecture.

### 4.1 watchOS is not .NET

The heads are `net10.0-ios` (iPhone only; `UIDeviceFamily` = 1). There is no
.NET watchOS target. A watch app is a **separate native Swift target**,
Xcode-managed and **not compiled by `dotnet`** — the same situation as
`MeltdownMonitor.iOS.WidgetExtension`. So haptic playback and the watch UI are
Swift, running on the watch. Everything that *can* be managed, is.

### 4.2 Phone ↔ watch transport — WatchConnectivity

The iPhone talks to the watch over **`WCSession` (WatchConnectivity)**, which —
unlike ActivityKit — **has managed bindings in .NET for iOS**. So the phone side
can stay pure C# in the iOS head; no `@_cdecl` Swift bridge is needed on the
phone (contrast the Live Activity, which needed one because ActivityKit is
unbound). Transfer modes, matched to each signal:

| Signal | Primitive | Why |
|---|---|---|
| Latest state + `RegulationReading` | `updateApplicationContext(_:)` | Coalesced "freshest value wins"; overwrites; delivered when the watch next runs. We only ever care about the newest reading. |
| Live stream while a watch session is active | `sendMessage(_:replyHandler:)` | Immediate, low-latency; requires both reachable (`isReachable`). |
| Confirmed alert / discrete cue | `transferUserInfo(_:)` | Queued FIFO; each one arrives even if delivered late. |

### 4.3 Background runtime for haptics — the central tradeoff

A watch app can only play haptics **while it has runtime**. Two ways to get it
while the wrist is down:

- **`WKExtendedRuntimeSession` with the `mindfulness` background mode** — a
  bounded "self-care" session (order ~1 hour) that is *allowed to play haptics*
  in the background. This is the right primitive for a deliberate, user-started
  **"regulation session"** and matches the wellness framing. It is **not**
  indefinite always-on.
- **`HKWorkoutSession` (`workout-processing`)** — indefinite background runtime,
  and it streams HR for free (synergy with §11 W3, watch-as-sensor). But it
  carries "you are recording a workout" semantics and a battery cost.

**Recommendation:** ship the dedicated, user-started mindfulness session first;
treat always-on (workout session) as part of the sensor stretch. Whether a
mindfulness session reliably plays haptics wrist-down for its full duration is
the **single highest-risk item** and must be validated on a real device before
any "always-on" promise (mirrors design doc §14 row 1 for background BLE).

### 4.4 Haptic APIs

- **`WKInterfaceDevice.current().play(_:)`** with `WKHapticType` (`.click`,
  `.notification`, `.directionUp/Down`, `.success`, `.stop`, …) — simple,
  system-defined cues. Used for **discrete** state-change taps.
- **CoreHaptics — `CHHapticEngine`** (Apple Watch Series 4+ / watchOS 6+) —
  custom `CHHapticEvent`s (`.hapticContinuous` / `.hapticTransient`) with
  `.hapticIntensity` / `.hapticSharpness` parameters and `CHHapticParameterCurve`
  envelopes. This builds the **paced-breath pattern** (a slow swell on the
  in-breath, a gentle decay on the out-breath) and scales intensity from the
  arousal index. Devices without CoreHaptics degrade to `WKInterfaceDevice`
  pulses on a timer.

### 4.5 Build & test reality

- The Swift/watch half needs **macOS + Xcode + a paired Apple Watch + a paid
  Apple Developer account**. The simulator cannot render real haptics and has no
  real HR. (Same constraint the Live Activity's remaining work carries.)
- The **entire managed half is buildable and unit-testable now**, on Linux/CI,
  with no watch present — exactly how `LiveActivityPublisher` (Phase 8) and the
  Regulation Field (Phase 10) were built before their device-only parts.

---

## 5. The managed / native boundary

```
 Pipeline (Mobile)                         managed, fully unit-tested
   │ ReadingUpdated / StateChanged / AlertFired
   ▼
 WatchHapticPublisher (Mobile)             managed — opt-in gate, Confidence /
   │ UpdateState / SendCue                 contact / pause gating, live stream
   │                                       throttled ≤ 1 Hz, state changes bypass,
   │                                       fire-and-forget so a slow watch can
   │                                       never stall the BLE pipeline
   ▼
 IWatchSession  (Mobile)                    managed seam
   │
   ├─ (tests)  RecordingWatchSession        managed fake — records, no-ops
   └─ (iOS)    WatchSession.cs              managed — WCSession (WatchConnectivity)
                                            │
   ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ │─ ─ native boundary ─ ─ ─ ─ ─ ─
                                            ▼
 MeltdownMonitor Watch App (Swift, watchOS target)
   • WCSessionDelegate            ← state / reading / cues
   • WKExtendedRuntimeSession(.mindfulness)   background runtime for haptics
   • CHHapticEngine               paced-breath pattern + proportional intensity
   • WKInterfaceDevice.play       discrete state cues
   • session screen               breathing animation; state colour over the wire
```

The split is identical in spirit to `docs/live-activity.md`: the managed side
owns *when* and *how strongly* to cue (the testable part); the watch owns the
physical rendering. The seam (`IWatchSession`) means `WatchHapticPublisher` and
its tests never reference WatchConnectivity or UIKit.

---

## 6. Managed types & policy (`MeltdownMonitor.Mobile`)

Sketches, mirroring `ILiveActivityController` / `LiveActivityContent`:

```csharp
// Coalesced "latest" snapshot pushed to the watch (applicationContext).
public readonly record struct WatchHapticState(
    DetectorState State,
    string StateLabel,     // StateColors.LabelFor(...)
    string ColorHex,       // StateColors.HexFor(...) — single-sourced palette
    double RegulationIndex, // RegulationReading.Index, [-1, 1]
    double Confidence,      // RegulationReading.Confidence, [0, 1]
    bool IsPaused);

// Discrete, must-arrive cues (transferUserInfo / sendMessage).
public enum WatchHapticCue { EscalatedToWarning, EscalatedToAlerting, Recovered }

// Platform-neutral seam; iOS implements over WCSession, tests get a fake.
public interface IWatchSession
{
    bool IsReachable { get; }                       // live sendMessage possible
    Task UpdateStateAsync(WatchHapticState state);  // coalesced applicationContext
    Task SendCueAsync(WatchHapticCue cue);          // discrete event
}
```

`WatchHapticPublisher` (structural copy of `LiveActivityPublisher`):

- Subscribes to `ReadingUpdated`, `StateChanged`, `AlertFired`.
- **Opt-in gate**: does nothing unless `MobileSettings.EnableWatchHaptics`; if
  switched off while running, sends a final "silent" state so the watch stops.
- **Silence gates**: `Confidence` below floor, `LatestContact` not making
  contact, or paused → push a state that the watch renders as silent.
- **Throttle** live `ReadingUpdated` pushes to ≤ 1 Hz (samples already arrive
  ~every 5 s, but coalescing keeps WatchConnectivity well inside budget);
  `StateChanged` **bypasses** the throttle.
- **Fire-and-forget** (`_ = action()` wrapped in try/catch) — a slow or
  unreachable watch must never stall the BLE callback path. (Same discipline as
  `LiveActivityPublisher.Invoke`.)
- A pure helper, `WatchHapticPlanner`, derives the cue/intensity from
  `(RegulationReading, DetectorState)` — see §7 — so the mapping is unit-tested
  without a watch (the way `RegulationFieldAnimator` is tested without a render
  surface).

---

## 7. Haptic mapping — the felt Regulation Field

The planner is pure and table-driven. Thresholds below are illustrative and
should track `DetectionThresholds` rather than be hard-coded twice.

| Condition | Continuous haptic | Discrete cue |
|---|---|---|
| `Confidence` < floor (baseline cold) | **silent** | none |
| Paused, or no skin contact | **silent** | none |
| `Index` ≤ 0 (at/below baseline, calm) | silent by default; optional faint slow "anchor" breath if the user opts in | none |
| 0 < `Index` < warn | gentle paced breath ~6 brpm, **low** intensity | — |
| warn ≤ `Index` < high (≈ Warning) | **same calming cadence**, **medium** intensity | soft single tap on entering Warning |
| `Index` ≥ high (≈ Alerting) | same calming cadence, intensity at the **ceiling** (still soft), slightly longer out-breath emphasis | soft double tap on entering Alerting |
| Return toward baseline (→ Cooldown / Watching) | ramp down to silent | gentle "release" / `.success` cue |

Invariants the tests pin:

- Cadence is **monotonic in the calming direction** — higher arousal never
  shortens the breath period.
- Intensity is **clamped** to the user's ceiling and to 0 below the confidence
  floor.
- `WatchHapticMode` filters the output: `PacedBreath` drops discrete cues,
  `StateCues` drops the continuous pacer, `Both` keeps both.

---

## 8. The watch app (Swift, new target)

A small SwiftUI watchOS app, `MeltdownMonitor Watch App`:

- **`WCSessionDelegate`** receives `WatchHapticState` (applicationContext) and
  `WatchHapticCue` (userInfo); keeps the latest state.
- **Session control**: a single primary button — "Start regulation session" /
  "Stop" — that opens/closes a `WKExtendedRuntimeSession` (`mindfulness`). While
  active, the app has runtime to play haptics with the wrist down (§4.3).
- **`CHHapticEngine`** renders the paced-breath pattern and proportional
  intensity from the incoming `RegulationIndex`; `WKInterfaceDevice.play(_:)`
  fires the discrete cues. No-CoreHaptics watches fall back to timed pulses.
- **Minimal screen**: the state colour (decoded from `ColorHex` so the palette
  stays single-sourced in `StateColors`) and a breathing circle synced to the
  haptic, so the visual and the felt cue agree. VoiceOver label on the state.

---

## 9. Settings (`MobileSettings`)

New fields, parallel to `EnableLiveActivity`, all conservative by default:

| Field | Default | Meaning |
|---|---|---|
| `EnableWatchHaptics` | `false` | Master opt-in (somatic output is off until asked for). |
| `WatchHapticMode` | `Both` | `PacedBreath` \| `StateCues` \| `Both` — respects individual sensory profiles. |
| `WatchHapticIntensity` | gentle | Ceiling for all cues (low / medium / firm). |
| `WatchPacedBreathRate` | ~6 brpm | Resonance-frequency target for the breath pacer. |

Surfaced under Settings ▸ (a new) **Haptics** section, gated behind the watch
being paired.

---

## 10. Files

| File | Target | Compiled by |
|---|---|---|
| `MeltdownMonitor.Mobile/Services/IWatchSession.cs` (+ `WatchHapticState`, `WatchHapticCue`) | Mobile | dotnet |
| `MeltdownMonitor.Mobile/Services/WatchHapticPublisher.cs` | Mobile | dotnet |
| `MeltdownMonitor.Mobile/Services/WatchHapticPlanner.cs` | Mobile | dotnet |
| `MeltdownMonitor.iOS/Services/WatchSession.cs` (WCSession) | iOS head | dotnet |
| `MeltdownMonitor.Tests/WatchHapticPublisherTests.cs`, `WatchHapticPlannerTests.cs` | Tests | dotnet |
| `MeltdownMonitor.WatchApp/…` (SwiftUI app, `WCSessionDelegate`, haptic engine) | **watchOS** | **Xcode** |

> As with the widget extension, **the .NET build does not compile any `.swift`
> file**. The watch target is Xcode-managed and added on a Mac (§12). Until it
> exists, the managed publisher runs against a no-op `IWatchSession` and the rest
> of the app is unaffected.

---

## 11. Phasing

Each phase ends at a buildable, demoable state, matching the doc's house style.

### Phase W1 — managed scaffold + tests (no Mac required)

- `IWatchSession` seam + `WatchHapticState` / `WatchHapticCue`.
- `WatchHapticPublisher` (opt-in, gating, throttle, state-bypass, fire-and-forget)
  + pure `WatchHapticPlanner` (§7).
- `MobileSettings` fields (§9).
- `WatchHapticPublisherTests` / `WatchHapticPlannerTests`, mirroring
  `LiveActivityPublisherTests`: opt-in gate + runtime disable, confidence floor →
  silent, no-contact / paused → silent, ≤ 1 Hz throttle, state-change bypass,
  cadence monotonicity, intensity clamp, recovery cue, mode filtering, teardown.
- Wire the publisher into `IosCompositionRoot` next to `_liveActivity`, with a
  no-op `RecordingWatchSession` until the iOS `WCSession` impl lands — graceful
  degrade, exactly like the Live Activity controller's `dlsym` no-op.

**Exit:** `dotnet test` green on Linux/CI; app behaviour unchanged when the flag
is off and no watch is present.

### Phase W2 — WatchConnectivity + watch app (Mac + paired Watch)

- `MeltdownMonitor.iOS/Services/WatchSession.cs` over `WCSession`
  (activate, `UpdateApplicationContext`, `SendMessage` when reachable,
  `TransferUserInfo`). Fallback noted below.
- New watchOS Swift target (§8): delegate, `WKExtendedRuntimeSession`,
  `CHHapticEngine`, session screen.
- Watch `Info.plist` (`WKApplication`, `WKCompanionAppBundleIdentifier`,
  `WKBackgroundModes` = `mindfulness`); App Group if shared defaults are wanted.

**Exit (on-device):** starting a regulation session and inducing a HRV drop
produces a gentle, correctly-scaled wrist cue within a second or two; latency and
battery are acceptable; wrist-down haptics fire for the session's duration.

### Phase W3 — watch as HR source (stretch)

- `HKWorkoutSession` + `HKLiveWorkoutBuilder` stream beat-to-beat HR on the
  watch → phone via `sendMessage` → adapt to a `WatchBeatSource : IBeatSource`
  so the Pipeline runs **strap-free**.
- Document the fidelity caveat: Apple Watch exposes HR and HRV-SDNN, not
  H10-grade beat-to-beat RR, so short-window RMSSD degrades — the same
  ECG-vs-optical tradeoff the README already draws for H10 vs Verity Sense.

---

## 12. One-time Xcode wiring (Mac required)

Like the widget extension, the watch app is a native target added in Xcode:

1. Build the iOS head once and open the generated Xcode project (or maintain a
   thin companion `.xcodeproj`); add a **Watch App** target
   ("Watch App for iOS App"), bundle id
   `com.thethreethousands.meltdownmonitor.watchkitapp` (must be prefixed by the
   iOS app id `com.thethreethousands.meltdownmonitor`).
2. Add the Swift files from `MeltdownMonitor.WatchApp/` to the watch target.
3. Set the watch `Info.plist` keys (§11 W2) and embed the watch app in the iOS
   app under *Build Phases ▸ Embed Watch Content*.
4. Confirm `WCSession` activates on both sides and that an extended runtime
   session can play haptics wrist-down (the §4.3 risk).

No managed rebuild is required when the watch target changes — the phone reaches
it through `WCSession`, and `WatchSession.cs` degrades to a no-op when no watch
is paired/reachable.

---

## 13. Tests

Managed only (Phase W1), in the existing `MeltdownMonitor.Tests` project so they
run anywhere — the suite stays Core+Mobile and BLE-free, like
`LiveActivityPublisherTests`:

- **Publisher**: opt-in gate, runtime disable mid-run, confidence-floor silence,
  no-contact/paused silence, ≤ 1 Hz throttle, state-change bypass, fire-and-forget
  isolation (a throwing `IWatchSession` never propagates), teardown.
- **Planner**: cadence monotonicity (arousal never speeds the breath), intensity
  clamp to the ceiling and to 0 below the floor, zone boundaries, mode filtering
  (`PacedBreath` / `StateCues` / `Both`), recovery cue on de-escalation.

The Swift layer is validated on-device (real haptics + real HR), like the Live
Activity's SwiftUI presentation.

---

## 14. Risks

| Risk | Severity | Mitigation |
|---|---|---|
| Extended runtime sessions don't reliably play haptics wrist-down for their full duration | **High** | Validate on a real device before any "always-on" claim; ship the user-started session model first; fall back to foreground-only if needed. |
| `WCSession` managed bindings incomplete on `net10.0-ios` | Medium | Keep `IWatchSession` as the seam; if bindings fall short, add an `@_cdecl` Swift bridge on the phone exactly like `LiveActivityController` and resolve via `dlsym`. |
| Vibration is aversive for some autistic users | Medium | Off by default; intensity ceiling; `WatchHapticMode`; one-gesture stop on the watch. |
| Apple Watch HR fidelity too low for RMSSD (W3) | Medium | Document the optical caveat (as with Verity Sense); keep Polar the recommended source. |
| Misread as a medical/therapeutic device | Medium | Wellness-only copy; no "treat/therapy" language; keep the README disclaimer posture. |
| Battery drain from CoreHaptics + runtime session | Low–Med | Bounded sessions; coalesced ≤ 1 Hz updates; measure on-device. |

---

## 15. Open questions

1. **Runtime model**: dedicated user-started mindfulness session (recommended) vs
   always-on workout session — decide after the §4.3 device test.
2. **Threshold sharing**: surface `DetectionThresholds` to the planner so warn/high
   zones track the detector rather than being a second source of truth.
3. **Default mode**: ship `Both`, or start with `PacedBreath` only and add discrete
   cues once the breath pacer is validated as non-startling?
4. **`VariabilityQuality` mapping**: worth driving haptic *sharpness/texture* from
   it later, or keep intensity-from-`Index` only for v1.1?
5. **Watch-as-sensor (W3)**: in scope for this milestone, or a separate one once
   the actuator path ships?
