# Google Play submission notes (Android)

Reference material for Play Console submissions of MeltdownMonitor for Android,
the counterpart to the iOS App Store material in this folder (design doc
`docs/android-design.md` §10). Cutting a release is `git tag android-vX.Y.Z &&
git push --tags`; the `Android Workflow` (`.github/workflows/android.yml`)
produces the signed `.aab`, which is then uploaded to the Play internal-testing
track by hand for v1.

The disclaimer and privacy posture are shared with iOS — see `disclaimer.md`
and `privacy-nutrition.md`. Only the Android-specific Play Console declarations
are recorded here.

## Listing basics

- Category: **Health & Fitness**.
- No diagnose / treat / cure claims in the listing — the same wellness-not-medical
  posture as iOS. The first-run disclaimer text is reused verbatim.
- No `INTERNET` permission is declared, so the app cannot send data off-device.
  Keep this true: it is what lets the data-safety form below be answered honestly.

## Data safety form

- **Data collected / shared:** none leaves the device. No analytics, no
  third-party tracking, no ads. Heart-rate variability and self check-ins are
  stored only in the app's private SQLite database.
- **Health Connect read:** the only health data touched is historical heart
  rate, read from Health Connect to warm the personal HRV baseline (see below).
  It is processed on-device and never transmitted.
- **Data export:** the user can share a copy of their own database via the
  system share sheet. This is a user-initiated export, not background sharing.

## Health Connect data-use declaration

Play requires a declaration of how Health Connect data is used.

- **Permission requested:** read `HeartRateRecord`.
- **Purpose:** seed the user's personal heart-rate baseline at startup so the
  app does not need a cold calibration period on first run. This is the exact
  analog of the iOS HealthKit read justification.
- **Scope:** the most recent 24 hours of heart-rate samples, read once per
  launch. No continuous background reads.
- **Storage / sharing:** the derived baseline lives on-device only; no Health
  Connect data is stored verbatim or shared.
- **Write-back:** off by default. When the user opts in, the app may write a
  session/episode record. Default-off matches the wellness-rules posture.

> Implementation status: the warm-start read path is the design target
> (`docs/android-design.md` §5.3 / Phase 5). The shipped `HealthConnectStore`
> degrades to a cold start until the binding decision in §11.1 is resolved, so
> the declaration above describes the intended behaviour to submit alongside it.

## Foreground-service declaration

Play Console requires a justification for the foreground-service type.

- **Service type:** `connectedDevice` (declared on `MonitoringService`).
- **Justification:** the app maintains a continuous Bluetooth Low Energy
  connection to a heart-rate chest strap and processes its readings in real
  time. Monitoring must continue while the screen is off and the app is
  backgrounded, which is the defining use of a connected-device foreground
  service. The ongoing notification it shows doubles as the live status surface
  (design doc §5.5).
- **Why not a background job:** the value is real-time, continuous heart-rate
  variability tracking; deferred or batched background execution (WorkManager,
  JobScheduler) cannot keep a live GATT connection open, so it does not meet the
  need.

## Battery-optimization copy

The app offers — never silently requests — the battery-optimization opt-in
(`REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`) so monitoring survives Doze and
aggressive OEM battery managers (design doc §5.1). The Play listing should note
that continuous monitoring may be interrupted by the device's battery manager
and point users to the in-app guidance.
