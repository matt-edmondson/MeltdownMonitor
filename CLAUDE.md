# CLAUDE.md

Project-specific guidance for working in this repo. Shared .NET conventions live in the user's global CLAUDE.md and are not repeated here.

## What this is

MeltdownMonitor watches autonomic-nervous-system dysregulation in real time: it streams RR intervals from a Polar H10 / Verity Sense over BLE, computes rolling HRV (RMSSD, pNN50, HR, plus LF/HF + Poincaré), maintains a personal EWMA baseline, runs a detection state machine, and surfaces calm alerts. It ships a Windows desktop (Dear ImGui) front-end and an iOS/mobile (Avalonia) front-end over a shared Core. See `README.md` for the domain explainer and glossary.

## Build & test

All projects target **.NET 10** (`net10.0` / `net10.0-windows10.0.19041.0` / `net10.0-ios`). You need the **.NET 10 SDK**.

```
dotnet build MeltdownMonitor.Core/MeltdownMonitor.Core.csproj
dotnet test  MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj   # no BLE/Windows needed
dotnet run   --project MeltdownMonitor.App                        # Windows only
```

Build matrix:
- **Core / Mobile / Tests** — build & test on Linux, macOS, or Windows. This is your default loop.
- **App / Ble.Windows** — `net10.0-windows10.0.19041.0`; build only on Windows. A plain `net10.0` project (incl. Tests) **cannot** reference them.
- **iOS / Ble.Apple** — `net10.0-ios`; build only on macOS with Xcode + the `ios` workload.

`dotnet build` / `dotnet test` on the whole solution will try to build the iOS heads and fail without the iOS workload — target the specific project instead.

## Layout

```
Core            net10.0   HRV math, detection state machine, EWMA baseline + history seeding,
                          regulation-field calculator, beat-source abstractions, SQLite persistence.
                          Platform-neutral; the source of truth for all numbers.
Ble.Windows     net10.0-windows   WinRT BLE → IBeatSource (+ battery/contact/device-info)
Ble.Apple       net10.0-ios       CoreBluetooth BLE + background state restoration
App             net10.0-windows   ImGui status window, tray, overlay. Windows entry point.
Mobile          net10.0   Avalonia UI + view models + platform-neutral service interfaces
iOS             net10.0-ios       iOS head: composition root + native services (HealthKit,
                          UserNotifications, AVFoundation, ActivityKit)
Tests           net10.0   MSTest; references Core + Mobile only
```

Pipeline flow (both heads): `IBeatSource → RrArtifactFilter → ShortWindowHrvCalculator → BaselineHrvTracker (gated) → DysregulationDetector (gated) → RegulationFieldCalculator → UI + MeltdownRepository`.

## Gotchas

- **BLE beats are batched, not real-time-testable.** RR intervals arrive in bursts that share one notification timestamp. Synthetic/replay beat sources can reproduce data-flow logic but **cannot** reproduce real-time visual/timing behaviour (animation stutter, playhead scroll). The only gate for those is the live app + a real Polar sensor — don't claim a timing/visual fix works from tests alone.
- **Two near-identical `Pipeline.cs`** (`App/` and `Mobile/`). The beat loop and optional-capability wiring are duplicated almost verbatim — a fix to one usually belongs in both.
- **Detection defaults live in `Core/Detection/DetectionThresholds.cs`** and are the runtime values (both heads construct `new()`). Notably `UseLfHfCorroboration = true` (on by default). Baseline tuning is expressed in **memory-window minutes** (`BaselineTuning`) and converted to a per-sample EWMA α from the sample cadence in each `Pipeline.ApplyTuning`/`AlphaFromWindow`.
- **Coverage is Core + Mobile only.** App, Ble.Windows, Ble.Apple, and iOS have no automated tests (partly a TFM constraint, partly a DI gap in `App.Pipeline` which `new`s its BLE source). Prefer adding logic to Core/Mobile where it can be tested.
- **Per-region blend modes on the Desktop (ImGui) head — unblocked upstream, not yet wired here.** The `ktsu.ImGui.App` OpenGL renderer used to `throw NotImplementedException` on any draw-command `UserCallback`, so the usual ImGui trick of `ImDrawList.AddCallback` to flip `glBlendFunc` (e.g. additive `SRC_ALPHA, ONE`) crashed rather than blended. That renderer now honors callbacks and exposes a GL-free `ImGuiApp.SetDrawBlendMode(drawList, ImGuiAppBlendMode.Additive/AlphaBlend)`. To use it for additive/glow (e.g. the regulation field), bump the `ktsu.ImGui.App` package reference in `MeltdownMonitor.App.csproj` (currently `2.6.0`) to a release that includes the API, then wrap the glow layers and always restore `AlphaBlend` afterwards. See `docs/regulation-field-blend-modes.md`.
- **iOS SQLite** opens with `MeltdownRepositoryOptions.IosSandbox` (`journal_mode=TRUNCATE`, `fullfsync=ON`) so background BLE writes survive device lock; desktop uses the default profile.
- **Auto-generated — never hand-edit:** `VERSION.md`, `CHANGELOG.md`, `LATEST_CHANGELOG.md`, `LICENSE.md`. Versioning is via commit-message tags (`[major]`/`[minor]`/`[patch]`/`[pre]`). `docs/superpowers/plans` and `specs` are point-in-time records — don't rewrite them.

## Conventions specific to this repo

- The user-facing self check-in labels are the `AnnotationLabel` enum: **Fine, Edged, Escalating, Blown, Shutdown** (`Core/Persistence/AnnotationLabel.cs`). The first four are the hyperarousal escalation axis; `Shutdown` is the low-arousal/collapse edge (append-only — labels persist as case-insensitive strings). Both check-in UIs auto-enumerate the enum.
- A repo-root `Directory.Build.props` sets `TreatWarningsAsErrors=true` and centralizes `Nullable`/`ImplicitUsings` for every project (`TargetFramework` stays per-project — it varies). The per-project `Nullable`/`ImplicitUsings` copies in the 7 csproj files are now redundant and can be removed. Warnings-as-errors is verified on the 5 non-iOS projects; the `net10.0-ios` projects (`Ble.Apple`, `iOS`) build only on macOS/CI, where the gate may surface pre-existing warnings — check `ios.yml` there.
