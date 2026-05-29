# Live Activity / Dynamic Island (iOS Phase 8)

This document covers the Lock Screen and Dynamic Island Live Activity that
mirrors the in-app state pill (design doc §4.5, Phase 8). It is the closest
mobile analogue to the Windows tray icon: an always-visible status surface
showing the current state colour, heart rate, and RMSSD-vs-baseline balance.

## Why there is Swift here

ActivityKit has no managed binding, and a Live Activity's UI **must** be
declared in a SwiftUI widget extension. This is the one place in the port
where Swift is unavoidable (design doc Phase 8 calls this out). Everything
that *can* be managed, is — the only Swift is the data model, a thin C
bridge, and the SwiftUI views.

## The managed / native boundary

```
 Pipeline (Mobile)                         managed, fully unit-tested
   │ SampleUpdated / StateChanged
   ▼
 LiveActivityPublisher (Mobile)            managed — throttle ≤1 Hz, opt-in,
   │ Start/Update/End                      state changes bypass the throttle
   ▼
 ILiveActivityController                    managed seam
   │
   ├─ (tests)  RecordingController          managed fake
   └─ (iOS)    LiveActivityController  ──┐  managed — dlsym lazy binding
                                          │
   ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─│─ ─ native boundary ─ ─ ─ ─ ─ ─
                                          ▼
 LiveActivityBridge.swift   @_cdecl("mm_live_activity_start" / _update / _end)
   │ Activity.request / update / end
   ▼
 ActivityKit  ──►  MeltdownLiveActivityWidget (SwiftUI, widget extension)
```

- **`MeltdownMonitor.Mobile`** owns all the logic worth testing: when to
  start/stop, the ≤1 Hz throttle, the state-change bypass, the opt-in flag
  (`MobileSettings.EnableLiveActivity`), and the content derivation
  (`LiveActivityContent`, including the `#RRGGBB` colour from
  `StateColors.HexFor` so the palette has a single source of truth).
- **`MeltdownMonitor.iOS/Services/LiveActivityController.cs`** calls the Swift
  bridge. The entry points are resolved **lazily at runtime with `dlsym`**, not
  via a static `[DllImport("__Internal")]`: a static import would make the app
  fail at *link* time while the bridge is absent (`ld: Undefined symbols …
  _mm_live_activity_start`), whereas `dlsym` lets the binary link cleanly,
  no-ops when the bridge isn't linked, and lights up automatically once the
  Swift file is added to the app target.
- **Swift** is confined to three small files (see below).

## Files

| File | Target | Compiled by |
|---|---|---|
| `MeltdownMonitor.iOS/LiveActivity/MeltdownActivityAttributes.swift` | app **and** widget | Xcode |
| `MeltdownMonitor.iOS/LiveActivity/LiveActivityBridge.swift` | app | Xcode |
| `MeltdownMonitor.iOS.WidgetExtension/MeltdownLiveActivityWidget.swift` | widget | Xcode |
| `MeltdownMonitor.iOS.WidgetExtension/MeltdownWidgetBundle.swift` | widget | Xcode |
| `MeltdownMonitor.iOS.WidgetExtension/Info.plist` | widget | — |

> **The .NET build does not compile any `.swift` file.** They are not in the
> `.csproj` and the iOS SDK does not glob them. The widget extension is a
> native Xcode-managed target that has to be added on a Mac (below). Until it
> is, the managed controller degrades to a no-op and the rest of the app is
> unaffected.

## One-time Xcode wiring (Mac required)

The .NET iOS head produces an `.app`; a widget extension is a separate native
target that Xcode (not `dotnet`) manages. To enable the Live Activity end to
end:

1. Build the app once (`dotnet build -t:Run -f net10.0-ios`) and open the
   generated Xcode project, **or** maintain a thin companion `.xcodeproj`
   that adds:
   - A **Widget Extension** target (`File ▸ New ▸ Target ▸ Widget Extension`,
     "Include Live Activity" checked). Name it `MeltdownMonitorWidget`,
     bundle id `com.thethreethousands.meltdownmonitor.WidgetExtension`.
   - Replace the generated files with the four under
     `MeltdownMonitor.iOS.WidgetExtension/`.
2. Add `LiveActivityBridge.swift` to the **app** target's compile sources and
   `MeltdownActivityAttributes.swift` to **both** the app and widget targets'
   target membership.
3. Confirm `NSSupportsLiveActivities` is `true` in the app `Info.plist`
   (already set in this repo).
4. Embed the widget extension in the app under
   *Build Phases ▸ Embed App Extensions*.

The `@_cdecl` symbols (`mm_live_activity_start` / `_update` / `_end`) are
exported from the app binary, which is what the controller's `dlsym` lookup
resolves against at runtime — no extra linker flags are needed once the Swift
file is in the app target, and no rebuild of the managed code is required.

## Throttling & lifecycle

- Sample updates are throttled to **≤ 1 Hz** (`LiveActivityPublisher`'s
  `minUpdateInterval`, default 1 s) to stay inside Apple's activity-refresh
  budget.
- **State transitions bypass the throttle** — the Lock Screen colour flips the
  instant the detector state does.
- The activity is **opt-in** (`Settings ▸ Display ▸ Show Live Activity`).
  Turning it off ends the running activity on the next pipeline event.
- On graceful terminate (`IosCompositionRoot.StopAsync`) the activity is
  dismissed so no stale card lingers on the Lock Screen.

## Tests

`MeltdownMonitor.Tests/LiveActivityPublisherTests.cs` covers the managed
behaviour against a fake controller and a controllable clock: first-sample
start, the 1 Hz throttle, the state-change bypass, the opt-in gate (including
runtime disable), paused content, the warming-baseline neutral ratio, and
`StopAsync` teardown. The Swift layer is validated on-device (it needs a real
Lock Screen / Dynamic Island).
