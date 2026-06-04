# Crash reporting (self-hosted GlitchTip)

MeltdownMonitor reports unhandled exceptions to a **self-hosted [GlitchTip](https://glitchtip.com/)**
instance running in the homelab cluster. GlitchTip is Sentry-API compatible, so the
integration uses the standard **Sentry .NET SDK** (`Sentry`, referenced from `Core`).

## How it's wired

- **`Core/Diagnostics/CrashReporting.cs`** — the single, platform-neutral entry point.
  `CrashReporting.Initialize(options)` starts the SDK (which hooks
  `AppDomain.UnhandledException` and unobserved-task exceptions) and returns an
  `IDisposable` to dispose on shutdown so queued events flush. It returns **null when no
  DSN is configured**, so the app runs exactly as before — no SDK, no network.
- **Desktop (`App/Program.cs`)** initializes it right after settings load, with
  `Environment = "windows-desktop"`.
- **iOS (`Program.Main` → `IosCompositionRoot.InitializeCrashReporting`)** initializes it
  as the very first thing in `Main`, *before* UIKit/Avalonia and the audio-session setup
  spin up, with `Environment = "ios"`. This is deliberate: the SDK used to come up only
  when Avalonia invoked the root-view-model factory in `OnFrameworkInitializationCompleted`,
  so any crash during the launch window (Avalonia bootstrap, the composition root, trimming/AOT
  faults) happened *before Sentry loaded* and was never reported. `Main` also wraps
  `UIApplication.Main` to capture and synchronously flush any managed exception that escapes
  the run loop. The SDK handle is held in a static field for the app's lifetime; the
  composition root's later init call is idempotent and still honours the settings DSN.

Because the logic lives in `Core`, DSN resolution is unit-tested in `Tests`
(`CrashReportingTests`); the heads only pass options.

## Configuring the DSN

The DSN is resolved in this order:

1. The per-head setting — `AppSettings.CrashReportingDsn` (desktop) /
   `MobileSettings.CrashReportingDsn` (mobile).
2. The `MELTDOWN_CRASH_REPORTING_DSN` environment variable (handy for ops/runtime).
3. The DSN baked into the build (see below) — for distributed binaries.
4. Otherwise crash reporting is **off**.

To get a DSN: deploy GlitchTip (`make apply-glitchtip` in the homelab repo), create an
organization/user via its web UI, create a project, and copy that project's DSN.

## Injecting the DSN at build time

For shipped binaries (where there's no runtime env var to set on a user's device), the DSN
can be embedded at build time. The repo-root `Directory.Build.props` stamps it into assembly
metadata (`MeltdownMonitor.CrashReportingDsn`) whenever it's supplied, and
`CrashReporting.ResolveDsn` reads it from the entry assembly as the lowest-priority fallback.

Supply it either way:

```sh
# As an MSBuild property
dotnet publish MeltdownMonitor.App -c Release -p:CrashReportingDsn="https://…@glitchtip…/1"

# …or via the environment variable (Directory.Build.props picks it up automatically)
MELTDOWN_CRASH_REPORTING_DSN="https://…@glitchtip…/1" dotnet publish MeltdownMonitor.App -c Release
```

**CI:** store the DSN as the `MELTDOWN_CRASH_REPORTING_DSN` repo secret. The `.NET Workflow`
and `iOS Workflow` expose it as an env var, so release builds embed it automatically. Builds
without the secret (forks, dev machines) embed nothing and ship with crash reporting off.

The DSN is write-only (it can submit events, not read them), so this is acceptable to bake
into a distributed binary; keeping it in a repo secret rather than committed source just
avoids casual scraping. Rotate it in GlitchTip if it's ever abused.

## Privacy

The app handles sensitive physiological data, so crash reporting is **opt-in** (no DSN →
off) and `SendDefaultPii` defaults to **false** — no usernames or request bodies are
attached to events. Keep it that way unless there is a deliberate reason to change it.
