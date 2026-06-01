# Configurable comet-trail length

**Date:** 2026-06-01
**Status:** Design, approved in brainstorming (count-based knob), pending spec review.

## Overview

The Regulation Field's comet trail is currently a **fixed 48-reading buffer** in both
front-ends, hardcoded as two constants (`RegulationFieldView.TrailLength` on desktop,
`NowViewModel.RegulationTrailLength` on mobile). At the default 5 s emit cadence that is
~4 minutes of history. This change makes the trail length a **user setting** on both
heads, expressed as a **point count** (not minutes — the user chose the count knob over a
time knob; the on-screen time span therefore equals `count × emit cadence`).

Default **48** (preserves today's look); clamped to **12–240** (≈1–20 min at 5 s).

## Goals

- One mirrored setting (`RegulationTrailLength`, default 48) on both `AppSettings` and `MobileSettings`, replacing the two hardcoded constants.
- A Settings slider on both heads, following each head's existing settings pattern.
- The trail buffer adopts the configured cap **live** (next appended reading), matching how the rest of the field tuning already updates without restart.
- No change to the trail's visual behaviour other than its length.

## Non-goals

- Minutes/time-based eviction (explicitly not chosen; the knob is a count).
- A shared settings type across heads (the codebase keeps `AppSettings` and `MobileSettings` separate — the field is mirrored, not unified).
- Changing the sparkline history (`SparklineWindowMinutes`) — a separate, unrelated setting.

## Setting

Add to both settings types (same name, type, default):

```csharp
/// <summary>Number of recent readings drawn as the Regulation Field comet trail.</summary>
public int RegulationTrailLength { get; set; } = 48;
```

- Desktop: `MeltdownMonitor.App/AppSettings.cs`
- Mobile: `MeltdownMonitor.Mobile/MobileSettings.cs`

Valid range **12–240**, enforced by clamping at the read/consumer sites (the desktop
pipeline accessor and the mobile VM setter), so a hand-edited settings file can't produce
a zero-length or pathologically huge buffer.

## Desktop wiring (`MeltdownMonitor.App`)

1. **Pipeline accessor.** `App/Pipeline.cs` already surfaces settings to the view (e.g.
   `LatestThresholds => _settings.Thresholds`). Add:
   ```csharp
   /// <summary>Configured comet-trail length (clamped), read live by the field view.</summary>
   public int RegulationTrailLength => Math.Clamp(_settings.RegulationTrailLength, 12, 240);
   ```

2. **Dynamic trail buffer.** `App/Regulation/RegulationFieldView.cs` currently uses a fixed
   `private readonly TrailPoint[] _trail = new TrailPoint[TrailLength];` ring buffer with
   `Array.Copy` shifting and an `_trailCount`. Replace with a growable list capped to the
   live setting:
   - Store trail points in a `List<TrailPoint>` (still under `_lock`).
   - In `OnSampleUpdated`, append the new point, then trim from the front while
     `count > _pipeline.RegulationTrailLength` (mirrors the mobile `while` trim). This makes
     lowering the slider drop the oldest points on the next reading and raising it simply
     allow more to accumulate.
   - `Snapshot()` returns `_trail.Select(p => p.Reading).ToArray()` under the lock.
   - Remove the now-unused `TrailLength` const and `_trailCount` field.

3. **Settings tab slider.** In `App/StatusWindow.cs`, add a `SliderInt` for the trail
   length next to the existing `SparklineWindowMinutes` slider (~line 912), e.g.
   `ImGui.SliderInt("Comet trail length", ref len, 12, 240)`, writing
   `_settings.RegulationTrailLength`. Add `RegulationTrailLength = 48` to the
   reset-to-defaults block (~line 1348) alongside the other resets.

## Mobile wiring (`MeltdownMonitor.Mobile` + `MeltdownMonitor.iOS`)

1. **Injected provider.** `NowViewModel` builds the trail in `OnReadingUpdated` and already
   trims with a `while` loop against the `RegulationTrailLength` const. Replace the const
   with an injected provider so the VM stays free of the settings type (consistent with its
   other injected callbacks):
   - Add a constructor parameter `Func<int>? trailLengthProvider = null` (keeps both existing
     call sites compiling).
   - In `OnReadingUpdated`, compute `int cap = Math.Clamp(_trailLengthProvider?.Invoke() ?? 48, 12, 240);`
     and trim `_regulationTrail` to `cap`.

2. **Composition.** In `MeltdownMonitor.iOS/IosCompositionRoot.cs:66`, pass the provider:
   `new NowViewModel(onAnnotate: RecordAnnotationAsync, trailLengthProvider: () => settings.RegulationTrailLength)`.
   The default factory `RootViewModel.cs:50` (`new NowViewModel()`) stays unchanged — a null
   provider falls back to 48.

3. **Settings VM + view.** In `SettingsViewModel`, add an int property mirroring
   `RmssdWarningDropPercent`'s clamp/Raise/Persist shape:
   ```csharp
   public int RegulationTrailLength
   {
       get => _settings.RegulationTrailLength;
       set
       {
           int clamped = Math.Clamp(value, 12, 240);
           if (_settings.RegulationTrailLength != clamped)
           {
               _settings.RegulationTrailLength = clamped;
               Raise();
               Persist();
           }
       }
   }
   ```
   Add a `Slider` (Min 12, Max 240) bound to it in `Views/SettingsView.axaml`, with a label
   showing the current value.

## Testing

**Test-reachable (Core + Mobile → `MeltdownMonitor.Tests`):**
- `NowViewModel`: feeding more than the cap of readings via `OnReadingUpdated` leaves
  `RegulationTrail.Count == cap`; lowering the provider's value then pushing one reading
  trims to the new (smaller) cap keeping the newest; a null provider caps at the default 48;
  out-of-range provider values are clamped (e.g. provider returns 5 → cap 12; returns 9999 → cap 240).
- `SettingsViewModel`: `RegulationTrailLength` round-trips onto `MobileSettings`, clamps to
  12–240, and raises/persists only on a real change.

**Build + live app (the usual gate for un-testable heads):**
- Desktop `RegulationFieldView` dynamic buffer and the two Settings sliders: App builds clean
  under warnings-as-errors (CI, since `ktsu.ImGui.App 2.6.0` can't restore locally) + live
  app — confirm moving the slider lengthens/shortens the trail immediately.

## Risks / edge cases

- **Clamp at every consumer** (desktop pipeline accessor + mobile VM) so a corrupt/hand-edited
  settings blob can't yield a 0-length or runaway buffer; the persisted value itself is also
  clamped on write in the mobile VM.
- **Live resize:** lowering the cap trims oldest on the next reading (not instantly on the
  current frame) — acceptable; the trail simply shortens within one emit interval.
- **Desktop buffer swap** (`array → List`) must preserve the existing oldest→newest order and
  the `_lock` discipline (written on the sample thread, read in `Snapshot`).

## Follow-ups (not in this spec)

- Optionally annotate the slider with the approximate time span (`count × cadence`) — skipped
  for now to keep the knob honestly count-based.
