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
- **iOS (`IosCompositionRoot.BuildRootViewModel`)** initializes it once settings are
  loaded, with `Environment = "ios"`. The SDK handle is held in a static field for the
  app's lifetime.

Because the logic lives in `Core`, DSN resolution is unit-tested in `Tests`
(`CrashReportingTests`); the heads only pass options.

## Configuring the DSN

The DSN is resolved in this order:

1. The per-head setting — `AppSettings.CrashReportingDsn` (desktop) /
   `MobileSettings.CrashReportingDsn` (mobile).
2. The `MELTDOWN_CRASH_REPORTING_DSN` environment variable (fallback, handy for CI/ops
   without baking the DSN into committed settings).
3. Otherwise crash reporting is **off**.

To get a DSN: deploy GlitchTip (`make apply-glitchtip` in the homelab repo), create an
organization/user via its web UI, create a project, and copy that project's DSN.

## Privacy

The app handles sensitive physiological data, so crash reporting is **opt-in** (no DSN →
off) and `SendDefaultPii` defaults to **false** — no usernames or request bodies are
attached to events. Keep it that way unless there is a deliberate reason to change it.
