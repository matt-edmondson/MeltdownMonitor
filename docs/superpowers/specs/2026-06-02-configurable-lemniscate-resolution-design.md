# Configurable lemniscate resolution

**Date:** 2026-06-02
**Status:** Implemented 2026-06-03 (both heads). Re-verified against the live tree during
implementation — the renderer / `StatusWindow` / VM line numbers below predate the tri-strip-ribbon
trace rewrite and the configurable-bucket-resolution feature that landed in `main` on 2026-06-03, so
they have drifted; the design intent and consumer-site list are unchanged. The mobile plumbing now
mirrors the freshly-added `FieldIndexBuckets`/`FieldVagalBuckets` provider pattern rather than
`LobeThickness`. The Core-constant value assertion was dropped (MSTEST0032 rejects asserting a
compile-time `const`); the bounds are covered behaviourally via `Polyline` at Min/Max.

## Overview

The Regulation Field's figure-8 outline is sampled by `LemniscateGeometry.Polyline(...)`
as a fixed-count polyline. That count — the curve's **resolution** — is hardcoded as
`private const int LobeSegments = 96` in **both** renderers (`RegulationFieldView.cs:22`
desktop, `RegulationField.cs:33` mobile) and is passed to `Polyline` for both the ghost
baseline and the live trace. Lower counts make the figure-8 visibly faceted; higher counts
make it smoother.

This change makes that sample count a **user setting** on both heads, expressed as a raw
**point count** (consistent with `RegulationTrailLength` being a count), with a slider in
each head's existing settings surface.

Default **96** (preserves today's look); clamped to **24–256**. The `96 / 24 / 256`
constants are centralized in `LemniscateGeometry` as the single source of truth.

## Goals

- Centralize the resolution constants in Core: `LemniscateGeometry.DefaultSegments = 96`,
  `MinSegments = 24`, `MaxSegments = 256`. Replaces the two hardcoded `const LobeSegments = 96`.
- One mirrored setting (`LobeSegments`, default `DefaultSegments`) on both `AppSettings` and
  `MobileSettings`.
- A slider on both heads following each head's existing render-tunable pattern (the
  `LobeThickness` / `RegulationTrailLength` plumbing), persisted.
- The renderers adopt the configured resolution **live** (next frame / next reading), matching
  how the rest of the field tuning already updates without restart.
- No change to the field's visual behaviour other than the outline's smoothness.

## Non-goals

- A "render quality" dial spanning trace texture / jitter detail — the knob is the outline
  sample count only (explicitly chosen over the broader interpretation in brainstorming).
- Comet-trail point density — that is `RegulationTrailLength`, a separate existing setting.
- A shared settings type across heads (the codebase keeps `AppSettings` and `MobileSettings`
  separate — the field is mirrored, not unified).
- Refactoring the *other* render knobs' duplicated clamp bounds (`0.5/3.0`, `12/2160`, …) to use
  Core constants — out of scope; only this new knob's constants are centralized.

## Core constants (`MeltdownMonitor.Core`)

Add to `LemniscateGeometry` (where `Polyline` already lives), so the default and bounds have one
testable home:

```csharp
/// <summary>Default outline sample count — preserves the historical fixed resolution.</summary>
public const int DefaultSegments = 96;

/// <summary>Minimum configurable resolution; still a recognizable figure-8 (clearly faceted).
/// Floor of 24 also keeps the desktop trace's n-1 divisor safe.</summary>
public const int MinSegments = 24;

/// <summary>Maximum configurable resolution; smooth, with diminishing visual return above.</summary>
public const int MaxSegments = 256;
```

These are referenced by both heads' settings defaults and consumer clamps. `Polyline` itself is
unchanged (it already validates `segments >= 0`).

**Safety note (verified, gates the 256 ceiling):** raising the count to 256 is safe on both
renderers. The desktop live trace indexes its RR-deviation array via a `posBuf` value **clamped
to `[0, devLen-1]`** (`RegulationFieldView.cs:325`), so the index is decoupled from the segment
count; the only segment-count-sized array is `pts = new Vector2[n]`, consistent with the
`n`-point polyline. The mobile jitter uses the vertex index only as a sine phase term
(`RegulationFieldAnimator.cs:86`), not an array index. The one constraint is `n >= 2` (the
desktop loop divides by `n-1` at `RegulationFieldView.cs:324`), which the floor of 24 satisfies.

## Setting

Add to both settings types (same name, type, default):

```csharp
/// <summary>Number of points sampled along the Regulation Field's figure-8 outline — its
/// render resolution (24–256; clamped at the consumer). Default 96 preserves the original
/// fixed look; lower = faceted, higher = smoother.</summary>
public int LobeSegments { get; set; } = LemniscateGeometry.DefaultSegments;
```

- Desktop: `MeltdownMonitor.App/AppSettings.cs`
- Mobile: `MeltdownMonitor.Mobile/MobileSettings.cs` (auto-serialized — the reflection-based
  `MobileSettingsSerializer` needs no change)

Valid range `MinSegments`–`MaxSegments`, enforced by clamping at the read/consumer sites (the
desktop pipeline accessor, the mobile VM setter, and the mobile control's render call), so a
hand-edited settings file can't produce a degenerate or runaway count.

## Desktop wiring (`MeltdownMonitor.App`)

1. **Pipeline accessor.** Mirror the existing `LobeThickness` accessor (`Pipeline.cs:84`). Add:
   ```csharp
   /// <summary>Configured Regulation Field outline resolution (clamped), read live by the field view.</summary>
   public int LobeSegments =>
       Math.Clamp(_settings.LobeSegments, LemniscateGeometry.MinSegments, LemniscateGeometry.MaxSegments);
   ```

2. **Renderer.** In `RegulationFieldView.cs`, **remove** `private const int LobeSegments = 96;`
   (line 22) and have the two `Polyline` calls read the pipeline accessor instead:
   - line 288 (ghost): `LemniscateGeometry.Polyline(centre, halfWidth, baseLobeHeight, _pipeline.LobeSegments)`
   - line 296 (live): `LemniscateGeometry.Polyline(centre, halfWidth, liveLobeHeight, _pipeline.LobeSegments)`

3. **Settings knob.** In `StatusWindow.cs`, add an int `ImGuiWidgets.Knob` next to the existing
   Trail / Jitter / Lobe-thickness knobs (~line 1029), mirroring the `RegulationTrailLength` knob
   (`StatusWindow.cs:1021`):
   ```csharp
   int segments = _settings.LobeSegments;
   if (ImGuiWidgets.Knob("Resolution", ref segments,
           LemniscateGeometry.MinSegments, LemniscateGeometry.MaxSegments,
           format: "%d pts", flags: ImGuiKnobOptions.ValueTooltip))
   {
       _settings.LobeSegments = segments;
       _settingsDirty = true;
   }
   ```
   with a `HelpMarker` ("How finely the figure-8 outline is sampled. Higher = smoother curve;
   lower = more faceted."). Add `_settings.LobeSegments = LemniscateGeometry.DefaultSegments;` to
   the reset-to-defaults block (`StatusWindow.cs:~1502`) alongside the other render resets.

## Mobile wiring (`MeltdownMonitor.Mobile` + `MeltdownMonitor.iOS`)

1. **Control styled property.** In `Controls/RegulationField.cs`, **remove**
   `private const int LobeSegments = 96;` (line 33) and add a styled property mirroring
   `LobeThicknessProperty` (lines 69–70):
   ```csharp
   public static readonly StyledProperty<int> LobeSegmentsProperty =
       AvaloniaProperty.Register<RegulationField, int>(
           nameof(LobeSegments), LemniscateGeometry.DefaultSegments);

   public int LobeSegments
   {
       get => GetValue(LobeSegmentsProperty);
       set => SetValue(LobeSegmentsProperty, value);
   }
   ```
   Add `LobeSegmentsProperty` to the `AffectsRender<RegulationField>(...)` list (line 109) — unlike
   jitter, changing resolution changes geometry, so it must re-render (matching `LobeThicknessProperty`).
   At the `Polyline` call (line 226), clamp at the consumer:
   ```csharp
   var ghost = LemniscateGeometry.Polyline(centreV, halfWidth, lobeHeight,
       Math.Clamp(LobeSegments, LemniscateGeometry.MinSegments, LemniscateGeometry.MaxSegments));
   ```

2. **Now VM provider.** In `NowViewModel`, mirror the `LobeThickness` plumbing
   (`NowViewModel.cs:198`, `:568`):
   - Add a constructor parameter `Func<int>? lobeSegmentsProvider = null` (keeps existing call
     sites compiling) and store it in a `_lobeSegmentsProvider` field.
   - Add a `SetField`-backed property exactly like `LobeThickness` (`NowViewModel.cs:198`) — a
     `_lobeSegments` field initialized to `LemniscateGeometry.DefaultSegments` with
     `public int LobeSegments { get => _lobeSegments; private set => SetField(ref _lobeSegments, value); }`.
     It must raise change notification because `NowView.axaml` binds the control's property to it.
   - In `OnReadingUpdated`, alongside the jitter/thickness refresh (line 568), add:
     ```csharp
     LobeSegments = Math.Clamp(_lobeSegmentsProvider?.Invoke() ?? LemniscateGeometry.DefaultSegments,
         LemniscateGeometry.MinSegments, LemniscateGeometry.MaxSegments);
     ```

3. **View binding.** In `Views/NowView.axaml`, bind the control's new property next to the
   existing `LobeThickness` binding: `LobeSegments="{Binding LobeSegments}"`.

4. **Settings VM + view.** In `SettingsViewModel`, add an int property mirroring
   `RegulationTrailLength`'s clamp/Raise/Persist shape (`SettingsViewModel.cs:165`):
   ```csharp
   public int LobeSegments
   {
       get => _settings.LobeSegments;
       set
       {
           int clamped = Math.Clamp(value, LemniscateGeometry.MinSegments, LemniscateGeometry.MaxSegments);
           if (_settings.LobeSegments != clamped)
           {
               _settings.LobeSegments = clamped;
               Raise();
               Persist();
           }
       }
   }
   ```
   Add a `Slider` (Min `MinSegments`, Max `MaxSegments`) bound to it in
   `Views/SettingsView.axaml`, next to the existing Lobe-thickness slider, with a label showing
   the current value.

5. **Composition.** In `MeltdownMonitor.iOS/IosCompositionRoot.cs:66`, pass the provider to the
   `NowViewModel` constructor alongside the existing ones:
   `lobeSegmentsProvider: () => settings.LobeSegments`. The default `new NowViewModel(...)` factory
   stays unchanged — a null provider falls back to `DefaultSegments`.

## Testing

**Test-reachable (Core + Mobile → `MeltdownMonitor.Tests`):**
- **Core `LemniscateGeometry`:** assert the new constants (`DefaultSegments == 96`,
  `MinSegments == 24`, `MaxSegments == 256`, `MinSegments < DefaultSegments < MaxSegments`); a
  `Polyline` at `MinSegments` and at `MaxSegments` returns the requested point count and stays a
  closed, symmetric figure-8 (extend the existing `LemniscateGeometryTests`).
- **Mobile `MobileSettingsSerializer`:** `LobeSegments` round-trips; default is `DefaultSegments`
  (mirror `RoundTrip_PreservesLobeThickness` / `Default_LobeThickness_Is1`).
- **Mobile `SettingsViewModel`:** `LobeSegments` round-trips onto `MobileSettings`, clamps to
  24–256 (below floor → 24, above ceiling → 256), and raises/persists only on a real change
  (mirror the `LobeThickness` VM tests).
- **Mobile `NowViewModel`:** `OnReadingUpdated` adopts the provider's value clamped to range; a
  null provider yields `DefaultSegments` (mirror the existing jitter/thickness provider tests).

**Build + live app (the usual gate for the un-testable heads):**
- Desktop `RegulationFieldView` resolution read-through and the `StatusWindow` knob: App builds
  clean under warnings-as-errors (CI) + live app — confirm moving the knob smooths/facets the
  figure-8 immediately, including at 24 and 256.
- Mobile `RegulationField` styled property + `SettingsView` slider: iOS builds on macOS CI; live
  validation that the slider re-renders the outline.

## Risks / edge cases

- **Clamp at every consumer** (desktop pipeline accessor, mobile control render call, mobile VM
  setter) so a corrupt/hand-edited settings blob can't yield a degenerate (< 24) or runaway count;
  the persisted value is also clamped on write in the mobile VM.
- **`n >= 2` invariant:** the desktop live-trace loop divides by `n-1` (`RegulationFieldView.cs:324`).
  The floor of 24 keeps this safe; this is why `MinSegments` must stay ≥ 2 (and is set far above it).
- **Live resize:** desktop re-reads the accessor each frame (instant); mobile re-reads on the next
  reading via the provider and `AffectsRender` re-renders — acceptable, the outline updates within
  one emit interval.
- **Name collision:** both renderers currently have a `const LobeSegments`; it must be *removed*
  (not shadowed) when the App accessor / Mobile styled property of the same name is introduced.

## Follow-ups (not in this spec)

- None. The other render knobs' duplicated clamp bounds could later be migrated to Core constants
  in the same spirit, but that is deliberately out of scope here.
