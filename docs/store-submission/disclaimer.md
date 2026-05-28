# Disclaimer & first-run copy

Canonical text used in the first-run gate, the marketing description, and
the App Review notes. The in-app strings live in
`MeltdownMonitor.Mobile/ViewModels/DisclaimerViewModel.cs`; if either side
changes, update both.

## First-run modal

**Title.** Before we begin

**Body.** MeltdownMonitor is an informational wellness tool. It is not a
medical device and does not diagnose, treat, or manage any condition. It
estimates short-window heart-rate variability from a Polar chest strap and
tells you when your own baseline shifts — the rest is up to you. If
something feels wrong physically or mentally, talk to a qualified
clinician, not an app.

**Privacy block.** Your data stays on this device. The app reads recent
heart-rate samples from Apple Health only after you grant permission, and
only to warm up your personal baseline so you don't have to wait for
calibration on day one. Nothing is sent anywhere.

**Accept button.** I understand — continue

## App Store description (long form)

> MeltdownMonitor watches your heart-rate variability in real time using a
> Polar chest strap and tells you when your own baseline shifts. It is a
> wellness companion, not a medical device: nothing is diagnosed, nothing
> is sent off your phone, and nothing is shared without your say-so.
>
> The app pairs with a Polar H10 (and other Polar straps that expose the
> standard Bluetooth heart-rate service), tracks an exponentially-weighted
> baseline of your RMSSD, and surfaces a colour-coded "state pill" on the
> Now screen. It runs in the background so an alert can fire even with
> the phone in your pocket, and it warm-starts from Apple Health so you
> don't have to wait days to see anything useful.
>
> Designed for people who already know their nervous-system rhythms and
> want a quiet, second-opinion signal — not a diagnosis, not a coach, not
> a social feed.

## App Review notes (paste into the submission)

- MeltdownMonitor is positioned as a wellness app under guideline 1.4.1.
  It explicitly disclaims medical-device status on first launch and never
  claims to diagnose or treat any condition.
- The app requires an external Polar Bluetooth heart-rate strap. To
  review without hardware: launch the app, accept the disclaimer, grant
  Bluetooth and HealthKit permissions, and observe the "Searching for
  Polar device…" state on the Now tab. All other surfaces (History,
  Settings) are reachable without a device.
- HealthKit reads are limited to recent heart-rate samples and are used
  solely to warm the local baseline. HealthKit writes record heart-rate
  samples and dysregulation episodes for the user's own export. Both are
  described in the Info.plist usage strings.
- Background modes (`bluetooth-central`, `audio`, `processing`) are used
  to keep the BLE session alive, play the alert chime when the phone is
  locked, and run the periodic baseline maintenance task. No background
  audio playback occurs unless an alert fires.
- No data leaves the device. No analytics SDK, no crash reporter, no
  network requests beyond OS-managed APNs delivery of local notifications
  (which do not include user data).
