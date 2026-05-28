# Screenshot plan

App Store Connect requires screenshots at specific sizes for each device
class. MeltdownMonitor is iPhone-only (UIDeviceFamily=1) so we only need
the iPhone sizes.

## Required sizes (as of 2026)

| Display class | Pixels (portrait) | Reference device |
|---|---|---|
| 6.9" Super Retina XDR | 1290 × 2796 | iPhone 16 Pro Max |
| 6.5" Super Retina | 1284 × 2778 | iPhone 14 Plus |
| 5.5" Retina HD | 1242 × 2208 | iPhone 8 Plus (legacy slot, still required) |

Apple accepts upscaled 6.9" art for the 6.5" slot; the 5.5" slot must be
its own rendered set.

## Shot list

Each entry is captured from the simulator `.app` bundle produced by the
`Build & test (macOS)` job in `.github/workflows/ios.yml`. Download the
`meltdownmonitor-ios-simulator` artifact, drag it onto a booted simulator
of the right device class, and trigger the listed app state.

1. **Now (Watching state).** Default screen after device pairing. Live
   HR readout, RMSSD strip, green state pill. Captures the "this is
   what it looks like every day" feeling.
2. **Now (Alert state).** Drive the pipeline into `Alert` via the
   debug seeding hook (Settings → Developer → "Seed alert" — debug
   builds only; for release-class shots, hand-grip a real strap before
   capture). Red state pill, chime indicator visible.
3. **History.** A populated week view with at least one alert episode
   marked. Use the seed-history script (`MeltdownMonitor.Tests` includes
   a fixture generator) to make this reproducible.
4. **Settings.** Shows the disclaimer link, HealthKit toggle, chime
   selector, baseline-reset button. Demonstrates that the user is in
   control of their data.
5. **First-run disclaimer.** Captured before accepting. Important
   context for App Review even if used as a secondary screenshot.

## Capture command

From a booted simulator:

```bash
xcrun simctl io booted screenshot ~/Desktop/mm-now-watching.png
```

Run the command for each shot and rename. The simulator's status bar can
be normalised with `xcrun simctl status_bar booted override --time
"9:41" --batteryState charged --batteryLevel 100 --cellularBars 4
--wifiBars 3` so all screenshots share the same chrome.

## Storage

Captured PNGs are intentionally **not** checked in to the repo — they're
re-generated per release from the CI artifact to stay in sync with the
current UI. Upload them directly to App Store Connect; keep a working
copy in a non-repo location (e.g. shared drive) keyed by release tag.
