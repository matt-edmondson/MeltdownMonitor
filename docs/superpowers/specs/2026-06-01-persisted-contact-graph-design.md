# Persisted sensor-contact + Overview graph

**Date:** 2026-06-01
**Status:** Design, approved in brainstorming (persist to DB; hrv_samples column; 0/1 "contact OK" strip), pending spec review.

## Overview

Sensor skin/electrode contact (`SensorContactStatus`: NotSupported / NotDetected / Detected)
is currently a **live-only** signal — tracked as `LatestContact` on each pipeline, surfaced as
a "contact lost" warning, used to gate detection/baseline/trail-velocity, but **never persisted**.
This change **persists contact at the HRV-sample cadence** and adds a **contact graph to the
desktop Overview tab**, alongside the existing metric sparklines.

Contact is recorded as part of each `HrvSample` (analogous to the `State` it already carries),
stored in a new `contact` column on the existing `hrv_samples` table via the repository's
established additive-migration path. The Overview graph is a **0/1 "contact OK" step strip**:
**1 = Detected or NotSupported** (signal trustworthy), **0 = NotDetected** (readings gated).

This is a **desktop (`App`) feature** — the Overview tab exists only in the Windows ImGui head.
The persistence layer is shared Core; both heads stamp contact onto the sample they persist.

## Goals

- Persist contact at the HRV-sample cadence (~5 s), integrated with the existing
  `hrv_samples` write/read/backfill path (no new table, no new read method).
- A contact step-strip in the desktop Overview tab, fed live and backfilled like the other
  sparklines, sharing their x-axis and window.
- Old databases (no `contact` column) upgrade transparently and read as `NotSupported`.
- No change to contact's role in gating; this only *records and visualizes* it.

## Non-goals

- Full-resolution (every-transition) contact history — sample-resolution (~5 s) matches the
  Overview graph's shared x-axis; a dedicated transitions table is explicitly not built.
- A mobile contact graph — mobile has no Overview tab. (Mobile still stamps contact into the
  persisted sample, so the data exists for any future use.)
- Changing the existing "Sensor contact lost" warnings or the overlay `Contact` metric.

## Data model (Core — testable)

### `HrvSample` (`MeltdownMonitor.Core/Hrv/HrvSample.cs`)

Add an **init-only** property (NOT a positional ctor parameter, so every existing
`new HrvSample(...)` call site keeps compiling unchanged):

```csharp
/// <summary>Sensor skin/electrode contact at this sample's moment. Default
/// <see cref="SensorContactStatus.NotSupported"/> (sensor not reporting contact).</summary>
public SensorContactStatus SensorContact { get; init; } = SensorContactStatus.NotSupported;
```

`ShortWindowHrvCalculator` does not set it (stays default); the pipeline stamps it (below).

### Persistence (`MeltdownMonitor.Core/Persistence/MeltdownRepository.cs`)

- **Migration:** add `contact` to the `MigrateHrvSamples` additive-column list (stored as
  `TEXT`, like `state`). Old rows have NULL → read as `NotSupported`.
- **Write:** `InsertHrvSample` includes `contact` = `sample.SensorContact.ToString()`.
- **Read:** both `GetHrvSamples` and `ReadHistory` select the `contact` column and set
  `SensorContact` on the constructed `HrvSample`, parsing case-insensitively; NULL/unknown →
  `NotSupported` (defensive, mirrors how `state` is read but tolerant of the pre-migration NULL).

Storing the enum name as TEXT matches the existing `state` column convention and keeps the
column human-readable.

## Pipeline stamping (both heads)

Both `App/Pipeline.cs` and `Mobile/Pipeline.cs` already have `LatestContact` in scope where
they build `finalSample`. Add it to the `with` expression:

```csharp
var finalSample = sample with
{
    BaselineRmssd = _baseline.BaselineRmssd,
    BaselineHr = _baseline.BaselineHr,
    State = state,
    SensorContact = LatestContact,
};
```

So contact persists through the existing `_repository.InsertHrvSample(finalSample)` with no new
write path. (Both heads stamp it even though only desktop graphs it, so the persisted data is
complete and symmetric.)

## Desktop Overview graph (`App` — CI + live app)

In `MeltdownMonitor.App/StatusWindow.cs`:

- **Buffer:** add `private readonly RingBuffer<float> _contact = new(InitialSparklineCapacity);`
  and include it in the `AllSparklines` array, so it is resized/resampled with the others
  (contact is now on the HRV-sample cadence, unlike battery which stays separate).
- **Mapping helper:** `private static float ContactToValue(SensorContactStatus c) => c == SensorContactStatus.NotDetected ? 0f : 1f;` — 1 = Detected/NotSupported (trustworthy), 0 = NotDetected (gated).
- **Live feed:** in `OnSampleUpdated`, `_contact.PushBack(ContactToValue(sample.SensorContact));`
- **Backfill:** in `BackfillFromRepository`'s sample loop, `_contact.PushBack(ContactToValue(s.SensorContact));` (the `AllSparklines` resize loop already covers `_contact`).
- **Render:** in `DrawOverviewTab`, draw a compact **contact strip** — a fixed-range [0,1] plot
  labelled e.g. "Sensor contact" that reads as a flat band at 1 dropping to 0 during contact
  loss (rendered with the existing sparkline/plot helper, fixed Y range 0..1, colored from the
  state palette — green-ish at 1, warning color at 0). It is a binary signal, so it must use a
  step/fixed-range presentation, not an auto-scaled wiggly line.

## Testing

**Test-reachable (Core → `MeltdownMonitor.Tests`):**
- `MeltdownRepository` round-trip: insert an `HrvSample` with `SensorContact = NotDetected`,
  read it back via `GetHrvSamples` and `ReadHistory`, assert `SensorContact == NotDetected`;
  insert with `Detected`, assert it round-trips.
- Default/migration tolerance: a sample inserted without setting contact reads back as
  `NotSupported`; reading rows from a schema that predates the column yields `NotSupported`
  (simulate by the default path — an `HrvSample` whose contact was never written).
- `HrvSample` default: `new HrvSample(...).SensorContact == NotSupported`.

**Build + live app (desktop, the usual gate):**
- The `App` build (CI — can't restore `ktsu.ImGui.App 2.6.0` locally) and a live-app check:
  the Overview tab shows the contact strip flat at "OK", dropping when the sensor loses contact,
  and the strip backfills on window open from persisted history.

## Risks / edge cases

- **Non-breaking `HrvSample` change:** init-only property with a default means no positional
  ctor breakage; verify by building Core + Mobile + Tests (which construct `HrvSample`) clean.
- **Migration on existing DBs:** the `contact` column is added by `MigrateHrvSamples` on open;
  pre-existing rows read `NotSupported`. Confirm a repository opened on an old-schema DB doesn't
  throw and reads contact as NotSupported.
- **Sample-cadence resolution:** sub-5 s contact blips between emitted samples aren't captured —
  acceptable and consistent with the Overview graph's shared x-axis (documented, not a defect).
- **Pause/no-sample gaps:** when no sample is emitted (paused, no beats), no contact point is
  recorded — identical to every other Overview sparkline; the strip simply has no new points.

## Follow-ups (not in this spec)

- A mobile contact history view (mobile has no Overview tab today).
- Full-resolution contact transitions (separate table) if sub-sample fidelity is ever needed.
