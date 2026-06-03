using Sentry;

namespace MeltdownMonitor.Core.Diagnostics;

/// <summary>
/// Configuration for <see cref="CrashReporting.Initialize"/>. A null/blank
/// <see cref="Dsn"/> means crash reporting is disabled — the app runs exactly
/// as before, with no network calls and no SDK overhead.
/// </summary>
public sealed record CrashReportingOptions
{
	/// <summary>
	/// The Sentry/GlitchTip DSN. When null or blank, <see cref="CrashReporting.ResolveDsn"/>
	/// falls back to the <see cref="CrashReporting.DsnEnvironmentVariable"/> environment
	/// variable; if that is also unset, crash reporting stays off.
	/// </summary>
	public string? Dsn { get; init; }

	/// <summary>Logical deployment environment shown in GlitchTip (e.g. "windows-desktop", "ios").</summary>
	public string Environment { get; init; } = "production";

	/// <summary>Release/version string used to group regressions. Null lets the SDK infer it.</summary>
	public string? Release { get; init; }

	/// <summary>
	/// Whether to attach personally identifiable information (usernames, request data) to
	/// events. Defaults to <c>false</c>: MeltdownMonitor handles sensitive physiological data,
	/// so crash reports stay opt-in and minimal by design.
	/// </summary>
	public bool SendDefaultPii { get; init; }
}

/// <summary>
/// Thin, platform-neutral wrapper over the Sentry .NET SDK that reports
/// unhandled exceptions to the self-hosted GlitchTip instance. Lives in Core so
/// every head (desktop, mobile, iOS) initializes crash reporting the same way
/// and the resolution logic stays unit-testable.
/// </summary>
public static class CrashReporting
{
	/// <summary>
	/// Environment variable consulted when no DSN is passed explicitly. Lets ops point a
	/// build at GlitchTip without baking the DSN into committed settings.
	/// </summary>
	public const string DsnEnvironmentVariable = "MELTDOWN_CRASH_REPORTING_DSN";

	/// <summary>
	/// Resolves the effective DSN: the explicitly configured value if present, otherwise the
	/// <see cref="DsnEnvironmentVariable"/> environment variable, otherwise null (disabled).
	/// </summary>
	public static string? ResolveDsn(string? configuredDsn)
	{
		if (!string.IsNullOrWhiteSpace(configuredDsn))
		{
			return configuredDsn.Trim();
		}

		string? fromEnv = System.Environment.GetEnvironmentVariable(DsnEnvironmentVariable);
		return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv.Trim();
	}

	/// <summary>
	/// Initializes the Sentry SDK against the resolved DSN, hooking unhandled-exception and
	/// unobserved-task capture. Returns the SDK handle to dispose on shutdown (which flushes
	/// any queued events), or <c>null</c> when no DSN is configured — in which case crash
	/// reporting is simply off and the caller's <c>using</c> is a harmless no-op.
	/// </summary>
	public static IDisposable? Initialize(CrashReportingOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		string? dsn = ResolveDsn(options.Dsn);
		if (dsn is null)
		{
			return null;
		}

		return SentrySdk.Init(o =>
		{
			o.Dsn = dsn;
			o.Environment = options.Environment;
			o.SendDefaultPii = options.SendDefaultPii;
			o.AutoSessionTracking = true;

			if (!string.IsNullOrWhiteSpace(options.Release))
			{
				o.Release = options.Release;
			}
		});
	}

	/// <summary>
	/// Manually reports an exception that was caught and handled but is still worth recording.
	/// No-ops when the SDK was never initialized (no DSN configured).
	/// </summary>
	public static void CaptureException(Exception exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		SentrySdk.CaptureException(exception);
	}
}
