using MeltdownMonitor.Core.Diagnostics;

namespace MeltdownMonitor.Tests;

/// <summary>
/// Covers DSN resolution and the disabled-by-default behaviour of
/// <see cref="CrashReporting"/>. Tests that touch the environment variable run
/// serially (and restore it) so a CI-set value can't make them flaky.
/// </summary>
[TestClass]
[DoNotParallelize]
public class CrashReportingTests
{
	private string? _savedEnv;

	[TestInitialize]
	public void SaveEnv() =>
		_savedEnv = Environment.GetEnvironmentVariable(CrashReporting.DsnEnvironmentVariable);

	[TestCleanup]
	public void RestoreEnv() =>
		Environment.SetEnvironmentVariable(CrashReporting.DsnEnvironmentVariable, _savedEnv);

	[TestMethod]
	public void ResolveDsn_PrefersConfiguredValue_AndTrims()
	{
		Environment.SetEnvironmentVariable(CrashReporting.DsnEnvironmentVariable, "https://env@example/9");

		string? resolved = CrashReporting.ResolveDsn("  https://configured@example/1  ");

		Assert.AreEqual("https://configured@example/1", resolved);
	}

	[TestMethod]
	public void ResolveDsn_FallsBackToEnvironmentVariable_WhenConfiguredBlank()
	{
		Environment.SetEnvironmentVariable(CrashReporting.DsnEnvironmentVariable, "https://env@example/9");

		Assert.AreEqual("https://env@example/9", CrashReporting.ResolveDsn(null));
		Assert.AreEqual("https://env@example/9", CrashReporting.ResolveDsn("   "));
	}

	[TestMethod]
	public void ResolveDsn_ReturnsNull_WhenNothingConfigured()
	{
		Environment.SetEnvironmentVariable(CrashReporting.DsnEnvironmentVariable, null);

		Assert.IsNull(CrashReporting.ResolveDsn(null));
		Assert.IsNull(CrashReporting.ResolveDsn(""));
	}

	[TestMethod]
	public void ResolveDsn_FallsBackToEmbedded_WhenConfiguredAndEnvBlank()
	{
		Environment.SetEnvironmentVariable(CrashReporting.DsnEnvironmentVariable, null);

		string? resolved = CrashReporting.ResolveDsn(null, "  https://embedded@example/3  ");

		Assert.AreEqual("https://embedded@example/3", resolved);
	}

	[TestMethod]
	public void ResolveDsn_EnvVarBeatsEmbedded()
	{
		Environment.SetEnvironmentVariable(CrashReporting.DsnEnvironmentVariable, "https://env@example/9");

		Assert.AreEqual("https://env@example/9", CrashReporting.ResolveDsn(null, "https://embedded@example/3"));
	}

	[TestMethod]
	public void ResolveDsn_ConfiguredBeatsEnvAndEmbedded()
	{
		Environment.SetEnvironmentVariable(CrashReporting.DsnEnvironmentVariable, "https://env@example/9");

		Assert.AreEqual(
			"https://configured@example/1",
			CrashReporting.ResolveDsn("https://configured@example/1", "https://embedded@example/3"));
	}

	[TestMethod]
	public void ResolveDsn_ReturnsNull_WhenAllSourcesBlank()
	{
		Environment.SetEnvironmentVariable(CrashReporting.DsnEnvironmentVariable, null);

		Assert.IsNull(CrashReporting.ResolveDsn(null, null));
		Assert.IsNull(CrashReporting.ResolveDsn("", "   "));
	}

	[TestMethod]
	public void Initialize_ReturnsNull_WhenNoDsnConfigured()
	{
		Environment.SetEnvironmentVariable(CrashReporting.DsnEnvironmentVariable, null);

		// No DSN anywhere → crash reporting stays off and the SDK is never started.
		IDisposable? handle = CrashReporting.Initialize(new CrashReportingOptions());

		Assert.IsNull(handle);
	}

	[TestMethod]
	public void Initialize_Throws_OnNullOptions() =>
		Assert.ThrowsExactly<ArgumentNullException>(() => CrashReporting.Initialize(null!));
}
