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

| File | Target | Compiled / used by |
|---|---|---|
| `MeltdownMonitor.iOS/LiveActivity/MeltdownActivityAttributes.swift` | bridge framework **and** widget | Xcode |
| `MeltdownMonitor.iOS/LiveActivity/LiveActivityBridge.swift` | bridge framework | Xcode |
| `MeltdownMonitor.iOS.WidgetExtension/MeltdownLiveActivityWidget.swift` | widget | Xcode |
| `MeltdownMonitor.iOS.WidgetExtension/MeltdownWidgetBundle.swift` | widget | Xcode |
| `MeltdownMonitor.iOS.WidgetExtension/Info.plist` | widget | — |
| `MeltdownMonitor.iOS.WidgetExtension/project.yml` | both native targets | XcodeGen |
| `MeltdownMonitor.iOS.WidgetExtension/generate.sh` | — | developer (Mac) |

> **The .NET build does not compile any `.swift` file.** They are not in the
> `.csproj` and the iOS SDK does not glob them. The native targets are defined
> by `project.yml` and built by Xcode on a Mac (below). Until the artifacts are
> built, the managed controller degrades to a no-op and the rest of the app is
> unaffected.

## Reproducible Xcode wiring (Mac required)

The .NET iOS head produces an `.app` via MSBuild and **cannot compile Swift**;
ActivityKit + WidgetKit + SwiftUI must be compiled by Xcode. The two build
systems are bridged declaratively by an **XcodeGen spec**
(`MeltdownMonitor.iOS.WidgetExtension/project.yml`) so the native targets are
version-controlled rather than hand-clicked in Xcode. The generated
`.xcodeproj` and Xcode build outputs are git-ignored — only the spec, the Swift
sources, and the widget `Info.plist` are committed.

The spec defines two Xcode targets:

| Xcode target | Product | Role |
|---|---|---|
| `MeltdownLiveActivityBridge` | dynamic `.framework` | Carries the `@_cdecl mm_live_activity_*` entry points (`LiveActivityBridge.swift`) + the shared `MeltdownActivityAttributes` model. The .NET app **links** it. |
| `MeltdownMonitorWidgetExtension` | `.appex` | The SwiftUI Lock Screen / Dynamic Island presentation. The .NET app **embeds** it under `PlugIns/`. |

### Build it (on a Mac)

```sh
brew install xcodegen                                   # one-time
cd MeltdownMonitor.iOS.WidgetExtension
export DEVELOPMENT_TEAM=ABCDE12345                       # your signing team
./generate.sh --build                                   # generate + xcodebuild
```

`generate.sh --build` writes, into `build/Release-iphoneos/`:
`MeltdownLiveActivityBridge.framework` and `MeltdownMonitorWidgetExtension.appex`
(set `SDK=iphonesimulator` / `CONFIG=Debug` to vary). The `.csproj` then picks
them up automatically on the next `dotnet build -f net10.0-ios`:

- a `<NativeReference Kind="Framework">` links the bridge framework, so the
  `@_cdecl` symbols (`mm_live_activity_start` / `_update` / `_end`) land in the
  app binary where the controller's `dlsym(RTLD_DEFAULT, …)` lookup resolves
  them at runtime;
- an `EmbedLiveActivityWidget` target copies the `.appex` into
  `…/MeltdownMonitor.app/PlugIns/`.

Both are guarded by `Exists(...)` against `$(MMLiveActivityArtifacts)` (default
`MeltdownMonitor.iOS.WidgetExtension/build/Release-iphoneos`). On Linux/CI — or
any box where the Xcode artifacts haven't been built — they don't apply: the
app links cleanly and the managed `LiveActivityController` degrades to a no-op,
exactly the "bridge absent" behaviour described above. No managed rebuild is
needed beyond re-running `dotnet build` after the artifacts exist.

`NSSupportsLiveActivities` is already `true` in the app `Info.plist`, and the
widget extension's bundle id (`com.matthewedmondson.meltdownmonitor.WidgetExtension`)
matches the committed `Info.plist`.

> **Verification is Mac-only.** None of the XcodeGen / xcodebuild / linking /
> embedding steps can be exercised off a Mac with Xcode — the spec and the
> `.csproj` wiring are validated by inspection here and must be confirmed on a
> device (a real Lock Screen / Dynamic Island) before shipping.

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
