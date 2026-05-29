# Historical Baseline Seeding & Long-Term Anchor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Seed `BaselineHrvTracker` from persisted history at launch (warm start) and bound the fast EWMA with a long-term personalised anchor (guardrail).

**Architecture:** `BaselineHrvTracker` (Core) gains a DB-agnostic `SeedFromHistory(IReadOnlyList<HrvSample>)` that computes robust (median) anchor and warm-start values, plus an anchor clamp applied after each EWMA update. `MeltdownMonitor.App.Pipeline.Start()` reads the last 7 days via `MeltdownRepository.ReadHistory` and feeds them in (best-effort). The guardrail is dormant when no anchor is set, so existing cold-start behaviour, the Mobile `WarmStartAsync`, and current tests are unaffected.

**Tech Stack:** C# / .NET 10, MSTest. Tabs, file-scoped namespaces, no `this.`, braces always (repo conventions).

Spec: `docs/superpowers/specs/2026-05-29-historical-baseline-seeding-design.md`

---

## File Structure

- **Modify** `MeltdownMonitor.Core/Baseline/BaselineHrvTracker.cs` — anchor fields, tunable constants, `SeedFromHistory`, `Median` helper, guardrail clamp in `Update`, `Reset` updates.
- **Create** `MeltdownMonitor.Tests/BaselineSeedingTests.cs` — unit tests for seeding + guardrail.
- **Modify** `MeltdownMonitor.App/Pipeline.cs` — `Start()` reads 7 days of history and calls `SeedFromHistory` (best-effort).

---

## Task 1: Seeding (anchor + warm-start medians) on `BaselineHrvTracker`

**Files:**
- Modify: `MeltdownMonitor.Core/Baseline/BaselineHrvTracker.cs`
- Test: `MeltdownMonitor.Tests/BaselineSeedingTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `MeltdownMonitor.Tests/BaselineSeedingTests.cs`:

```csharp
using MeltdownMonitor.Core.Baseline;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class BaselineSeedingTests
{
	private static HrvSample Sample(
		double rmssd,
		double hr,
		DateTimeOffset ts,
		DetectorState state = DetectorState.Watching,
		double? lfHf = null)
	{
		var sample = new HrvSample(ts, rmssd, Pnn50: 20, hr, BaselineRmssd: 0, BaselineHr: 0, state);
		if (lfHf is { } v)
		{
			sample = sample with
			{
				Extended = new ExtendedHrvMetrics(
					LfPowerMs2: 0, HfPowerMs2: 0, LfHfRatio: v,
					SD1: 0, SD2: 0, SD1SD2Ratio: 0, Sdnn: 0)
			};
		}

		return sample;
	}

	// 20 recent clean samples with RMSSD 40..59 (median 49.5) and HR 60..79 (median 69.5).
	private static List<HrvSample> RecentClean()
	{
		var now = DateTimeOffset.UtcNow;
		var list = new List<HrvSample>();
		for (int i = 0; i < 20; i++)
		{
			list.Add(Sample(40 + i, 60 + i, now.AddMinutes(-30).AddSeconds(i), lfHf: 1.0 + (i * 0.1)));
		}

		return list;
	}

	[TestMethod]
	public void SeedFromHistory_WarmStarts_AndSeedsMedian()
	{
		var tracker = new BaselineHrvTracker();
		tracker.SeedFromHistory(RecentClean());

		Assert.IsTrue(tracker.IsWarm, "Enough recent clean samples should warm-start the tracker.");
		Assert.AreEqual(49.5, tracker.BaselineRmssd, 0.001);
		Assert.AreEqual(69.5, tracker.BaselineHr, 0.001);
	}

	[TestMethod]
	public void SeedFromHistory_ExcludesEpisodeSamples_FromMedian()
	{
		var samples = RecentClean();
		var now = DateTimeOffset.UtcNow;
		// Inject extreme dysregulated samples that would wreck a mean/median if counted.
		samples.Add(Sample(1, 200, now.AddMinutes(-5), DetectorState.Alerting));
		samples.Add(Sample(1, 200, now.AddMinutes(-4), DetectorState.Warning));

		var tracker = new BaselineHrvTracker();
		tracker.SeedFromHistory(samples);

		Assert.AreEqual(49.5, tracker.BaselineRmssd, 0.001);
		Assert.AreEqual(69.5, tracker.BaselineHr, 0.001);
	}

	[TestMethod]
	public void SeedFromHistory_StaleHistory_StaysCold()
	{
		var old = DateTimeOffset.UtcNow.AddHours(-3);
		var samples = new List<HrvSample>();
		for (int i = 0; i < 50; i++)
		{
			samples.Add(Sample(50, 70, old.AddSeconds(i)));
		}

		var tracker = new BaselineHrvTracker();
		tracker.SeedFromHistory(samples);

		Assert.IsFalse(tracker.IsWarm, "History older than the warm-start window must not warm-start.");
		Assert.AreEqual(0, tracker.BaselineRmssd, 0.001, "Stale history does not seed the live EWMA.");
	}

	[TestMethod]
	public void SeedFromHistory_NoHistory_IsNoOp()
	{
		var tracker = new BaselineHrvTracker();
		tracker.SeedFromHistory([]);

		Assert.IsFalse(tracker.IsWarm);
		Assert.AreEqual(0, tracker.BaselineRmssd, 0.001);
	}
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test MeltdownMonitor.Tests --filter "FullyQualifiedName~BaselineSeedingTests"`
Expected: FAIL to compile — `BaselineHrvTracker` has no `SeedFromHistory`.

- [ ] **Step 3: Implement constants, anchor fields, `Median`, and `SeedFromHistory`**

In `MeltdownMonitor.Core/Baseline/BaselineHrvTracker.cs`, add `using System.Linq;` if not already present (the file currently has none — add it under the existing usings).

Add the constants next to the existing ones (after `WarmUpMinutes`):

```csharp
	/// <summary>Look-back window (days) the owner should read for the anchor median.</summary>
	public const int AnchorWindowDays = 7;
	// Recent window whose median seeds the live EWMA at startup.
	private const double WarmStartWindowMinutes = 60.0;
	// Minimum recent clean samples required to warm-start (skip the live warm-up).
	private const int MinWarmStartSamples = 12;
	// Guardrail: the live baseline may not drift more than this fraction from the anchor.
	private const double MaxAnchorDrift = 0.40;
```

Add the anchor fields next to the existing private fields (after `_isWarm`):

```csharp
	private double _anchorRmssd;
	private double _anchorHr;
	private double _anchorLfHfRatio;
```

Add the seeding method and median helper (place after `Update`, before `Reset`):

```csharp
	/// <summary>
	/// Seeds the baseline from persisted history: a robust (median) long-term anchor
	/// over the whole supplied window, and a warm-start of the live EWMA from the most
	/// recent hour. Clean samples only (no Warning/Alerting states, positive values).
	/// Safe to call once before live samples flow; a no-op when no usable history exists.
	/// </summary>
	public void SeedFromHistory(IReadOnlyList<HrvSample> history)
	{
		List<HrvSample> clean = [.. history.Where(IsClean)];

		_anchorRmssd = Median([.. clean.Where(s => s.Rmssd > 0).Select(s => s.Rmssd)]);
		_anchorHr = Median([.. clean.Where(s => s.MeanHr > 0).Select(s => s.MeanHr)]);
		_anchorLfHfRatio = Median([.. clean.Where(s => s.Extended is { LfHfRatio: > 0 })
			.Select(s => s.Extended!.LfHfRatio)]);

		DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-WarmStartWindowMinutes);
		List<HrvSample> recent = [.. clean.Where(s => s.Timestamp >= cutoff)];
		List<double> recentRmssd = [.. recent.Where(s => s.Rmssd > 0).Select(s => s.Rmssd)];
		List<double> recentHr = [.. recent.Where(s => s.MeanHr > 0).Select(s => s.MeanHr)];

		if (recentRmssd.Count < MinWarmStartSamples || recentHr.Count < MinWarmStartSamples)
		{
			// Not enough recent data to trust a warm start; anchor (if any) still guards
			// the live warm-up that follows.
			return;
		}

		_baselineRmssd = Median(recentRmssd);
		_baselineHr = Median(recentHr);

		List<double> recentLfHf = [.. recent.Where(s => s.Extended is { LfHfRatio: > 0 })
			.Select(s => s.Extended!.LfHfRatio)];
		if (recentLfHf.Count > 0)
		{
			_baselineLfHfRatio = Median(recentLfHf);
		}

		_firstSampleTime = recent.Max(s => s.Timestamp);
		_isWarm = true;
	}

	private static bool IsClean(HrvSample sample) =>
		sample.State is not (DetectorState.Warning or DetectorState.Alerting);

	private static double Median(IReadOnlyList<double> values)
	{
		if (values.Count == 0)
		{
			return 0;
		}

		double[] sorted = [.. values.OrderBy(v => v)];
		int mid = sorted.Length / 2;
		return (sorted.Length % 2 == 0)
			? (sorted[mid - 1] + sorted[mid]) / 2.0
			: sorted[mid];
	}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test MeltdownMonitor.Tests --filter "FullyQualifiedName~BaselineSeedingTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Core/Baseline/BaselineHrvTracker.cs MeltdownMonitor.Tests/BaselineSeedingTests.cs
git commit -m "feat: seed HRV baseline from persisted history (warm-start + anchor)"
```

---

## Task 2: Anchor guardrail clamp in `Update` + `Reset`

**Files:**
- Modify: `MeltdownMonitor.Core/Baseline/BaselineHrvTracker.cs`
- Test: `MeltdownMonitor.Tests/BaselineSeedingTests.cs`

- [ ] **Step 1: Write the failing tests**

Append these methods to `BaselineSeedingTests`:

```csharp
	[TestMethod]
	public void Guardrail_PreventsBaselineDriftingBelowAnchorBand()
	{
		// Warm-start with RMSSD median 49.5, HR median 69.5 (anchor == warm-start here).
		var tracker = new BaselineHrvTracker();
		tracker.SeedFromHistory(RecentClean());

		// Feed a long run of crushed RMSSD; the EWMA would head toward 5 but the
		// guardrail floors it at anchor * (1 - 0.40) = 49.5 * 0.6 = 29.7.
		var now = DateTimeOffset.UtcNow;
		for (int i = 0; i < 2000; i++)
		{
			tracker.Update(Sample(5, 69.5, now.AddSeconds(i)));
		}

		Assert.AreEqual(29.7, tracker.BaselineRmssd, 0.1,
			"Baseline must not drop below 40% under the anchor.");
	}

	[TestMethod]
	public void Guardrail_PreventsBaselineDriftingAboveAnchorBand()
	{
		var tracker = new BaselineHrvTracker();
		tracker.SeedFromHistory(RecentClean());

		var now = DateTimeOffset.UtcNow;
		for (int i = 0; i < 2000; i++)
		{
			tracker.Update(Sample(49.5, 200, now.AddSeconds(i)));
		}

		// HR ceiling = anchor 69.5 * 1.40 = 97.3.
		Assert.AreEqual(97.3, tracker.BaselineHr, 0.1,
			"Baseline HR must not rise above 40% over the anchor.");
	}

	[TestMethod]
	public void Guardrail_NoAnchor_DoesNotClamp()
	{
		// No seeding => no anchor => behaves exactly like cold start.
		var tracker = new BaselineHrvTracker();
		tracker.Update(Sample(5, 70, DateTimeOffset.UtcNow));

		Assert.AreEqual(5, tracker.BaselineRmssd, 0.001);
	}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test MeltdownMonitor.Tests --filter "FullyQualifiedName~Guardrail"`
Expected: FAIL — the two clamp tests fail (baseline drifts past the band); `Guardrail_NoAnchor_DoesNotClamp` already passes.

- [ ] **Step 3: Implement the clamp**

In `Update`, after the LF/HF baseline block and **before** the warm-up check (`if (!_isWarm ...)`), add:

```csharp
		ClampToAnchor();
```

Then add the method (place after `SeedFromHistory`/`Median`):

```csharp
	// Keep the live EWMA within +/-MaxAnchorDrift of the personalised anchor so a long
	// sub-threshold rough patch cannot silently re-normalise the baseline. No-op until
	// an anchor has been seeded.
	private void ClampToAnchor()
	{
		if (_anchorRmssd > 0)
		{
			_baselineRmssd = Math.Clamp(_baselineRmssd,
				_anchorRmssd * (1.0 - MaxAnchorDrift), _anchorRmssd * (1.0 + MaxAnchorDrift));
		}

		if (_anchorHr > 0)
		{
			_baselineHr = Math.Clamp(_baselineHr,
				_anchorHr * (1.0 - MaxAnchorDrift), _anchorHr * (1.0 + MaxAnchorDrift));
		}

		if (_anchorLfHfRatio > 0)
		{
			_baselineLfHfRatio = Math.Clamp(_baselineLfHfRatio,
				_anchorLfHfRatio * (1.0 - MaxAnchorDrift), _anchorLfHfRatio * (1.0 + MaxAnchorDrift));
		}
	}
```

Update `Reset` to clear the anchor (add these three lines alongside the existing resets):

```csharp
		_anchorRmssd = 0;
		_anchorHr = 0;
		_anchorLfHfRatio = 0;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test MeltdownMonitor.Tests --filter "FullyQualifiedName~BaselineSeedingTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Run the full Core test suite for regressions**

Run: `dotnet test MeltdownMonitor.Tests`
Expected: PASS — existing `BaselineTrackerTests` (cold start, EWMA) still green because the clamp is dormant without an anchor.

- [ ] **Step 6: Commit**

```bash
git add MeltdownMonitor.Core/Baseline/BaselineHrvTracker.cs MeltdownMonitor.Tests/BaselineSeedingTests.cs
git commit -m "feat: clamp live HRV baseline to the personalised anchor band"
```

---

## Task 3: Wire `Pipeline.Start()` to seed from the repository

**Files:**
- Modify: `MeltdownMonitor.App/Pipeline.cs`

No unit test: `MeltdownMonitor.App.Pipeline.Start()` launches `RunAsync` which connects to BLE hardware, so it is not unit-testable here. The seeding logic is fully covered by Task 1/2 tracker tests; this task is thin glue verified by build + manual launch.

- [ ] **Step 1: Add the seeding call to `Start()`**

In `MeltdownMonitor.App/Pipeline.cs`, change `Start()` from:

```csharp
	public void Start()
	{
		_cts = new CancellationTokenSource();
		_pipelineTask = RunAsync(_cts.Token);
	}
```

to:

```csharp
	public void Start()
	{
		SeedBaselineFromHistory();
		_cts = new CancellationTokenSource();
		_pipelineTask = RunAsync(_cts.Token);
	}

	// Warm-start the baseline from recent persisted history before live samples flow.
	// Best-effort: a missing or locked database must never prevent startup.
	private void SeedBaselineFromHistory()
	{
		try
		{
			DateTimeOffset to = DateTimeOffset.UtcNow;
			DateTimeOffset from = to.AddDays(-BaselineHrvTracker.AnchorWindowDays);
			var history = MeltdownRepository.ReadHistory(_settings.DatabasePath, from, to);
			_baseline.SeedFromHistory(history);
		}
		catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException
			or IOException or InvalidOperationException)
		{
			System.Diagnostics.Debug.WriteLine($"Baseline seeding skipped: {ex.Message}");
		}
	}
```

(`MeltdownMonitor.Core.Baseline` is already imported in `Pipeline.cs`. If `Microsoft.Data.Sqlite` is not resolvable from the App project, widen the catch to `catch (Exception ex)` with the same Debug line — the App references the Core repository which owns the SQLite dependency.)

- [ ] **Step 2: Build**

Run: `dotnet build MeltdownMonitor.App/MeltdownMonitor.App.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Manual verification**

Launch the app with an existing populated `meltdown.db` containing recent samples. Open the status window (tray) → Overview. Expected: the detector is armed immediately (no 10-minute "warm-up needed" message) and baselines start at personalised values rather than the first live reading. With an empty/missing DB, the app starts exactly as before (live 10-minute warm-up).

- [ ] **Step 4: Commit**

```bash
git add MeltdownMonitor.App/Pipeline.cs
git commit -m "feat: warm-start the Windows pipeline baseline from persisted history"
```

---

## Self-Review

- **Spec coverage:** warm-start seed (Task 1), long-term anchor median (Task 1), guardrail clamp (Task 2), warm-up skip vs fallback (Task 1 — `MinWarmStartSamples`/stale tests), exclude dysregulated samples (Task 1 — episode test), persistence = DB recompute at launch (Task 3), no-history parity (Task 1/2 tests). All covered.
- **Placeholder scan:** none — every step has concrete code/commands.
- **Type consistency:** `SeedFromHistory(IReadOnlyList<HrvSample>)`, `AnchorWindowDays` (public const, used in Task 3), `_anchorRmssd/_anchorHr/_anchorLfHfRatio`, `ClampToAnchor`, `Median`, `IsClean` — names consistent across tasks. `ExtendedHrvMetrics` ctor args match the record definition. `MeltdownRepository.ReadHistory(string, DateTimeOffset, DateTimeOffset)` signature matches.
- **Out of scope (per spec):** time-of-day awareness, periodic in-session anchor recompute, persisted baseline snapshot, Mobile pipeline changes — intentionally excluded.
