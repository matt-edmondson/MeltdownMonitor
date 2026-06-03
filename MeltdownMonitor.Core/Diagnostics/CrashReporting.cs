using System.Reflection;

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
	/// Assembly-metadata key under which a build-time DSN is stamped (see the repo-root
	/// <c>Directory.Build.props</c>). Read from the entry assembly as the lowest-priority
	/// fallback so distributed binaries can ship a DSN without a runtime env/setting.
	/// </summary>
	public const string EmbeddedDsnMetadataKey = "MeltdownMonitor.CrashReportingDsn";

	/// <summary>
	/// Resolves the effective DSN in priority order: the explicitly configured value, then the
	/// <see cref="DsnEnvironmentVariable"/> environment variable, then the value baked into the
	/// entry assembly at build time, otherwise null (disabled).
	/// </summary>
	public static string? ResolveDsn(string? configuredDsn) =>
		ResolveDsn(configuredDsn, ReadEmbeddedDsn());

	/// <summary>
	/// Resolution core with the build-time embedded DSN passed explicitly, kept public so the
	/// precedence (configured &gt; environment variable &gt; embedded) is unit-testable without
	/// depending on the test host's entry assembly.
	/// </summary>
	public static string? ResolveDsn(string? configuredDsn, string? embeddedDsn)
	{
		if (!string.IsNullOrWhiteSpace(configuredDsn))
		{
			return configuredDsn.Trim();
		}

		string? fromEnv = System.Environment.GetEnvironmentVariable(DsnEnvironmentVariable);
		if (!string.IsNullOrWhiteSpace(fromEnv))
		{
			return fromEnv.Trim();
		}

		return string.IsNullOrWhiteSpace(embeddedDsn) ? null : embeddedDsn.Trim();
	}

	/// <summary>
	/// Reads the build-time DSN from the entry assembly's <see cref="AssemblyMetadataAttribute"/>,
	/// or null when none was stamped in (the common dev/local case).
	/// </summary>
	private static string? ReadEmbeddedDsn() =>
		Assembly.GetEntryAssembly()?
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(a => a.Key == EmbeddedDsnMetadataKey)?
			.Value;

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
