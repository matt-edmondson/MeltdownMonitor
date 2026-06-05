# MeltdownMonitor for Android — Design Document

Status: **Draft / Pre-implementation**
Author: design pass, June 2026
Scope: add an Android head to the existing cross-platform .NET app, reusing the
platform-neutral `MeltdownMonitor.Mobile` and `MeltdownMonitor.Core` assemblies
that already ship on iOS. This is a sibling-head project, not a port.

This document is the Android counterpart to `docs/ios-design.md`. Where the iOS
doc was written before any mobile code existed, this one is written after the
mobile layer has shipped, so most of the hard architectural questions are
already answered. The remaining work is Android-specific glue.

---

## 1. Starting position — what is already done

The single biggest fact for Android planning: **the entire platform-neutral
mobile stack already exists and is exercised in production by the iOS head.**

- `MeltdownMonitor.Core` (`net10.0`) — HRV math, detection state machine, EWMA
  baseline, regulation-field calculator, `IBeatSource` and the optional
  capability interfaces, SQLite persistence. Pure managed code, no platform
  attributes. Reused verbatim.
- `MeltdownMonitor.Mobile` (`net10.0`) — Avalonia UI, all view models, the full
  metric chart suite, the Regulation Field control, and the six service seams
  the head implements natively:
  - `INotificationDispatcher`
  - `IHealthStore`
  - `IChimePlayer`
  - `IMobileSettingsStore`
  - `ILiveActivityController`
  - `IDatabaseExporter`
- The composition pattern is settled: a head project sets
  `App.RootViewModelFactory` and `App.Started`, builds the view models on the
  UI thread, then composes and starts the pipeline asynchronously. See
  `MeltdownMonitor.iOS/IosCompositionRoot.cs` for the reference.

So Android does **not** need to design the UI, the view models, the pipeline,
the settings shape, or the service contracts. It needs three things:

1. `MeltdownMonitor.Ble.Android` — an `IBeatSource` over Android BLE.
2. `MeltdownMonitor.Android` — the head: a single-Activity Avalonia host, an
   `AndroidCompositionRoot`, and native implementations of the six interfaces.
3. A foreground service so monitoring survives the screen turning off.

Everything else is reuse.

---

## 2. Goals and non-goals

### Goals

- Bring the shipped mobile experience (Now / History / Metrics / Settings,
  Regulation Field included) to Android phones with no second copy of any UI or
  domain logic.
- Reuse `MeltdownMonitor.Core` and `MeltdownMonitor.Mobile` byte-for-byte.
- Keep continuous monitoring alive in the background using a foreground
  service, which is the Android model and is more reliable than the iOS
  background-BLE model.
- Stay within Google Play policy for non-medical wellness apps, matching the
  disclaimer posture already used on iOS and the desktop.

### Non-goals (v1)

- Wear OS companion — design so it is not foreclosed, do not ship it in v1.
- Cloud sync — local-only, same as every other head.
- Health Connect write-back of episodes beyond a simple session record — read
  for warm-start is the priority, write is fast-follow.
- Tablet-optimized layout — the phone layout should adapt, but no dedicated
  large-screen design in v1.

---

## 3. Framework choice

**Avalonia 12.x targeting `net10.0-android`**, matching the version the Mobile
assembly already references (Avalonia 12.0.4). Avalonia ships an Android
backend that hosts Skia, which is what the Regulation Field and the chart suite
render through, so the visual layer that exists today runs unchanged.

Reasoning:
- The Mobile assembly is already an Avalonia single-view application
  (`ISingleViewApplicationLifetime`). An Android head plugs into the same
  `App` with the same two static seams. No UI rewrite.
- `SkiaSharp` already ships native assets for Android, so the additive-blend
  glow path (`AdditiveSkiaLayer`, `SKBlendMode.Plus`) that the Mobile head uses
  works on Android the same way it does on iOS. The iOS csproj pulls in
  `SkiaSharp.NativeAssets.iOS`. The Android head pulls in the Android-native
  asset package instead.
- A direct project reference from the Android head to `MeltdownMonitor.Core`
  and `MeltdownMonitor.Mobile` is supported with no changes to either, exactly
  as the iOS head does it.

Trade-offs already settled by the iOS effort: .NET MAUI and a native Kotlin
rewrite were both rejected for the same reason a Swift rewrite was rejected, a
second source of truth for clinical-adjacent code is a permanent liability. The
managed-Avalonia choice carries straight over.

---

## 4. Solution layout (after the port)

```
MeltdownMonitor.sln
├── MeltdownMonitor.Core                  # net10.0 — unchanged
├── MeltdownMonitor.Mobile                # net10.0 — unchanged (already shipped)
├── MeltdownMonitor.Ble.Windows           # unchanged
├── MeltdownMonitor.App                   # unchanged (Windows tray)
├── MeltdownMonitor.Ble.Apple             # unchanged (iOS BLE)
├── MeltdownMonitor.iOS                    # unchanged (iOS head)
├── MeltdownMonitor.Tests                  # unchanged (Core + Mobile)
│
├── MeltdownMonitor.Ble.Android            # NEW — net10.0-android
│                                          # Android BLE → IBeatSource (+ battery/contact/device-info)
└── MeltdownMonitor.Android                # NEW — net10.0-android head project
                                           # AndroidManifest, MainActivity, foreground service,
                                           # AndroidCompositionRoot, six native service impls
```

This is the same split the iOS side uses. The Mobile / head boundary the iOS
doc anticipated for "an Android head project later without restructuring" is
exactly what we are filling in.

`MeltdownMonitor.Ble.Android` mirrors `MeltdownMonitor.Ble.Apple`: it references
only `Core`, targets the platform TFM, and is trimmable for release.

---

## 5. Android-specific constraints — read before designing

These drive the real architectural decisions, the same way the background-BLE
and App Store rules drove the iOS design. Most differ meaningfully from iOS.

### 5.1 Background BLE via a foreground service

This is the central Android decision and it is **simpler and more reliable than
iOS**. Android does not relaunch your process from a BLE event the way iOS
state restoration does. Instead you keep the process alive yourself with a
**foreground service** that shows an ongoing notification.

- Start a foreground service of type `connectedDevice` (and/or `health` on
  Android 14+, which fits this app's purpose) when monitoring begins. The
  ongoing notification it requires is not a cost, it is the feature: it doubles
  as the always-on status surface (see 5.5).
- The service holds the `BluetoothGatt` connection. As long as it runs, GATT
  notifications (`onCharacteristicChanged`) keep arriving with the screen off.
- Use `connectGatt(..., autoConnect: true)` so the stack transparently
  reconnects if the sensor drops and returns to range. This is the Android
  analog of iOS state restoration, and it does not require the app to be
  relaunched by the OS.
- **Doze and battery optimization** are the real risk, not the service model.
  On Doze, background work is throttled, but a running foreground service with
  an active connection is largely exempt for connection callbacks. For
  user-confidence we will offer (not silently request) the
  `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` opt-in, with copy explaining why, and
  detect aggressive OEM battery managers (Samsung, Xiaomi, etc.) and link the
  user to the relevant settings if monitoring is being killed.

**Design consequence:** unlike iOS, Android genuinely can offer close to the
"set it and forget it" experience the Windows tray app does, gated only on the
user not force-killing the app and on OEM battery managers behaving. We still
surface connected / disconnected / reconnecting state clearly.

### 5.2 Runtime permissions

Android's permission model is the biggest source of boilerplate. Required:

- `BLUETOOTH_SCAN` and `BLUETOOTH_CONNECT` (runtime, Android 12 / API 31+). The
  scan permission can carry `usesPermissionFlags="neverForLocation"` because we
  only scan for a known Heart Rate Service and do not derive location, which
  lets us avoid the location permission on API 31+.
- `ACCESS_FINE_LOCATION` is required for BLE scanning only on API 30 and below.
  Declare it with `android:maxSdkVersion="30"` so newer devices are not asked.
- `POST_NOTIFICATIONS` (runtime, Android 13 / API 33+) for the alert and
  status notifications.
- `FOREGROUND_SERVICE` plus the typed permission
  `FOREGROUND_SERVICE_CONNECTED_DEVICE` (and `FOREGROUND_SERVICE_HEALTH` if we
  use the health type), API 34+.
- Health Connect read permission for heart rate (see 5.3), requested through
  Health Connect's own permission flow, not a manifest runtime grant.

All runtime requests are sequenced behind the existing first-run disclaimer,
matching the iOS ordering (acknowledge, then ask).

### 5.3 Health Connect, not Google Fit

The Google Fit APIs are deprecated and being shut down through 2025–2026, so
the warm-start data source on Android is **Health Connect**
(`androidx.health.connect`), the platform's successor and the analog of iOS
HealthKit.

- **Read** `HeartRateRecord` for the last 24 hours to warm the EWMA baseline
  through the existing `BaselineHrvTracker`, exactly as `HealthKitStore` feeds
  the iOS pipeline. This removes the cold-start calibration on first run.
- **Write** is fast-follow: Health Connect does not have a direct equivalent of
  HealthKit's custom "episode workout", but an `ExerciseSessionRecord` or a
  series of `HeartRateRecord` samples can stand in. Default off, opt-in, behind
  the same wellness-rules reasoning as iOS.
- Health Connect ships as a system component on Android 14+ and as a Play Store
  app on older versions. The implementation must degrade gracefully when it is
  absent (no warm-start, start cold), the way the pipeline already tolerates a
  null `IHealthStore`.

There is a managed binding (`Xamarin.AndroidX.Health.Connect.Client`). If its
maturity on `net10.0-android` is not adequate, the fallback is a thin JNI
wrapper over the AndroidX client, scoped to just the read path. Decision
deferred to implementation, logged in §11.

### 5.4 Notifications and channels

`INotificationDispatcher` maps to `NotificationManagerCompat` with
notification channels (mandatory since Android 8):

- An **Alert** channel at `IMPORTANCE_HIGH` for hyperarousal alerts, with the
  soft chime. Hypoarousal / shutdown alerts use the same gentle-copy softening
  the iOS dispatcher applies.
- A **Status** channel at `IMPORTANCE_LOW` (silent) for the ongoing
  monitoring notification (5.5) and the cooldown status update.

Channel importance is user-overridable in Android system settings, which is the
correct platform behavior. We set sensible defaults and do not fight the user.

### 5.5 The ongoing notification is the "Live Activity"

On iOS, `ILiveActivityController` drives a Lock Screen / Dynamic Island
activity, and the iOS doc treated it as v1.1 because it is extra surface. On
Android the equivalent already has to exist on day one: the foreground
service's ongoing notification. So `ILiveActivityController` on Android is not
deferred, it is the natural home for the live state.

- Implement `ILiveActivityController` against the foreground-service
  notification: `Start` shows it, `Update` rebuilds it with current state
  color, HR, and the RMSSD-vs-baseline ratio (the same `LiveActivityContent`
  the publisher already produces), `End` stops the service and dismisses it.
- The `LiveActivityPublisher` in Mobile already throttles updates to <= 1 Hz
  and bypasses the throttle on state changes, which is exactly the refresh
  budget an ongoing notification wants. No new throttling logic.
- This means the Windows tray icon's spiritual successor (an always-present,
  color-coded status surface) is fully realizable on Android in v1, more so
  than on iOS.

### 5.6 Background audio for chimes

`IChimePlayer` maps to a short `SoundPool` or `MediaPlayer` playback with audio
focus requested as `AUDIOFOCUS_GAIN_TRANSIENT_MAY_DUCK` and usage
`USAGE_NOTIFICATION`, so the chime ducks the user's music rather than stopping
it (the Android analog of the iOS `mixWithOthers` choice). Playing from the
foreground service while backgrounded is allowed because the service is
running. No special background-audio capability is needed, unlike iOS.

### 5.7 SQLite on the Android sandbox

`Microsoft.Data.Sqlite` works on Android. The iOS data-protection problem that
forced `journal_mode=TRUNCATE` and `fullfsync=ON` does **not** apply the same
way on Android, where file-based encryption keys are available after first
unlock and the foreground service keeps the process warm. The default
`MeltdownRepositoryOptions.Default` (WAL) is appropriate.

- One caveat: if we ever want writes before first unlock (direct boot), that
  needs device-protected storage, which is out of scope for v1 (monitoring
  starts after the user has unlocked and opened the app at least once).
- Database path: `Context.FilesDir`/`meltdownmonitor/data.db`, resolved by the
  Android composition root, the same way `IosCompositionRoot.DatabasePath()`
  resolves the Application Support path. The repository stays platform-neutral
  and just takes the path.

### 5.8 Activity lifecycle and process death

Android can destroy the Activity (configuration changes, memory pressure) while
the service keeps running. The composition must own the pipeline in the
**service / application** scope, not the Activity, so a rotated or recreated
Activity rebinds to the live pipeline rather than restarting it. This differs
from iOS, where the single UIApplication is the natural owner. Concretely the
`AndroidCompositionRoot` holds the pipeline as application-scoped state and the
foreground service is what keeps it alive.

---

## 6. Mapping iOS / desktop concepts → Android

| Concept | iOS | Android |
|---|---|---|
| Always-on status surface | Live Activity (v1.1) + in-app pill | Foreground-service ongoing notification (v1) + in-app pill |
| Background monitoring | `bluetooth-central` background mode + state restoration | Foreground service + `connectGatt(autoConnect:true)` |
| Alert | `UNUserNotificationCenter` time-sensitive | `NotificationManagerCompat`, high-importance channel |
| Status / silent | Silent notification + badge | Low-importance channel / ongoing notification |
| Chime | `AVAudioSession` playback + mixWithOthers | `SoundPool`/`MediaPlayer`, transient-duck audio focus |
| Health warm-start | HealthKit `HKQuantityType.heartRate` | Health Connect `HeartRateRecord` |
| Settings store | `NSUserDefaults` JSON blob | `SharedPreferences` (or DataStore) JSON blob |
| DB export | `UIActivityViewController` share sheet | `ACTION_SEND` / `ACTION_CREATE_DOCUMENT` intent |
| Settings file location | `Library/Application Support/...` | `Context.FilesDir/meltdownmonitor/...` |
| BLE peripheral re-identify | persisted `CBPeripheral` GUID in defaults | persisted device MAC/address in SharedPreferences |
| Single instance | guaranteed by OS | single-task launch mode on MainActivity |

The UI rows (Now screen, History tab, Settings sheet) are not in this table
because they are not remapped. They are the same Avalonia views.

---

## 7. BLE — `MeltdownMonitor.Ble.Android`

A new project mirroring `MeltdownMonitor.Ble.Apple`, exposing the same
`IBeatSource` contract (and the optional `IBatterySource`, `IContactSource`,
`IDeviceInfoSource`) so nothing downstream changes.

Responsibilities:
- Wrap `BluetoothManager` / `BluetoothLeScanner` for discovery and
  `BluetoothGatt` + a `BluetoothGattCallback` for the connection, mirroring the
  CoreBluetooth delegate structure in `PolarHrSource.cs`.
- Filter the scan by the Heart Rate Service UUID (`0x180D`) and the Polar name
  prefix (Polar H10 / Polar Sense), the same filter logic the Apple source
  uses, just translated to `ScanFilter`.
- Discover the three services on connect: Heart Rate (`0x180D`), Battery
  (`0x180F`), Device Information (`0x180A`), and subscribe to the Heart Rate
  Measurement characteristic (`0x2A37`) by writing the Client Characteristic
  Configuration Descriptor (`0x2902`). Android requires writing the CCCD
  explicitly, which CoreBluetooth hides, so this is the one place the Android
  source has more ceremony than the Apple one.
- **Reuse `HrMeasurementParser` and `RrArtifactFilter` from Core verbatim** —
  the GATT bytes are identical on every platform. Surface beats through a
  `Channel<Beat>` as `IAsyncEnumerable<Beat>`, the same shape as the Apple
  source.
- Persist the last-connected device address in `SharedPreferences`
  (`BleStateRestoration` analog) and prefer reconnect-by-address over a fresh
  scan on startup.
- Emit battery (`0x2A19`), sensor contact (parsed from the HR measurement flags
  byte), and device-info (`0x180A` characteristics) through the optional
  capability events, matching the Apple source's coverage so the UI lights up
  identically.

Open item logged for implementation: the GATT operation queue. Android's GATT
stack permits only one outstanding operation at a time (one read/write/descriptor
write in flight), so the source needs a small serial queue around discovery and
the CCCD write. CoreBluetooth manages this internally, so it is new code on
Android. Not hard, but it is the most common source of Android BLE bugs and
deserves explicit handling and a note in code.

---

## 8. The six service interfaces on Android

All six already exist in `MeltdownMonitor.Mobile/Services`. Android implements
them in the head:

| Interface | Android implementation | Notes |
|---|---|---|
| `INotificationDispatcher` | `NotificationManagerCompat` + channels | High-importance alert channel, low-importance status channel (§5.4) |
| `IHealthStore` | Health Connect client | Read `HeartRateRecord` for 24 h warm-start, write fast-follow (§5.3) |
| `IChimePlayer` | `SoundPool` / `MediaPlayer` | Transient-duck audio focus (§5.6) |
| `IMobileSettingsStore` | `SharedPreferences` JSON blob | Reuse `MobileSettingsSerializer` (already platform-neutral) |
| `ILiveActivityController` | foreground-service ongoing notification | The live status surface, not a no-op (§5.5) |
| `IDatabaseExporter` | `ACTION_SEND` with a `FileProvider` URI | Copy/flush the DB first, like the iOS exporter |

The concrete Mobile-scoped helpers (`MobileAlertDispatcher`,
`HealthKitEpisodeRecorder` — which is named for HealthKit but only depends on
the `IHealthStore` interface, `LiveActivityPublisher`, `MobileSettingsSerializer`)
are reused as-is. If `HealthKitEpisodeRecorder` reads awkwardly once a second
platform uses it, renaming it to `EpisodeRecorder` is a trivial, optional
cleanup in Mobile, not Android-specific work.

---

## 9. AndroidManifest and permissions

| Manifest entry | Reason |
|---|---|
| `BLUETOOTH_SCAN` (with `neverForLocation`) | BLE discovery, API 31+ |
| `BLUETOOTH_CONNECT` | GATT connection, API 31+ |
| `ACCESS_FINE_LOCATION` (`maxSdkVersion="30"`) | BLE scan on API 30 and below only |
| `POST_NOTIFICATIONS` | Alerts and status, API 33+ |
| `FOREGROUND_SERVICE` | The monitoring service |
| `FOREGROUND_SERVICE_CONNECTED_DEVICE` / `FOREGROUND_SERVICE_HEALTH` | Typed FGS permission, API 34+ |
| `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` | Optional opt-in to survive Doze (§5.1) |
| `<service android:foregroundServiceType="connectedDevice\|health">` | The monitoring service declaration |
| `<queries>` / Health Connect package visibility | So the app can resolve Health Connect (§5.3) |
| `minSdkVersion` | Propose **API 26 (Android 8)** for channels, or API 31 to drop the legacy location dance — decide in §11 |
| `targetSdkVersion` | Latest stable at implementation time (API 35+) |

`FileProvider` is declared for the DB export path. No `INTERNET` permission is
needed (local-only, like every other head), which is worth keeping true for the
Play data-safety form.

---

## 10. Build and distribution

A clear advantage over iOS: **the Android head builds on Linux, macOS, and
Windows**, not just one host. The current Linux container can build it once the
`android` workload and the Android SDK are installed (`dotnet workload install
android`), so the default dev loop does not require a Mac.

- Local dev loop: `dotnet build MeltdownMonitor.Android/MeltdownMonitor.Android.csproj`,
  deploy to an emulator or a USB device. Real BLE timing/visual behavior still
  needs a physical device plus a real Polar sensor, the same caveat the repo
  already documents for every head.
- Whole-solution caveat extends: `dotnet build` on the `.sln` already fails
  without the iOS workload. Adding Android means a plain build needs both the
  `android` and `ios` workloads, or you target specific projects. The CLAUDE.md
  "target the specific project" guidance covers this.
- CI: a new `.github/workflows/android.yml`, or an Android job added to the
  existing matrix, running on `ubuntu-latest` with JDK 17 (already set up in
  `dotnet.yml`), the Android SDK, and the `android` workload. It builds the head
  and the Ble.Android project, runs the existing Core + Mobile tests (which
  already run anywhere), and uploads the `.apk` / `.aab` as an artifact. Unlike
  the iOS workflow, this needs no Mac runner.
- Signing: a Play upload key in repo secrets, `aab` signing on tags matching
  `android-v*`, gated on a `secrets.ANDROID_SIGNING_AVAILABLE` check so forks
  and contributors without the key still get build-and-test.
- Distribution path for v1: Play **internal testing track**, then closed
  testing. Same gates as iOS: wellness-not-medical disclaimer at first launch,
  Play data-safety form filled (declare Health Connect read, no data sharing,
  no third-party tracking), and the disclaimer copy reused from
  `docs/store-submission/`.

Play policy notes, the analog of the iOS App Store rules in the iOS doc:
- Category **Health & Fitness**, no diagnose / treat / cure claims in the
  listing. The existing disclaimer posture transfers directly.
- Health Connect access requires a declaration of how the data is used. The
  honest answer is the same as HealthKit's: historical HR seeds the personal
  baseline.
- A foreground-service declaration form in Play Console must justify the
  `connectedDevice` / `health` service type — continuous sensor monitoring is a
  permitted use, but the justification has to be written. Draft it into
  `docs/store-submission/` alongside the iOS materials.

---

## 11. Open questions to resolve before coding

1. **Health Connect binding maturity.** Is `Xamarin.AndroidX.Health.Connect.Client`
   solid on `net10.0-android`, or do we need a thin JNI wrapper for the read
   path? (§5.3)
2. **minSdkVersion.** API 26 (widest reach, keeps the legacy location-permission
   dance for API <= 30) versus API 31 (drops the location workaround, loses
   older devices). What is the target audience's device floor?
3. **Foreground service type.** `connectedDevice`, `health`, or both? `health`
   (API 34+) is the most honest fit but is newer, so a fallback to
   `connectedDevice` on older targets is likely needed.
4. **DataStore vs SharedPreferences** for `IMobileSettingsStore`. SharedPreferences
   is simpler and the settings are a single JSON blob, so it is the default
   unless there is a reason to prefer DataStore.
5. **OEM battery-manager handling.** How far do we go to detect and guide users
   past aggressive killers (Samsung, Xiaomi, OnePlus)? A `dontkillmyapp`-style
   guidance screen, or just the standard battery-optimization opt-in? (§5.1)
6. **SkiaSharp Android native asset package and version** pinned to match the
   `3.119.4` SkiaSharp the Mobile assembly already references, to avoid a split.

---

## 12. Risks

| Risk | Severity | Mitigation |
|---|---|---|
| OEM battery managers kill the foreground service | High | Offer battery-optimization opt-in with clear copy, detect kills, guide users to OEM settings, field-test on Samsung/Xiaomi before claiming "always-on" |
| Android GATT one-operation-at-a-time bugs | Medium | Implement an explicit serial GATT operation queue in Ble.Android from the start (§7) |
| Health Connect binding gaps on net10.0-android | Medium | Verify the managed binding early (§11.1), JNI read-path fallback if needed |
| Play foreground-service / Health Connect review friction | Medium | Draft the FGS justification and Health Connect data-use declaration before submitting (§10) |
| Permission-flow UX sprawl (BLE + notifications + Health Connect) | Low–Medium | Sequence all asks behind the existing first-run disclaimer, reuse the iOS ordering |
| SkiaSharp version split between Mobile and the Android head | Low | Pin the Android native-asset package to the Mobile SkiaSharp version (§11.6) |

Note that the iOS doc's highest risk (background BLE silently dying because
state restoration did not fire) does not have a direct Android equivalent. The
foreground-service model is more predictable. Android's top risk is OEM battery
managers instead, which is a UX-and-guidance problem rather than an
architectural one.

---

## 13. File-by-file plan

Phases end at a buildable, demoable state, following the iOS doc's structure.
Because the Mobile layer already exists, the early phases are much shorter than
the iOS equivalents were.

### Phase 1 — scaffold (compiles, no behavior)

- `MeltdownMonitor.Ble.Android/MeltdownMonitor.Ble.Android.csproj` —
  `net10.0-android`, project ref to Core, trimmable for release.
- `MeltdownMonitor.Ble.Android/AndroidBleSource.cs` — `IBeatSource` skeleton
  throwing `NotImplementedException`.
- `MeltdownMonitor.Android/MeltdownMonitor.Android.csproj` — `net10.0-android`,
  project refs to Core, Mobile, Ble.Android, plus the Avalonia.Android and
  SkiaSharp Android native-asset packages.
- `MeltdownMonitor.Android/MainActivity.cs` — Avalonia single-view bootstrap,
  single-task launch mode, sets `App.RootViewModelFactory` and `App.Started`.
- `MeltdownMonitor.Android/Properties/AndroidManifest.xml` — all entries from
  §9, placeholder strings.
- `MeltdownMonitor.Android/AndroidCompositionRoot.cs` — `BuildRootViewModel`
  and `BuildAndStartPipelineAsync` skeletons mirroring `IosCompositionRoot`.
- `MeltdownMonitor.sln` — add the two new projects; confirm the existing
  `Any CPU`-only config rows do not try to build them on Windows-only matrices
  (the iOS projects already use build-only-on-their-host config; mirror that).

### Phase 2 — BLE end-to-end

- Flesh out `AndroidBleSource`: `BluetoothLeScanner` with a `ScanFilter` for
  `0x180D` + Polar name prefix, `connectGatt(autoConnect:true)`, a serial GATT
  operation queue, CCCD write to subscribe to `0x2A37`, parse with shared
  `HrMeasurementParser`, filter with shared `RrArtifactFilter`, surface as
  `IAsyncEnumerable<Beat>`.
- Implement the optional capability interfaces (battery, contact, device-info)
  to match the Apple source's coverage.
- `AndroidBleSource` persists/reconnects the last device address via
  `SharedPreferences`.

### Phase 3 — foreground service and composition

- `MeltdownMonitor.Android/MonitoringService.cs` — a foreground service that
  owns the pipeline (application-scoped, §5.8), shows the ongoing notification,
  and survives Activity teardown.
- `AndroidCompositionRoot.BuildAndStartPipelineAsync` — open `MeltdownRepository`
  at `FilesDir/meltdownmonitor/data.db` with `MeltdownRepositoryOptions.Default`,
  construct `AndroidBleSource`, construct `Pipeline`, warm-start from Health
  Connect (Phase 5 fills this), start, attach the alert dispatcher and the
  publisher.
- `MainActivity` binds to the running service and feeds the live pipeline to the
  view models on the UI thread.

### Phase 4 — notifications, chime, settings, export

- `Services/AndroidNotificationDispatcher.cs` — channels + `NotificationManagerCompat`.
- `Services/AndroidChimePlayer.cs` — `SoundPool`, transient-duck focus.
- `Services/SharedPreferencesSettingsStore.cs` — `IMobileSettingsStore` over a
  JSON blob, reusing `MobileSettingsSerializer`. Hydrate before pipeline start.
- `Services/IntentDatabaseExporter.cs` — `ACTION_SEND` via `FileProvider`.
- Runtime-permission sequencing behind the first-run disclaimer.

### Phase 5 — Health Connect warm-start

- `Services/HealthConnectStore.cs` implementing `IHealthStore`: request read
  permission, pull 24 h of `HeartRateRecord`, feed `BaselineHrvTracker` before
  live beats arrive. Degrade to cold start when Health Connect is absent or
  denied.

### Phase 6 — live status surface

- `Services/OngoingNotificationActivityController.cs` implementing
  `ILiveActivityController` against the foreground-service notification (§5.5),
  driven by the existing `LiveActivityPublisher`. Color, HR, RMSSD-vs-baseline
  ratio in the ongoing notification, <= 1 Hz.

### Phase 7 — CI and distribution

- `.github/workflows/android.yml` on `ubuntu-latest`: JDK 17, Android SDK,
  `android` workload, build the head + Ble.Android, run Core + Mobile tests,
  upload the `.aab`. Gate signing/upload on `secrets.ANDROID_SIGNING_AVAILABLE`.
- Draft the Play data-safety answers, the foreground-service justification, and
  the Health Connect data-use declaration into `docs/store-submission/`.

### Phase 8 — episode write-back (fast-follow)

- Extend `HealthConnectStore.WriteEpisodeAsync` to write an
  `ExerciseSessionRecord` (or HR-sample series), opt-in via the existing
  `MobileSettings` write-back flag, default off.

### Exit criteria for v1 (Phases 1–7)

1. Cold-launching on a real Android phone with a paired H10 lands in the
   monitoring state within a few seconds of the disclaimer being dismissed.
2. Locking the phone and waiting, then triggering an HR change, fires an alert
   notification while the foreground service keeps the connection.
3. The ongoing notification shows live state color and HR.
4. The exported DB opens cleanly in `sqlite3` after an `ACTION_SEND` handoff.
5. `dotnet test` is green for Core and Mobile on the Android CI job.

### Out of scope for v1 (logged for later)

- Wear OS companion.
- Health Connect episode write-back beyond the simple session record.
- Tablet / large-screen layout.
- Cloud sync.
- Polar proprietary BLE service for raw ECG.

---

## 14. What this document is not

Not a UI mockup pack (the UI already exists and is shared), not a final
selection of the Health Connect binding, not a commitment to a ship date. It
exists so the next session can scaffold the two new Android projects against a
shared understanding of the constraints, particularly the foreground-service
model and the Play policy ones, which determine more of the architecture than
the visible UI does. The visible UI is, by design, already done.
