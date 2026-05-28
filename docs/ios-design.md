# MeltdownMonitor for iOS — Design Document

Status: **Draft / Pre-implementation**
Author: design pass, May 2026
Scope: cross-platform .NET port of MeltdownMonitor targeting iPhone (iOS 17+),
reimagined for a mobile context rather than a literal port of the Windows tray app.

---

## 1. Goals and non-goals

### Goals

- Bring the same dysregulation-detection science (HRV pipeline, EWMA baseline,
  state machine) to an always-with-you device.
- Reuse `MeltdownMonitor.Core` verbatim — no second copy of the HRV math.
- Reimagine the UX for the phone: live chart as the primary surface, push-style
  alerts, HealthKit interop, eventual Apple Watch companion.
- Stay within Apple's policies for non-medical wellness apps (the existing
  disclaimer language already aligns).

### Non-goals (v1)

- WatchOS app — design for it, don't ship it in v1.
- Cloud sync — the desktop is local-only and so is the phone in v1.
- A literal tray-icon equivalent — there isn't one on iOS; alerts are the
  background-state UI.
- Android — keep the framework choice (Avalonia) Android-friendly but do not
  ship a second platform yet.

---

## 2. Framework choice

**Avalonia 11.x targeting `net8.0-ios`** (matched to whatever LTS the rest of
the solution is on at port time — Core currently sits on `net10.0`; iOS
workload availability for that target needs to be re-checked before the
project file is created).

Reasoning:
- Avalonia code-driven UI is the closest spiritual match to the existing Dear
  ImGui codebase — no XAML, no storyboards, controls composed in C#.
- Direct project reference to `MeltdownMonitor.Core` is supported with no
  changes to Core (it's already pure managed code, no `[SupportedOSPlatform]`
  attributes).
- Avalonia 11 ships an iOS backend that wraps `UIKit` and can host Skia for
  the live HRV charts at 60 fps.
- Same project layout works later for Android with a sibling head project,
  preserving option value.

Trade-offs versus alternatives considered:
- **.NET MAUI**: more mature on iOS but its XAML-first idiom would force more
  ceremony, and MAUI's `Maui.Graphics`/chart story is weaker than Avalonia +
  Skia for the live sparkline we need.
- **Swift / SwiftUI**: best UX, native HealthKit/WidgetKit/WatchKit, but
  requires a complete port of Core (HRV math, FFT, state machine, repository)
  to Swift — a second source of truth for clinical-adjacent code is a
  permanent maintenance liability.

---

## 3. Solution layout (after the port)

```
MeltdownMonitor.sln
├── MeltdownMonitor.Core                  # net10.0 — unchanged
├── MeltdownMonitor.Ble.Windows           # unchanged
├── MeltdownMonitor.App                   # unchanged (Windows tray)
├── MeltdownMonitor.Tests                 # unchanged
│
├── MeltdownMonitor.Ble.Apple             # NEW — net8.0-ios (and -maccatalyst)
│                                         # CoreBluetooth wrapper implementing IBeatSource
├── MeltdownMonitor.Mobile                # NEW — net8.0 platform-neutral
│                                         # Avalonia ViewModels, view logic, chart models,
│                                         # alert policy, settings abstraction
└── MeltdownMonitor.iOS                   # NEW — net8.0-ios head project
                                          # Info.plist, entitlements, AppDelegate,
                                          # HealthKit bridge, notification bridge,
                                          # Avalonia bootstrap
```

The split between `MeltdownMonitor.Mobile` (platform-neutral) and
`MeltdownMonitor.iOS` (head) is the same pattern the Windows side uses, and it
keeps the door open for an Android head project later without restructuring.

---

## 4. iOS-specific constraints — read before designing UI

These are the things that drive real architectural choices on iOS. Each one
has either changed how a Windows feature maps to mobile, or constrained what
v1 can promise.

### 4.1 Background BLE

iOS allows a backgrounded app to keep its BLE central session alive **only**
if it declares the `bluetooth-central` background mode in `Info.plist`
(`UIBackgroundModes`). Even then:

- Scanning in the background is *filtered* — you must specify the service
  UUID (`0x180D` Heart Rate Service) when calling `scanForPeripherals`. Wildcard
  scans return no results once the app is suspended.
- Notifications from already-connected peripherals continue to wake the app
  briefly. This is the path we depend on for continuous monitoring: connect
  while foregrounded, then survive in the background by handling
  `didUpdateValueFor` callbacks.
- State preservation/restoration (`CBCentralManagerOptionRestoreIdentifierKey`)
  is required to reconnect if iOS suspends and re-launches the app. This is a
  meaningful chunk of work — see §7.
- If the device disconnects while the app is fully suspended, reconnect
  requires the app to be relaunched in the background by a connection event,
  which only works if state restoration is wired up. Otherwise the user has
  to open the app again.

**Design consequence:** the iOS app cannot promise the same "set it and
forget it" experience the Windows tray app does. We surface a clear
"connected / disconnected / reconnecting" state and prompt the user when
reconnection requires foreground.

### 4.2 Notifications

`UNUserNotificationCenter` replaces Windows toast + tray colour. Two
categories:

- **Alert** (Warning, Alerting): time-sensitive notification with optional
  sound (a soft chime, not a default alert tone). Requires the user to grant
  notification permission on first run.
- **Status** (silent, badge-only): for the ambient "I am still monitoring"
  signal that the tray icon provides on Windows. iOS does not allow a
  persistent foreground icon, so we use a subtle badge plus an optional
  Live Activity (see §4.5).

### 4.3 HealthKit integration

A meaningful win over the desktop version. With user consent:

- **Read** `HKQuantityTypeIdentifier.heartRate` and `heartRateVariabilitySDNN`
  so the app can warm the baseline from historical data instead of starting
  from zero on first install. This is a substantial UX improvement — no more
  10-minute calibration on day one.
- **Write** the app's HR samples and a custom-categorised "dysregulation
  episode" workout-like event to HealthKit, so the user owns their data in a
  portable, system-managed store.

Entitlements required: `com.apple.developer.healthkit`,
`com.apple.developer.healthkit.access`. Info.plist usage strings:
`NSHealthShareUsageDescription`, `NSHealthUpdateUsageDescription`. App Review
will ask why — the answer is honest: historical HR seeds the personal
baseline, and recording episodes lets the user share them with a clinician.

### 4.4 App Store review — wellness, not medical

The existing disclaimer in `README.md` is the right posture. For the iOS
listing:

- Category: **Health & Fitness**.
- App description must not claim to diagnose, treat, or manage any condition.
  Language like "self-awareness", "tracks patterns", "informational only" is
  fine.
- Avoid words like "detect" in marketing copy where it implies clinical
  detection. In-app the state machine can still be called a "detector"
  internally.
- Privacy nutrition label: declare HealthKit data, no third-party sharing,
  no tracking.
- Include the same disclaimer at first launch with a one-time
  acknowledgement before HealthKit access is requested.

### 4.5 Live Activity (iOS 16.1+)

The closest thing to the Windows tray icon is a **Live Activity** on the
Lock Screen and Dynamic Island showing current state colour, HR, and a
RMSSD-vs-baseline mini sparkline. Implemented via ActivityKit and a
WidgetExtension. **Treat this as v1.1, not v1** — it's the right
long-term answer but ships behind first-run monitoring.

### 4.6 Background audio for chimes

If the user enables the alert chime, playing a sound from a background BLE
callback requires the `audio` background mode and an `AVAudioSession`
configured with the `playback` category and `mixWithOthers` option (so we
don't stomp on music). Local notifications can play short sounds without
this, but anything longer than ~5 seconds needs the audio mode.

### 4.7 No SQLite WAL on iOS sandbox

`Microsoft.Data.Sqlite` works on iOS, but the default WAL mode interacts
poorly with iOS's data-protection encryption when the device is locked.
We'll force `journal_mode=TRUNCATE` and `PRAGMA fullfsync=ON` on iOS, and
mark the DB file `NSFileProtectionCompleteUntilFirstUserAuthentication`
so it's accessible to background BLE callbacks.

---

## 5. Mapping desktop concepts → mobile

| Desktop concept | iOS equivalent |
|---|---|
| Tray icon colour | App icon badge + Live Activity colour (v1.1) + in-app state pill |
| Status window (ImGui) | Main screen: live chart, current state pill, HR readout, "connect" button |
| Right-click tray menu | Tab bar / settings sheet |
| Windows toast | UNUserNotification with sound (opt-in) |
| WAV chime | AVAudioPlayer with the same default chime asset; background-audio mode |
| Annotation dialog | Bottom sheet with the same four labels (Calm / Activated / Overwhelmed / Recovering) |
| Pause 1 hour | Same — exposed in settings, also a long-press on the state pill |
| %APPDATA% settings file | `NSUserDefaults` via `ktsu.AppDataStorage` if it ports cleanly, else a thin iOS settings store |
| `data.db` in AppData | App sandbox `Library/Application Support/MeltdownMonitor/data.db` with file protection |
| Single-instance mutex | Not applicable — iOS guarantees single instance |

---

## 6. UX — mobile-first, not a desktop port

Three primary surfaces:

**1. Now screen (default tab).** Top half: a live RMSSD-vs-baseline chart
(Skia-rendered, ~60 s visible window, baseline drawn as a horizontal band).
Below it: current state pill (Idle / Watching / Warning / Alerting /
Cooldown / Paused) with a colour matching the desktop tray palette, current
HR, and time since last state change. Bottom: a single primary button —
"Connect device" / "Disconnect" / "Reconnecting…" depending on state.

**2. History tab.** Daily timeline of state changes, alerts, and annotations.
Tappable rows expand to show the metrics at that moment. This is what the
desktop currently lacks and is the biggest UX win of going mobile.

**3. Settings sheet.** Device type (Auto / H10 / Verity Sense), thresholds
(advanced disclosure — same defaults), chime on/off, notification
permission, HealthKit permission, pause for 1h, export DB (share-sheet),
disclaimer re-show.

Charts: **ScottPlot.Avalonia** or a hand-rolled Skia control. Decision
deferred to implementation — ScottPlot if its Avalonia iOS support is
verified, otherwise direct Skia (we already need Skia for Avalonia anyway).

Accessibility: VoiceOver labels on the state pill and the HR readout; honor
Dynamic Type for all text; avoid colour-only state encoding (the pill always
shows the state name as text, not just a colour swatch).

---

## 7. BLE — `MeltdownMonitor.Ble.Apple`

A new project mirroring `MeltdownMonitor.Ble.Windows`, exposing the same
`IBeatSource` contract from Core so nothing downstream changes.

Responsibilities:
- Wrap `CBCentralManager` and `CBPeripheral` (via the Xamarin.iOS bindings
  that ship with the `net8.0-ios` workload).
- Implement state restoration: pass
  `CBCentralManagerOptionRestoreIdentifierKey` at init, persist the connected
  peripheral identifier in user defaults, and handle
  `willRestoreState` to resume after a background relaunch.
- Reuse `HrMeasurementParser` from Core verbatim — the GATT bytes are the
  same on every platform.
- Reuse `RrArtifactFilter` from Core verbatim.
- Reconnect with the same exponential backoff schedule as Windows.

Open question logged for implementation: whether to expose Polar's
proprietary BLE service for raw ECG (H10 supports this and could improve
artifact rejection) — defer to v1.1.

---

## 8. HealthKit bridge

A small interface in `MeltdownMonitor.Mobile`:

```
public interface IHealthStore
{
    Task<bool> RequestAuthorizationAsync();
    IAsyncEnumerable<HrSample> ReadRecentHeartRateAsync(TimeSpan lookback);
    Task WriteHrSampleAsync(HrSample sample);
    Task WriteEpisodeAsync(EpisodeRecord episode);
}
```

Implemented in `MeltdownMonitor.iOS` against the native HealthKit API.
The Avalonia head can no-op it on desktop targets if we ever bring this
project back to Windows / Linux for testing.

Baseline warm-start: on first install, if HealthKit auth is granted, pull
the last 24 hours of HR samples (sampled at >=1 Hz) and feed them through
the existing `BaselineHrvTracker` so the user lands in `Watching` state
immediately instead of `Idle`.

---

## 9. Permissions and Info.plist keys

| Key | Reason |
|---|---|
| `NSBluetoothAlwaysUsageDescription` | Required for any BLE access |
| `NSBluetoothPeripheralUsageDescription` | Belt-and-braces, older iOS versions |
| `NSHealthShareUsageDescription` | Reading HR / HRV from HealthKit for baseline warm-start |
| `NSHealthUpdateUsageDescription` | Writing episode records back to HealthKit |
| `UIBackgroundModes` | `bluetooth-central`, `audio` (chime), `processing` (periodic baseline housekeeping) |
| `NSUserNotificationsUsageDescription` | Not strictly a key — handled by `UNUserNotificationCenter.requestAuthorization` |

Entitlements file:
- `com.apple.developer.healthkit` (boolean)
- `com.apple.developer.healthkit.access` (array — empty in v1; "health-records" is not used)
- `aps-environment` (development/production for push, only if remote notifications are added later — not in v1)

---

## 10. Persistence

Same SQLite schema as desktop — that's the whole point of reusing Core's
`MeltdownRepository`. Storage location moves from
`%APPDATA%\MeltdownMonitor\data.db` to
`$(NSApplicationSupportDirectory)/MeltdownMonitor/data.db`.

Three iOS-specific adjustments inside the existing repository class (or via
a constructor option to avoid touching Core for platform reasons):
1. Force `journal_mode=TRUNCATE` (see §4.7).
2. Set `NSFileProtectionCompleteUntilFirstUserAuthentication` on the file
   so background BLE callbacks can still write to it.
3. Implement an "export" path that copies the DB into the share-sheet's
   temp area — iOS doesn't let users browse the sandbox directly.

---

## 11. Build and distribution

- Build host: **macOS required** (Xcode 15+, .NET 8 iOS workload installed
  via `dotnet workload install ios`). The current Linux container cannot
  build the iOS head project — this is a constraint to surface clearly in the
  PR description and in onboarding docs.
- Local dev loop: `dotnet build -t:Run -f net8.0-ios` against the simulator;
  device builds require a paid Apple Developer account and provisioning
  profile.
- Signing: managed by Xcode automatic signing during development; manual
  signing certs for TestFlight builds.
- Distribution path for v1: **TestFlight only**, internal testers. App Store
  submission gated on (a) HealthKit review responses being drafted, (b)
  privacy nutrition label filled, (c) the wellness-not-medical disclaimer
  shown at first launch.

---

## 12. File-by-file plan

Files listed in implementation order. Each phase ends at a buildable,
demoable state.

### Phase 1 — scaffold (no behaviour, just compiles)

- `MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj` — net8.0, Avalonia
  package refs, project ref to Core.
- `MeltdownMonitor.Mobile/App.axaml{,.cs}` — Avalonia app, theme.
- `MeltdownMonitor.Mobile/Views/NowView.axaml{,.cs}` — placeholder.
- `MeltdownMonitor.Mobile/Views/HistoryView.axaml{,.cs}` — placeholder.
- `MeltdownMonitor.Mobile/Views/SettingsView.axaml{,.cs}` — placeholder.
- `MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs` — exposes
  `IObservable<DetectorState>` and `IObservable<HrvSample>`, fed by a
  to-be-injected pipeline.
- `MeltdownMonitor.Mobile/Services/IHealthStore.cs` — interface only.
- `MeltdownMonitor.Mobile/Services/INotificationDispatcher.cs` — interface only.
- `MeltdownMonitor.iOS/MeltdownMonitor.iOS.csproj` — net8.0-ios, project refs
  to Mobile, Core, Ble.Apple.
- `MeltdownMonitor.iOS/AppDelegate.cs` — Avalonia + UIKit bootstrap.
- `MeltdownMonitor.iOS/Info.plist` — all keys from §9, placeholder usage
  strings.
- `MeltdownMonitor.iOS/Entitlements.plist` — see §9.
- `MeltdownMonitor.Ble.Apple/MeltdownMonitor.Ble.Apple.csproj` — net8.0-ios,
  project ref to Core.
- `MeltdownMonitor.Ble.Apple/PolarHrSource.cs` — `IBeatSource` skeleton
  raising `NotImplementedException`.
- `MeltdownMonitor.sln` — add the three new projects, exclude from Windows
  build configurations.

### Phase 2 — BLE end-to-end

- Flesh out `MeltdownMonitor.Ble.Apple/PolarHrSource.cs`:
  CBCentralManagerDelegate, scan for `0x180D`, connect, subscribe to
  `0x2A37`, parse with shared `HrMeasurementParser`, filter with shared
  `RrArtifactFilter`, surface as `IAsyncEnumerable<Beat>`.
- `MeltdownMonitor.Ble.Apple/BleStateRestoration.cs` — `willRestoreState`
  handler and identifier persistence.
- Wire the source into a `Pipeline` mirror in `MeltdownMonitor.Mobile`
  (port of `MeltdownMonitor.App/Pipeline.cs` shorn of WinForms/ImGui refs).

### Phase 3 — UI

- Real `NowView` with Skia-backed chart control.
- `HistoryView` reading from `MeltdownRepository`.
- `SettingsView` bound to a new `AppSettings` class mirroring desktop's.
- State pill colour palette matching the desktop tray (`Idle` grey,
  `Watching` blue, `Warning` amber, `Alerting` red, `Cooldown` violet,
  `Paused` muted).

### Phase 4 — alerts and background

- `MeltdownMonitor.iOS/Services/NotificationDispatcher.cs` —
  `UNUserNotificationCenter` implementation.
- Background-mode wiring: `audio` session for the optional chime,
  `bluetooth-central` confirmed working with the simulator's BLE shim and
  on-device with a real H10.
- First-run disclaimer screen with HealthKit ask deferred until after
  acknowledgement.

### Phase 5 — HealthKit warm-start

- `MeltdownMonitor.iOS/Services/HealthKitStore.cs` implementing
  `IHealthStore`.
- Baseline warm-start path in the pipeline that pulls the last 24 h of HR
  samples into `BaselineHrvTracker` before live BLE data starts arriving.

### Out of scope for v1 (logged for v1.1+)

- Live Activity / Dynamic Island.
- WatchOS companion (would benefit from native Swift; revisit then).
- iCloud sync.
- Polar proprietary BLE service for raw ECG access.

---

## 13. Open questions to resolve before coding

1. **Core target framework.** Core is on `net10.0`; the iOS workload may
   not support `net10.0-ios` yet. If not, either downgrade Core to `net8.0`
   (it has zero net10-specific code that I can see — needs verification) or
   bring the Mobile/iOS heads up to whatever `-ios` flavour `net10` ships,
   if available.
2. **AppDataStorage portability.** `ktsu.AppDataStorage` is used on
   desktop; check whether it's net8.0-ios compatible or if we need a thin
   iOS-side replacement that backs to `NSUserDefaults`.
3. **Chart library.** ScottPlot.Avalonia on iOS — verified or not?
   Fallback is hand-rolled Skia, which is fine but more code.
4. **Distribution.** Is there a paid Apple Developer account available
   for signing TestFlight builds, or is this code-only-no-shipping for now?
5. **Test strategy.** `MeltdownMonitor.Tests` currently covers Core and
   runs anywhere. Should we add a Mobile-tests project for ViewModel/state
   plumbing, or rely on Core tests + manual?

---

## 14. Risks

| Risk | Severity | Mitigation |
|---|---|---|
| Background BLE silently dies on real devices | High | Implement state restoration from the start; field-test on a real iPhone before claiming "always-on". |
| App Store rejects for medical claims | Medium | Keep the disclaimer prominent; review marketing copy with the wellness-not-medical rules in mind. |
| HealthKit privacy review delays first submission | Medium | Draft the responses to "why do you need this data" before submitting. |
| Avalonia iOS chart performance | Low–Medium | Decide chart library in Phase 3; benchmark on a real device before locking it in. |
| No Mac in CI | Medium | iOS head build runs only on Mac runners; configure GitHub Actions `macos-latest` with the iOS workload, fail the build matrix loudly if absent. |
| Core on `net10.0` blocking the iOS workload | Medium | Resolve in §13(1) before scaffolding. |

---

## 15. What this document is not

Not a UI mockup pack, not a final selection of chart library, not a commitment
to a ship date. It exists so the next session can scaffold projects against
a shared understanding of the constraints — particularly the background-BLE
and App Store ones, which determine more of the architecture than the
visible UI does.
