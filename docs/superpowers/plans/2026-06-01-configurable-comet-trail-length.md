# Configurable comet-trail length — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Regulation Field comet-trail length a user setting (point count) on both the Windows desktop and the Avalonia/iOS mobile head, replacing the two hardcoded `48` constants.

**Architecture:** A new `RegulationTrailLength` int setting (default 48, clamped 12–240) is mirrored on `AppSettings` and `MobileSettings`. The desktop pipeline exposes a clamped accessor that `RegulationFieldView` reads live while trimming a now-dynamic trail buffer; the mobile `NowViewModel` reads the cap from an injected `Func<int>` and trims its trail; both heads gain a Settings slider.

**Tech Stack:** C# / .NET 10, MSTest, Dear ImGui (desktop), Avalonia (mobile).

**Spec:** `docs/superpowers/specs/2026-06-01-configurable-comet-trail-length-design.md`

**Branch:** `claude/configurable-trail-length` (already created off `main`; the spec commit is on it).

**Build reality (read before starting):**
- **Core / Mobile / Tests** build & test locally — full TDD for those tasks.
- **App** (`net10.0-windows`) CANNOT restore locally (`ktsu.ImGui.App 2.6.0` is unpublished on nuget.org → `NU1102`); **iOS** (`net10.0-ios`) builds only on macOS. Tasks touching `MeltdownMonitor.App/*` and `MeltdownMonitor.iOS/*` are verified by careful code inspection + CI on the PR — do NOT run `dotnet build` on App/iOS/solution for those (restore fails by design).

---

## File structure

**Modify:**
- `MeltdownMonitor.App/AppSettings.cs` — add `RegulationTrailLength` field (Task 1).
- `MeltdownMonitor.Mobile/MobileSettings.cs` — add `RegulationTrailLength` field (Task 1).
- `MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs` — injected `Func<int>` cap + trim (Task 2).
- `MeltdownMonitor.Mobile/ViewModels/SettingsViewModel.cs` — `RegulationTrailLength` property (Task 3).
- `MeltdownMonitor.Mobile/Views/SettingsView.axaml` — slider (Task 4).
- `MeltdownMonitor.iOS/IosCompositionRoot.cs` — pass the provider (Task 4).
- `MeltdownMonitor.App/Pipeline.cs` — clamped accessor (Task 5).
- `MeltdownMonitor.App/Regulation/RegulationFieldView.cs` — dynamic trail buffer (Task 5).
- `MeltdownMonitor.App/StatusWindow.cs` — knob + reset default (Task 6).

**Test (modify):**
- `MeltdownMonitor.Tests/MobileSettingsSerializerTests.cs` — round-trip the new field (Task 1).
- `MeltdownMonitor.Tests/NowViewModelTests.cs` — cap/trim/clamp (Task 2).
- `MeltdownMonitor.Tests/SettingsViewModelTests.cs` (create if absent, else extend) — property round-trip/clamp (Task 3).

---

### Task 1: Add the `RegulationTrailLength` setting to both heads

**Files:**
- Modify: `MeltdownMonitor.App/AppSettings.cs`
- Modify: `MeltdownMonitor.Mobile/MobileSettings.cs`
- Test: `MeltdownMonitor.Tests/MobileSettingsSerializerTests.cs`

- [ ] **Step 1: Write the failing test**

Open `MeltdownMonitor.Tests/MobileSettingsSerializerTests.cs`, read it to match its style, and add this test method inside the class:

```csharp
	[TestMethod]
	public void RoundTrip_PreservesRegulationTrailLength()
	{
		var settings = new MobileSettings { RegulationTrailLength = 96 };

		string json = MobileSettingsSerializer.Serialize(settings);
		MobileSettings restored = MobileSettingsSerializer.Deserialize(json);

		Assert.AreEqual(96, restored.RegulationTrailLength);
	}

	[TestMethod]
	public void Default_RegulationTrailLength_Is48()
	{
		Assert.AreEqual(48, new MobileSettings().RegulationTrailLength);
	}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~MobileSettingsSerializerTests"`
Expected: FAIL to compile — `MobileSettings` has no `RegulationTrailLength`.

- [ ] **Step 3: Add the field to `MobileSettings`**

In `MeltdownMonitor.Mobile/MobileSettings.cs`, add a property among the existing ones (place it after `EnableLiveActivity` to group it with display-ish settings):

```csharp
	/// <summary>Number of recent readings drawn as the Regulation Field comet trail
	/// (12–240; clamped at the consumer). Default 48 ≈ 4 min at the 5 s emit cadence.</summary>
	public int RegulationTrailLength { get; set; } = 48;
```

- [ ] **Step 4: Add the field to `AppSettings`**

In `MeltdownMonitor.App/AppSettings.cs`, add the same property (place it after `SparklineWindowMinutes`):

```csharp
	/// <summary>Number of recent readings drawn as the Regulation Field comet trail
	/// (12–240; clamped at the consumer). Default 48 ≈ 4 min at the 5 s emit cadence.</summary>
	public int RegulationTrailLength { get; set; } = 48;
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~MobileSettingsSerializerTests"`
Expected: PASS (both new tests + existing serializer tests).

- [ ] **Step 6: Commit**

```bash
git add MeltdownMonitor.App/AppSettings.cs MeltdownMonitor.Mobile/MobileSettings.cs MeltdownMonitor.Tests/MobileSettingsSerializerTests.cs
git commit -m "feat: add RegulationTrailLength setting (default 48) to both heads"
```

---

### Task 2: Mobile `NowViewModel` — injected cap + trim (TDD)

**Files:**
- Modify: `MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs`
- Test: `MeltdownMonitor.Tests/NowViewModelTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `MeltdownMonitor.Tests/NowViewModelTests.cs` (it already imports `MeltdownMonitor.Core.Regulation` and `MeltdownMonitor.Mobile.ViewModels`). Helper to push N readings:

```csharp
	[TestMethod]
	public void Trail_CapsAtProvidedLength()
	{
		var vm = new NowViewModel(trailLengthProvider: () => 20);
		for (int i = 0; i < 50; i++)
		{
			vm.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));
		}

		Assert.AreEqual(20, vm.RegulationTrail.Count);
	}

	[TestMethod]
	public void Trail_NullProvider_CapsAtDefault48()
	{
		var vm = new NowViewModel();
		for (int i = 0; i < 100; i++)
		{
			vm.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));
		}

		Assert.AreEqual(48, vm.RegulationTrail.Count);
	}

	[TestMethod]
	public void Trail_LoweringCap_TrimsKeepingNewest()
	{
		int cap = 40;
		var vm = new NowViewModel(trailLengthProvider: () => cap);
		// Fill to 40 with index 0.0, then push one identifiable reading at the new smaller cap.
		for (int i = 0; i < 40; i++)
		{
			vm.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));
		}

		cap = 10;
		vm.OnReadingUpdated(new RegulationReading(0.9, 1.0, 1.0, 0.5, 0.0)); // newest, distinct

		Assert.AreEqual(10, vm.RegulationTrail.Count);
		Assert.AreEqual(0.9, vm.RegulationTrail[^1].Index, 1e-9, "the newest reading must be kept");
	}

	[TestMethod]
	public void Trail_ClampsProviderToValidRange()
	{
		var tiny = new NowViewModel(trailLengthProvider: () => 3);     // below 12 floor
		var huge = new NowViewModel(trailLengthProvider: () => 99999); // above 240 ceiling
		for (int i = 0; i < 300; i++)
		{
			tiny.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));
			huge.OnReadingUpdated(new RegulationReading(0.0, 1.0, 1.0, 0.5, 0.0));
		}

		Assert.AreEqual(12, tiny.RegulationTrail.Count, "below-floor cap clamps to 12");
		Assert.AreEqual(240, huge.RegulationTrail.Count, "above-ceiling cap clamps to 240");
	}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~NowViewModelTests"`
Expected: FAIL to compile — `NowViewModel` has no `trailLengthProvider` constructor parameter.

- [ ] **Step 3: Implement the provider + trim**

In `MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs`:

(a) Remove the constant `private const int RegulationTrailLength = 48;` (the line near the top).

(b) Add a field next to the other injected callbacks (`_onConnect` / `_onDisconnect` / `_onAnnotate`):

```csharp
	private readonly Func<int>? _trailLengthProvider;
```

(c) Add the constructor parameter (append it to the existing signature, keeping defaults so both call sites stay valid) and assign it:

```csharp
	public NowViewModel(
		Func<Task>? onConnect = null,
		Func<Task>? onDisconnect = null,
		Func<AnnotationLabel, string?, Task>? onAnnotate = null,
		Func<int>? trailLengthProvider = null)
	{
		_onConnect = onConnect;
		_onDisconnect = onDisconnect;
		_onAnnotate = onAnnotate;
		_trailLengthProvider = trailLengthProvider;
		// ... rest of the existing constructor body unchanged ...
```

(d) In `OnReadingUpdated`, replace the trim loop that referenced the constant. The current body trims with `while (_regulationTrail.Count > RegulationTrailLength)`. Change it to read and clamp the live cap:

```csharp
		_regulationTrail.Add(reading);
		int cap = Math.Clamp(_trailLengthProvider?.Invoke() ?? 48, 12, 240);
		while (_regulationTrail.Count > cap)
		{
			_regulationTrail.RemoveAt(0);
		}

		// Hand the control a fresh list instance so its AffectsRender binding fires.
		RegulationTrail = _regulationTrail.ToArray();
```

(Confirm the surrounding lines — `Reading = reading;`, the `Raise(nameof(IsTrendVisible));` added by the velocity feature, and the `RunOnUi` wrapper — are preserved exactly.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~NowViewModelTests"`
Expected: PASS — the 4 new tests plus all existing `NowViewModelTests`.

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs MeltdownMonitor.Tests/NowViewModelTests.cs
git commit -m "feat: NowViewModel comet trail honours an injected length provider"
```

---

### Task 3: Mobile `SettingsViewModel` — `RegulationTrailLength` property (TDD)

**Files:**
- Modify: `MeltdownMonitor.Mobile/ViewModels/SettingsViewModel.cs`
- Test: `MeltdownMonitor.Tests/SettingsViewModelTests.cs` (create if it does not exist)

- [ ] **Step 1: Write the failing tests**

First check whether `MeltdownMonitor.Tests/SettingsViewModelTests.cs` exists (`Glob`/`Read`). If it exists, add the methods below inside its class. If not, create it with this content:

```csharp
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.Tests;

[TestClass]
public class SettingsViewModelTests
{
	[TestMethod]
	public void RegulationTrailLength_RoundTripsOntoSettings()
	{
		var settings = new MobileSettings();
		var vm = new SettingsViewModel(settings);

		vm.RegulationTrailLength = 120;

		Assert.AreEqual(120, settings.RegulationTrailLength);
		Assert.AreEqual(120, vm.RegulationTrailLength);
	}

	[TestMethod]
	public void RegulationTrailLength_ClampsToRange()
	{
		var settings = new MobileSettings();
		var vm = new SettingsViewModel(settings);

		vm.RegulationTrailLength = 5;
		Assert.AreEqual(12, settings.RegulationTrailLength, "below floor clamps to 12");

		vm.RegulationTrailLength = 9999;
		Assert.AreEqual(240, settings.RegulationTrailLength, "above ceiling clamps to 240");
	}

	[TestMethod]
	public void RegulationTrailLength_PersistsOnlyOnChange()
	{
		var settings = new MobileSettings { RegulationTrailLength = 48 };
		int saves = 0;
		var vm = new SettingsViewModel(settings, onChanged: () => saves++);

		vm.RegulationTrailLength = 48; // unchanged → no persist
		Assert.AreEqual(0, saves);

		vm.RegulationTrailLength = 60; // changed → one persist
		Assert.AreEqual(1, saves);
	}
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests"`
Expected: FAIL to compile — `SettingsViewModel` has no `RegulationTrailLength`.

- [ ] **Step 3: Implement the property**

In `MeltdownMonitor.Mobile/ViewModels/SettingsViewModel.cs`, add a property mirroring the existing `RmssdWarningDropPercent` shape (clamp / change-check / Raise / Persist), e.g. after `HrWarningRisePercent`:

```csharp
	public int RegulationTrailLength
	{
		get => _settings.RegulationTrailLength;
		set
		{
			int clamped = Math.Clamp(value, 12, 240);
			if (_settings.RegulationTrailLength != clamped)
			{
				_settings.RegulationTrailLength = clamped;
				Raise();
				Persist();
			}
		}
	}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Mobile/ViewModels/SettingsViewModel.cs MeltdownMonitor.Tests/SettingsViewModelTests.cs
git commit -m "feat: SettingsViewModel exposes RegulationTrailLength (clamped 12-240)"
```

---

### Task 4: Mobile view slider + iOS composition wiring

**Files:**
- Modify: `MeltdownMonitor.Mobile/Views/SettingsView.axaml` (Mobile — builds locally)
- Modify: `MeltdownMonitor.iOS/IosCompositionRoot.cs` (iOS — verified by inspection + CI)

- [ ] **Step 1: Add the slider to `SettingsView.axaml`**

In `MeltdownMonitor.Mobile/Views/SettingsView.axaml`, add a new settings group (mirror the "Sensitivity" group's label+value+`Slider` shape). Place it after the "Display" `StackPanel` (the Live Activity group) and before the "Sensitivity" group:

```xml
            <StackPanel Spacing="6">
                <TextBlock Text="Regulation field" FontWeight="SemiBold" />
                <Grid ColumnDefinitions="*,Auto">
                    <TextBlock Grid.Column="0" Text="Comet trail length" />
                    <TextBlock Grid.Column="1"
                               Text="{Binding RegulationTrailLength, StringFormat='{}{0} pts'}" />
                </Grid>
                <Slider Minimum="12" Maximum="240"
                        Value="{Binding RegulationTrailLength, Mode=TwoWay}" />
                <TextBlock Text="How many recent readings the comet trail shows."
                           FontSize="12" Foreground="#8A8F98" TextWrapping="Wrap" />
            </StackPanel>
```

- [ ] **Step 2: Build the mobile project (validates the binding)**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: Build succeeded, 0 warnings/errors. (Compiled XAML validates `RegulationTrailLength` against `SettingsViewModel` because `x:DataType` is set; a slider binding to an `int` two-way works as `Slider.Value` is `double` and Avalonia coerces — the VM property is `int`, so the binding round-trips through `double`→`int`. This compiles; numeric coercion is a runtime binding behaviour Avalonia supports.)

- [ ] **Step 3: Wire the provider in iOS composition (inspection only — do NOT build iOS)**

In `MeltdownMonitor.iOS/IosCompositionRoot.cs`, line ~66, the local `settings` (loaded at line 59) is in scope. Change:

```csharp
		_now = new NowViewModel(onAnnotate: RecordAnnotationAsync);
```

to:

```csharp
		_now = new NowViewModel(
			onAnnotate: RecordAnnotationAsync,
			trailLengthProvider: () => settings.RegulationTrailLength);
```

(The same `settings` instance is mutated by `SettingsViewModel` and saved via `onChanged`, so the provider reads the live value.)

- [ ] **Step 4: Run the full suite (mobile path unaffected)**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Mobile/Views/SettingsView.axaml MeltdownMonitor.iOS/IosCompositionRoot.cs
git commit -m "feat: mobile trail-length slider + iOS provider wiring"
```

---

### Task 5: Desktop pipeline accessor + dynamic trail buffer (inspection + CI)

**Files:**
- Modify: `MeltdownMonitor.App/Pipeline.cs`
- Modify: `MeltdownMonitor.App/Regulation/RegulationFieldView.cs`

**Do NOT build App locally (restore fails: `ktsu.ImGui.App 2.6.0` unpublished). Verify by careful inspection; CI builds it.**

- [ ] **Step 1: Add the clamped accessor to `App/Pipeline.cs`**

Read the file to find the `LatestThresholds`/`LatestReading` accessors, and add next to them:

```csharp
	/// <summary>Configured comet-trail length (clamped 12–240), read live by the field view.</summary>
	public int RegulationTrailLength => Math.Clamp(_settings.RegulationTrailLength, 12, 240);
```

- [ ] **Step 2: Make the trail buffer dynamic in `RegulationFieldView.cs`**

Read the file first. Apply these edits:

(a) Remove the constant `private const int TrailLength = 48;` (line ~20; leave the comment-free gap).

(b) Replace the fixed array + count fields:
```csharp
	private readonly TrailPoint[] _trail = new TrailPoint[TrailLength];
	private int _trailCount;
```
with a list:
```csharp
	private readonly List<TrailPoint> _trail = [];
```

(c) In `OnSampleUpdated` (inside the existing `lock (_lock)`), replace the append block:
```csharp
				var point = new TrailPoint(now, reading);
				if (_trailCount < TrailLength)
				{
					_trail[_trailCount++] = point;
				}
				else
				{
					Array.Copy(_trail, 1, _trail, 0, TrailLength - 1);
					_trail[^1] = point;
				}
```
with a list append + live-cap trim:
```csharp
				_trail.Add(new TrailPoint(now, reading));
				int cap = _pipeline.RegulationTrailLength;
				while (_trail.Count > cap)
				{
					_trail.RemoveAt(0);
				}
```

(d) In `Snapshot()` (inside `lock (_lock)`), replace:
```csharp
			var trail = new RegulationReading[_trailCount];
			for (int i = 0; i < _trailCount; i++)
			{
				trail[i] = _trail[i].Reading;
			}
```
with:
```csharp
			var trail = new RegulationReading[_trail.Count];
			for (int i = 0; i < _trail.Count; i++)
			{
				trail[i] = _trail[i].Reading;
			}
```

(Leave the `TrailPoint` record and everything else unchanged. `_trailCount` is now fully removed — grep the file to confirm no remaining references.)

- [ ] **Step 3: Inspection verification (no build)**

Re-read the edited regions and confirm: `_trailCount` has zero remaining references; `TrailLength` has zero remaining references; `_trail` is a `List<TrailPoint>` used only under `_lock`; `_pipeline.RegulationTrailLength` exists (added in Step 1) and returns `int`; `Math` is in scope (it is — used elsewhere in the file). `git diff` and read it critically.

- [ ] **Step 4: Commit**

```bash
git add MeltdownMonitor.App/Pipeline.cs MeltdownMonitor.App/Regulation/RegulationFieldView.cs
git commit -m "feat: desktop comet trail uses a dynamic, settings-driven length"
```

---

### Task 6: Desktop Settings knob + reset default (inspection + CI)

**Files:**
- Modify: `MeltdownMonitor.App/StatusWindow.cs`

**Do NOT build App locally. Verify by inspection; CI builds it.**

- [ ] **Step 1: Add the trail-length knob**

Read `StatusWindow.cs` around the "Refresh" settings section. After the `SparklineWindowMinutes` knob's `HelpMarker(...)` call (the block ending the History knob), add a new knob mirroring it exactly (the int `ImGuiWidgets.Knob` overload is the same one used for `SparklineWindowMinutes`):

```csharp
			int trail = _settings.RegulationTrailLength;
			if (ImGuiWidgets.Knob("Trail (pts)", ref trail, 12, 240, format: "%d pts", flags: ImGuiKnobOptions.ValueTooltip))
			{
				_settings.RegulationTrailLength = trail;
				_settingsDirty = true;
			}
			ImGui.SameLine();
			HelpMarker("How many recent readings the Regulation Field comet trail shows. Higher = longer tail; lower = shorter.");
```

(Place it on its own line after the History knob's HelpMarker. If the row visually overflows in the live app, that's a cosmetic adjustment for the live-app pass — functionally correct as written.)

- [ ] **Step 2: Add the reset-to-defaults line**

In the "reset all tuning" popup handler, after `_settings.SparklineWindowMinutes = 60;`, add:

```csharp
				_settings.RegulationTrailLength = 48;
```

- [ ] **Step 3: Inspection verification (no build)**

Confirm: `ImGuiWidgets.Knob(string, ref int, int, int, format:, flags:)` matches the existing `SparklineWindowMinutes` call signature (copy it); `_settingsDirty` is the existing dirty flag used by the other knobs; `ImGuiKnobOptions.ValueTooltip` and `HelpMarker` are already used in this method. `git diff` and read critically.

- [ ] **Step 4: Commit**

```bash
git add MeltdownMonitor.App/StatusWindow.cs
git commit -m "feat: desktop Settings knob for comet trail length + reset default"
```

---

### Task 7: Full verification + PR

**Files:** none (verification only)

- [ ] **Step 1: Run the whole suite**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj`
Expected: PASS — all prior tests plus the new serializer (2), NowViewModel (4), and SettingsViewModel (3) tests.

- [ ] **Step 2: Build the locally-buildable projects under warnings-as-errors**

Run:
```
dotnet build MeltdownMonitor.Core/MeltdownMonitor.Core.csproj
dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj
dotnet build MeltdownMonitor.Ble.Windows/MeltdownMonitor.Ble.Windows.csproj
```
Expected: all succeed, 0 warnings/errors. (App + iOS build only on CI / macOS — covered by the PR's workflows.)

- [ ] **Step 3: Push and open the PR**

```bash
git push -u origin claude/configurable-trail-length
gh pr create --base main --title "Configurable comet-trail length" --body "Implements docs/superpowers/specs/2026-06-01-configurable-comet-trail-length-design.md. Adds a RegulationTrailLength setting (default 48, clamped 12-240) on both heads with a Settings slider; the trail buffer adopts it live. Desktop view + App/iOS builds verified by code review + CI (App can't restore ktsu.ImGui.App 2.6.0 locally; iOS needs macOS)."
```

Watch CI — the `.NET` workflow validates the App build (the desktop `RegulationFieldView`/`StatusWindow` changes) and the `iOS` workflow validates the iOS composition change; both are the only gates for the un-buildable-locally edits.

- [ ] **Step 4: Live-app smoke (only gate for real-time visuals)**

On the desktop app: move the "Trail (pts)" knob and confirm the comet trail lengthens/shortens within an emit interval; confirm "reset to defaults" returns it to 48.

---

## Self-review

**Spec coverage:**
- `RegulationTrailLength` mirrored on both settings types, default 48 → Task 1. ✓
- Range 12–240 clamped at every consumer (desktop pipeline accessor Task 5; mobile VM setter Task 3; mobile trail trim Task 2) → ✓
- Replaces both hardcoded constants (`NowViewModel.RegulationTrailLength` Task 2; `RegulationFieldView.TrailLength` Task 5) → ✓
- Settings slider on both heads (mobile Task 4; desktop Task 6) → ✓
- Live adoption of the cap (desktop trim per append against `_pipeline.RegulationTrailLength` Task 5; mobile trim per `OnReadingUpdated` against the provider Task 2) → ✓
- Tests: NowViewModel cap/trim/clamp (Task 2), SettingsViewModel round-trip/clamp/persist (Task 3), serializer round-trip + default (Task 1) → ✓
- Desktop view + sliders by build/CI + live app (Tasks 5/6/7) → ✓
- Non-goals (minutes/time eviction, shared settings type, sparkline changes) correctly not implemented.

**Placeholder scan:** none — every code step shows full content; verify steps give exact commands + expected output.

**Type consistency:** `RegulationTrailLength` is an `int` everywhere (both settings fields, the desktop pipeline accessor returns `int`, the mobile VM property is `int`, the provider is `Func<int>`); clamp bounds `12`/`240` and default `48` identical across Tasks 1/2/3/5/6; `NowViewModel(... , Func<int>? trailLengthProvider = null)` signature in Task 2 matches the call sites updated in Task 4 (iOS) and the unchanged default factory `new NowViewModel()` in `RootViewModel`. ✓
