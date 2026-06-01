# Persisted sensor-contact + Overview graph — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist sensor contact at the HRV-sample cadence and add a 0/1 "contact OK" step-strip to the desktop Overview tab.

**Architecture:** Add an init-only `SensorContact` to `HrvSample`, persisted as a new `contact` TEXT column on `hrv_samples` via the repository's existing additive-migration path; both pipelines stamp `LatestContact` onto the persisted sample; the desktop `StatusWindow` adds a contact sparkline (1 = trustworthy, 0 = NotDetected) fed live and backfilled like the others.

**Tech Stack:** C# / .NET 10, Microsoft.Data.Sqlite, MSTest, Dear ImGui (desktop).

**Spec:** `docs/superpowers/specs/2026-06-01-persisted-contact-graph-design.md`
**Branch:** `claude/contact-graph` (already created off `main`; spec commit is on it).

**Build reality:** Core / Mobile / Tests build & test locally (full TDD). App (`net10.0-windows`, `ktsu.ImGui.App 2.6.0` unpublished → `NU1102`) and iOS (`net10.0-ios`, macOS only) CANNOT build locally — those edits are verified by inspection + CI. Do NOT run `dotnet build` on App/iOS/solution.

---

## File structure
- `MeltdownMonitor.Core/Hrv/HrvSample.cs` — add `SensorContact` init property (Task 1).
- `MeltdownMonitor.Core/Persistence/MeltdownRepository.cs` — migrate + insert + both reads (Task 2).
- `MeltdownMonitor.App/Pipeline.cs` — stamp `SensorContact = LatestContact` (Task 3).
- `MeltdownMonitor.Mobile/Pipeline.cs` — same stamp (Task 3).
- `MeltdownMonitor.App/StatusWindow.cs` — contact sparkline buffer/feed/backfill/render (Task 4).
- Tests: `MeltdownMonitor.Tests/HrvSampleTests.cs` (create, Task 1); `MeltdownMonitor.Tests/MeltdownRepositoryContactTests.cs` (create, Task 2).

---

### Task 1: `HrvSample.SensorContact` init property (TDD)

**Files:**
- Modify: `MeltdownMonitor.Core/Hrv/HrvSample.cs`
- Test: `MeltdownMonitor.Tests/HrvSampleTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `MeltdownMonitor.Tests/HrvSampleTests.cs`:

```csharp
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class HrvSampleTests
{
	private static HrvSample Make() => new(
		Timestamp: DateTimeOffset.UnixEpoch,
		Rmssd: 40, Pnn50: 10, MeanHr: 70,
		BaselineRmssd: 45, BaselineHr: 68,
		State: DetectorState.Watching);

	[TestMethod]
	public void SensorContact_DefaultsToNotSupported()
	{
		Assert.AreEqual(SensorContactStatus.NotSupported, Make().SensorContact);
	}

	[TestMethod]
	public void SensorContact_RoundTripsViaInit()
	{
		var s = Make() with { SensorContact = SensorContactStatus.NotDetected };
		Assert.AreEqual(SensorContactStatus.NotDetected, s.SensorContact);
	}
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~HrvSampleTests"`
Expected: FAIL to compile — `HrvSample` has no `SensorContact`.

- [ ] **Step 3: Add the property**

`HrvSample.cs` is a positional record `public record HrvSample(DateTimeOffset Timestamp, double Rmssd, double Pnn50, double MeanHr, double BaselineRmssd, double BaselineHr, DetectorState State)` with init-only `Extended` and `BaselineLfHfRatio` already declared in its body. Add `using MeltdownMonitor.Core.Beats;` at the top if not present (for `SensorContactStatus`), and add this init property in the body alongside `Extended`/`BaselineLfHfRatio`:

```csharp
	/// <summary>Sensor skin/electrode contact at this sample's moment. Default
	/// <see cref="SensorContactStatus.NotSupported"/> (sensor not reporting contact).</summary>
	public SensorContactStatus SensorContact { get; init; } = SensorContactStatus.NotSupported;
```

(Confirm the `MeltdownMonitor.Core.Beats` namespace holds `SensorContactStatus` — it does; `Beat`/`SensorContactStatus` live there. `HrvSample.cs` already imports `MeltdownMonitor.Core.Detection` for `DetectorState`.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~HrvSampleTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Core/Hrv/HrvSample.cs MeltdownMonitor.Tests/HrvSampleTests.cs
git commit -m "feat: add SensorContact to HrvSample (init-only, defaults NotSupported)"
```

---

### Task 2: Persist `contact` on `hrv_samples` (TDD)

**Files:**
- Modify: `MeltdownMonitor.Core/Persistence/MeltdownRepository.cs`
- Test: `MeltdownMonitor.Tests/MeltdownRepositoryContactTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Create `MeltdownMonitor.Tests/MeltdownRepositoryContactTests.cs`:

```csharp
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MeltdownRepositoryContactTests
{
	private static HrvSample Sample(DateTimeOffset ts, SensorContactStatus contact) => new(
		Timestamp: ts,
		Rmssd: 40, Pnn50: 10, MeanHr: 70,
		BaselineRmssd: 45, BaselineHr: 68,
		State: DetectorState.Watching)
	{
		SensorContact = contact,
	};

	[TestMethod]
	public void InsertThenGet_RoundTripsContact()
	{
		var path = NewTempDbPath();
		var ts = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
		try
		{
			using var repo = new MeltdownRepository(path);
			repo.InsertHrvSample(Sample(ts, SensorContactStatus.NotDetected));

			var read = repo.GetHrvSamples(ts.AddMinutes(-1), ts.AddMinutes(1));

			Assert.AreEqual(1, read.Count);
			Assert.AreEqual(SensorContactStatus.NotDetected, read[0].SensorContact);
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void InsertThenReadHistory_RoundTripsContact()
	{
		var path = NewTempDbPath();
		var ts = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
		try
		{
			using (var repo = new MeltdownRepository(path))
			{
				repo.InsertHrvSample(Sample(ts, SensorContactStatus.Detected));
			}

			var read = MeltdownRepository.ReadHistory(path, ts.AddMinutes(-1), ts.AddMinutes(1));

			Assert.AreEqual(1, read.Count);
			Assert.AreEqual(SensorContactStatus.Detected, read[0].SensorContact);
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void DefaultSample_ReadsBackAsNotSupported()
	{
		var path = NewTempDbPath();
		var ts = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
		try
		{
			using var repo = new MeltdownRepository(path);
			// A sample whose contact was never set persists/reads as NotSupported.
			repo.InsertHrvSample(new HrvSample(ts, 40, 10, 70, 45, 68, DetectorState.Watching));

			var read = repo.GetHrvSamples(ts.AddMinutes(-1), ts.AddMinutes(1));

			Assert.AreEqual(1, read.Count);
			Assert.AreEqual(SensorContactStatus.NotSupported, read[0].SensorContact);
		}
		finally
		{
			TryDelete(path);
		}
	}

	private static string NewTempDbPath() =>
		Path.Combine(Path.GetTempPath(), $"mm-{Guid.NewGuid():N}.db");

	private static void TryDelete(string path)
	{
		foreach (var f in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
		{
			try { File.Delete(f); } catch { /* best-effort temp cleanup */ }
		}
	}
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~MeltdownRepositoryContactTests"`
Expected: FAIL — contact is not persisted/read, so `read[0].SensorContact` is `NotSupported` for the NotDetected/Detected cases (the first two tests fail; the default test happens to pass).

- [ ] **Step 3: Add the migration column**

In `MeltdownRepository.MigrateHrvSamples`, add `contact` to the `toAdd` array (after `("sdnn", "REAL")`):

```csharp
			("sdnn",           "REAL"),
			("contact",        "TEXT"),
```

- [ ] **Step 4: Write the column in `InsertHrvSample`**

`InsertHrvSample` builds an `INSERT OR IGNORE INTO hrv_samples (ts, rmssd, pnn50, mean_hr, baseline_rmssd, baseline_hr, state, lf_power_ms2, hf_power_ms2, lf_hf_ratio, sd1, sd2, sd1_sd2_ratio, sdnn) VALUES (...)`. Add `contact` to the column list and a `$contact` value, and bind it. Change the column list and VALUES to include `contact` / `$contact` as the final entry:

```csharp
				cmd.CommandText = """
					INSERT OR IGNORE INTO hrv_samples (
						ts, rmssd, pnn50, mean_hr, baseline_rmssd, baseline_hr, state,
						lf_power_ms2, hf_power_ms2, lf_hf_ratio, sd1, sd2, sd1_sd2_ratio, sdnn, contact)
					VALUES (
						$ts, $rmssd, $pnn50, $mean_hr, $baseline_rmssd, $baseline_hr, $state,
						$lf, $hf, $lf_hf, $sd1, $sd2, $sd1_sd2, $sdnn, $contact)
					""";
```

and add the parameter binding alongside the existing `$state` binding (`sample.State.ToString()` pattern):

```csharp
				cmd.Parameters.AddWithValue("$contact", sample.SensorContact.ToString());
```

(Keep every existing column, parameter, and the `$lf`..`$sdnn` DBNull handling exactly as-is — only `contact`/`$contact` is added.)

- [ ] **Step 5: Read the column in BOTH `GetHrvSamples` and `ReadHistory`**

Both methods use the SELECT `SELECT ts, rmssd, pnn50, mean_hr, baseline_rmssd, baseline_hr, state, lf_power_ms2, hf_power_ms2, lf_hf_ratio, sd1, sd2, sd1_sd2_ratio, sdnn FROM hrv_samples WHERE ts >= $from AND ts <= $to ORDER BY ts` (columns 0–13), then construct `new HrvSample(ts, reader.GetDouble(1), ... state) { Extended = ext }`. In **each** method:

(a) Append `, contact` to the SELECT column list (it becomes column index 14):

```csharp
				cmd.CommandText = """
					SELECT ts, rmssd, pnn50, mean_hr, baseline_rmssd, baseline_hr, state,
					       lf_power_ms2, hf_power_ms2, lf_hf_ratio, sd1, sd2, sd1_sd2_ratio, sdnn, contact
					FROM hrv_samples
					WHERE ts >= $from AND ts <= $to
					ORDER BY ts
					""";
```

(b) Parse contact (defensive NULL/unknown → NotSupported) just before constructing the sample, and set it via the object initializer. Where the method currently does `results.Add(new HrvSample(ts, ...state) { Extended = ext });`, change to:

```csharp
				var contact = reader.IsDBNull(14)
					? SensorContactStatus.NotSupported
					: Enum.TryParse<SensorContactStatus>(reader.GetString(14), ignoreCase: true, out var c)
						? c
						: SensorContactStatus.NotSupported;

				results.Add(new HrvSample(ts,
					reader.GetDouble(1),
					reader.GetDouble(2),
					reader.GetDouble(3),
					reader.GetDouble(4),
					reader.GetDouble(5),
					state)
				{
					Extended = ext,
					SensorContact = contact,
				});
```

Apply this in BOTH `GetHrvSamples` (instance) and `ReadHistory` (static) — they have identical query/construction logic. Add `using MeltdownMonitor.Core.Beats;` to the file if `SensorContactStatus` isn't already in scope (the repository already references `Beat`/`BatteryReading` from that namespace, so it is imported).

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~MeltdownRepositoryContactTests"`
Expected: PASS (3 tests). Then full suite `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj` — all pass (the existing repository/history tests still green; the added column is additive).

- [ ] **Step 7: Commit**

```bash
git add MeltdownMonitor.Core/Persistence/MeltdownRepository.cs MeltdownMonitor.Tests/MeltdownRepositoryContactTests.cs
git commit -m "feat: persist sensor contact on hrv_samples (migrate + read/write)"
```

---

### Task 3: Stamp contact onto the persisted sample (both pipelines)

**Files:**
- Modify: `MeltdownMonitor.Mobile/Pipeline.cs` (Mobile — builds locally)
- Modify: `MeltdownMonitor.App/Pipeline.cs` (App — inspection + CI)

- [ ] **Step 1: Mobile — add to the `with` expression**

In `MeltdownMonitor.Mobile/Pipeline.cs`, `RunAsync` builds:

```csharp
				var finalSample = sample with
				{
					BaselineRmssd = _baseline.BaselineRmssd,
					BaselineHr = _baseline.BaselineHr,
					State = state,
				};
```

Add the contact stamp (the loop already has `LatestContact` in scope — it's used two lines above for `_baseline.Update`/`_detector.Process`):

```csharp
				var finalSample = sample with
				{
					BaselineRmssd = _baseline.BaselineRmssd,
					BaselineHr = _baseline.BaselineHr,
					State = state,
					SensorContact = LatestContact,
				};
```

- [ ] **Step 2: App — the identical edit**

In `MeltdownMonitor.App/Pipeline.cs`, `RunAsync` has the same `finalSample = sample with { BaselineRmssd, BaselineHr, State }` block. Add `SensorContact = LatestContact,` to it identically. (App can't build locally — verify by reading that `LatestContact` is the in-scope property, which it is: used just above for `_baseline.Update(sample, LatestContact)` and `_detector.Process(sample, _baseline.IsWarm, LatestContact)`.)

- [ ] **Step 3: Build Mobile + run the suite**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj` (expect 0/0) and `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj` (expect all pass). The Mobile pipeline now persists contact; existing tests remain green.

- [ ] **Step 4: Commit**

```bash
git add MeltdownMonitor.App/Pipeline.cs MeltdownMonitor.Mobile/Pipeline.cs
git commit -m "feat: stamp LatestContact onto the persisted HRV sample (both heads)"
```

---

### Task 4: Desktop Overview contact strip (App — inspection + CI)

**Files:**
- Modify: `MeltdownMonitor.App/StatusWindow.cs`

**Do NOT build App locally. Verify by careful inspection; CI builds it.**

- [ ] **Step 1: Add the buffer + include it in `AllSparklines`**

READ `StatusWindow.cs` first. Add a contact ring buffer next to the other sparkline fields (e.g. after `_sd1Sd2`):

```csharp
	private readonly RingBuffer<float> _contact = new(InitialSparklineCapacity);
```

Add `_contact` to the `AllSparklines` expression-bodied array so it is resized/resampled with the rest:

```csharp
	private RingBuffer<float>[] AllSparklines => [
		_rmssd, _baselineRmssd, _pnn50, _sdnn,
		_meanHr, _baselineHr,
		_lfPower, _hfPower, _lfHfRatio, _baselineLfHf,
		_sd1, _sd2, _sd1Sd2,
		_contact,
	];
```

- [ ] **Step 2: Add the mapping helper**

Add a static helper (near the other private helpers, e.g. next to `SnapshotF`):

```csharp
	// 1 = signal trustworthy (Detected or NotSupported), 0 = NotDetected (readings gated).
	private static float ContactToValue(SensorContactStatus contact) =>
		contact == SensorContactStatus.NotDetected ? 0f : 1f;
```

- [ ] **Step 3: Feed it live in `OnSampleUpdated`**

Inside the `lock (_historyLock)` block of `OnSampleUpdated`, after the existing `_baselineLfHf.PushBack(...)` line (and outside the `if (sample.Extended is { } ext)` block, since contact is always present), add:

```csharp
				_contact.PushBack(ContactToValue(sample.SensorContact));
```

- [ ] **Step 4: Backfill it in `BackfillFromRepository`**

In the `foreach (var s in samples.TakeLast(desired))` loop, after the `_baselineLfHf.PushBack(...)` line (outside the `if (s.Extended is { } ext)` block), add:

```csharp
					_contact.PushBack(ContactToValue(s.SensorContact));
```

(The `foreach (var rb in AllSparklines) rb.Resize(desired);` loop just above already resizes `_contact`, since it's now in `AllSparklines`. Do NOT add a separate resize.)

- [ ] **Step 5: Render the strip in `DrawOverviewTab`**

READ `DrawOverviewTab` to match how existing sparklines are plotted (they use a helper that calls `ImGui.PlotLines`/an `ImGuiWidgets` plot with a label, the `SnapshotF(...)` array, and a size). Add a contact plot using the SAME plotting helper the other metrics use, but with a FIXED 0..1 scale so the binary signal reads as a step/band rather than auto-scaling. Concretely, after the existing metric plots in the Overview tab, add a block of the form:

```csharp
		float[] contact = SnapshotF(_contact);
		// Binary contact strip: flat at 1 (OK), drops to 0 when contact is lost. Fixed 0..1 scale.
		ImGui.PlotLines("Sensor contact", ref contact[0], contact.Length, 0, "1=OK  0=no contact", 0f, 1f, new Vector2(0, 40));
```

ADAPT this to the file's actual plotting convention: if the other sparklines go through a private helper (e.g. `DrawSparkline(label, buffer, ...)` or an `ImGuiWidgets` call) rather than raw `ImGui.PlotLines`, mirror that helper's signature and pass an explicit fixed min=0/max=1 and a short height. The non-negotiable requirements: (a) use `SnapshotF(_contact)`, (b) fixed Y range 0..1 (not auto-scaled), (c) guard the empty-buffer case the same way the existing plots do (if they pre-check `Length > 0` or pass a safe `ref`, do likewise — `ref contact[0]` throws on an empty array, so match the existing guard pattern). If the existing plot helper already handles empty buffers and fixed scales, prefer calling it over raw `PlotLines`.

- [ ] **Step 6: Inspection verification (no build)**

Confirm by reading: `_contact` is in `AllSparklines` (resized once, no double-resize); `ContactToValue` is used in both the live and backfill feeds; the render uses `SnapshotF(_contact)`, a fixed 0..1 range, a short height, and matches the existing empty-buffer guard; `SensorContactStatus` is in scope in `StatusWindow.cs` (it is — `_pipeline.LatestContact` of that type is already used). `git diff` and read critically (brace balance, the feed lines are inside `lock (_historyLock)`, no stray edits).

- [ ] **Step 7: Commit**

```bash
git add MeltdownMonitor.App/StatusWindow.cs
git commit -m "feat: contact step-strip in the desktop Overview tab"
```

---

### Task 5: Full verification + PR

**Files:** none (verification only)

- [ ] **Step 1: Run the whole suite**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj`
Expected: PASS — all prior tests plus HrvSample (2) and repository contact (3).

- [ ] **Step 2: Build the locally-buildable projects under warnings-as-errors**

Run:
```
dotnet build MeltdownMonitor.Core/MeltdownMonitor.Core.csproj
dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj
dotnet build MeltdownMonitor.Ble.Windows/MeltdownMonitor.Ble.Windows.csproj
```
Expected: all succeed, 0 warnings/errors. (App + iOS build on CI / macOS only.)

- [ ] **Step 3: Push and open the PR**

```bash
git push -u origin claude/contact-graph
gh pr create --base main --title "Persisted sensor contact + Overview graph" --body "Implements docs/superpowers/specs/2026-06-01-persisted-contact-graph-design.md. Adds HrvSample.SensorContact, persists it on hrv_samples (additive migration), stamps it in both pipelines, and adds a 0/1 contact step-strip to the desktop Overview tab. Core persistence is unit-tested; App graph + pipeline stamp verified by code review + CI (App can't restore ktsu.ImGui.App 2.6.0 locally; iOS needs macOS)."
```

Watch CI — the `.NET` workflow builds App (the `StatusWindow` graph) and the `iOS` workflow builds the iOS head (the Mobile pipeline stamp it consumes).

- [ ] **Step 4: Live-app smoke (only gate for the visual)**

On the desktop app with a real sensor: the Overview tab shows the contact strip flat at "OK", dropping to 0 when the sensor loses skin/electrode contact, and backfilling from persisted history on window open.

---

## Self-review

**Spec coverage:**
- `HrvSample.SensorContact` init-only, default NotSupported → Task 1. ✓
- Persist on `hrv_samples` via migration + write + both reads, NULL→NotSupported → Task 2. ✓
- Pipeline stamping both heads → Task 3. ✓
- Desktop Overview 0/1 strip (1=Detected/NotSupported, 0=NotDetected), live + backfill + fixed-range render → Task 4. ✓
- Tests: HrvSample default/round-trip (Task 1); repository round-trip via GetHrvSamples + ReadHistory + default→NotSupported (Task 2); App graph by inspection+CI+live (Tasks 4/5). ✓
- Non-goals (full-resolution transitions table, mobile graph) correctly omitted.

**Placeholder scan:** none — Task 4 Step 5 gives concrete code plus an explicit "adapt to the file's plot helper" instruction with non-negotiable constraints (that is guidance for an unknown-until-read API, not a placeholder; the engineer reads `DrawOverviewTab` and mirrors its existing plotting call). All other steps are exact.

**Type consistency:** `SensorContact` is `SensorContactStatus` everywhere (HrvSample property, insert `.ToString()`, read `Enum.TryParse`, pipeline `LatestContact` which is `SensorContactStatus`, `ContactToValue(SensorContactStatus)`); column name `contact` consistent across migrate/insert/both selects; reader index 14 matches the appended SELECT column in both read methods; `_contact` buffer name consistent across field/AllSparklines/feed/backfill/render. ✓
