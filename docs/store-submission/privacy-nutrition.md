# Privacy Nutrition answers (App Store Connect)

Pre-filled answers for the "App Privacy" questionnaire in App Store
Connect. MeltdownMonitor is local-only by design, so almost everything is
"Not Collected" — these notes exist so a future submission run can paste
them in without re-litigating the reasoning.

## Headline label

> Data Not Collected — The developer does not collect any data from this
> app.

This is what the on-store label should display. Every section below
explains why.

## Per-category answers

### Health & Fitness

- **Heart rate / HRV samples.** Read from HealthKit, processed entirely
  on device, optionally written back to HealthKit. Never leaves the user's
  device, never sent to a server we control.
- **Answer:** Not Collected.
- **Rationale:** Apple's definition of "Collect" requires the data to
  leave the device and be received by a server the developer controls.
  HealthKit reads/writes stay between the app and the user's own Health
  store.

### Identifiers

- **Device ID, user ID, advertising ID.** None requested.
- **Answer:** Not Collected.

### Usage Data / Diagnostics

- No analytics SDK, no telemetry, no crash reporter.
- **Answer:** Not Collected.
- **Note:** If a future build adds OSLog-style diagnostics that ship in
  Apple's standard crash reports (opt-in via Settings → Privacy &
  Security → Analytics), update this entry — those go to Apple, not us,
  but Apple's questionnaire still wants them disclosed if we read them
  back via MetricKit.

### Contact Info / User Content / Search History / Browsing / Financial / Location

- Not requested, not stored, not transmitted.
- **Answer:** Not Collected for every row.

## Permission strings (already in `MeltdownMonitor.iOS/Info.plist`)

| Key | Current copy |
|---|---|
| `NSBluetoothAlwaysUsageDescription` | MeltdownMonitor connects to your Polar heart rate sensor over Bluetooth to read R-R intervals. |
| `NSBluetoothPeripheralUsageDescription` | MeltdownMonitor connects to your Polar heart rate sensor over Bluetooth to read R-R intervals. |
| `NSHealthShareUsageDescription` | MeltdownMonitor reads recent heart rate samples to warm-start your personal baseline so you don't have to wait for calibration on first launch. |
| `NSHealthUpdateUsageDescription` | MeltdownMonitor records heart rate samples and dysregulation episodes to Apple Health so you own your data and can share it with a clinician. |

Keep these in lockstep with this file and `disclaimer.md`. App Review
treats divergence between Info.plist usage strings, on-store privacy
copy, and the first-run modal as a red flag.

## "Why do you need this data?" pre-drafts (§14 risk: HealthKit review)

If App Review asks for written justification (they often do on first
submission of a HealthKit app), reply with:

1. **Heart-rate read (HealthKit share).** "The app's HRV baseline takes
   roughly five days of continuous wear to settle from a cold start. By
   reading the user's existing heart-rate history from HealthKit, the
   baseline warms up immediately and the user sees meaningful
   colour-coded state on day one instead of staring at a calibration
   screen for a week. The data never leaves the device."
2. **Heart-rate write (HealthKit update).** "Sessions are stored locally
   in SQLite, but users have repeatedly asked to share session summaries
   with their own clinicians. Writing to HealthKit lets them export
   through the standard Health app rather than through a developer-built
   channel, which is both safer and more familiar."
3. **Background BLE.** "Dysregulation episodes are precisely the moments
   the user is least likely to be looking at the phone. Without
   `bluetooth-central` in `UIBackgroundModes` the connection drops within
   seconds of screen-off, defeating the entire premise of the app."
