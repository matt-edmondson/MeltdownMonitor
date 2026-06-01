# Shutdown / Hypoarousal Visualization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make low arousal a first-class axis on the Regulation Field — a labelled SHUTDOWN quadrant with a deepening zone + marker halo that track the continuous `Hypoarousal` scalar, and a hypoarousal-aware velocity arrow — on both heads.

**Architecture:** A pure Core helper (`HypoarousalVisual`) centralises the collapse-cue mappings so both renderers stay consistent and the logic is unit-tested. A second `RegulationVelocityTracker` (the existing, signal-agnostic, fully-tested class — unchanged) is fed the `Hypoarousal` scalar in both pipelines, exposed as `LatestHypoarousalDynamics`. The desktop (ImGui) and mobile (Avalonia) renderers consume the scalar + the new dynamics to draw the zone, halo, and arrow. A distinct collapse colour is added to both palettes; Lavender stays on the window-of-tolerance + crossover.

**Tech Stack:** C# / .NET 10, MSTest. Repo conventions: tabs, CRLF, file-scoped namespaces, usings inside namespace where the file already does so, braces always, explicit accessibility, no `this.`, nullable enabled, warnings-as-errors. Commit tags `[minor]`/`[patch]`; no `Co-Authored-By` lines.

**Source spec:** `docs/superpowers/specs/2026-06-02-shutdown-hypoarousal-visualization-design.md`

**Build/test loop (no BLE/Windows):**
```
dotnet build MeltdownMonitor.Core/MeltdownMonitor.Core.csproj
dotnet test  MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj
```
Desktop App compiles on Windows only (build to a temp `--output`; a running instance locks the DLLs). iOS heads build only on macOS via `ios.yml` — but this work touches no iOS-head files.

---

## File structure

| File | Responsibility | Task |
|---|---|---|
| `MeltdownMonitor.Core/Regulation/HypoarousalVisual.cs` | **New.** Pure mappings: zone/halo intensity, collapse-arrow decision, index-arrow suppression. | 1 |
| `MeltdownMonitor.Tests/HypoarousalVisualTests.cs` | **New.** Unit tests for the helper. | 1 |
| `MeltdownMonitor.Mobile/Pipeline.cs` | Add 2nd tracker, `LatestHypoarousalDynamics`, `HypoarousalDynamicsUpdated`. | 2 |
| `MeltdownMonitor.App/Pipeline.cs` | Add 2nd tracker, `LatestHypoarousalDynamics`. | 2 |
| `MeltdownMonitor.App/Regulation/MacchiatoPalette.cs` | Add the `Slate` collapse colour. | 3 |
| `MeltdownMonitor.Mobile/Controls/RegulationField.cs` | Add the `Slate` colour; zone/halo/arrow; pulse rename. | 3, 5 |
| `MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs` | Consume `HypoarousalDynamics`. | 4 |
| `MeltdownMonitor.Tests/NowViewModelTests.cs` | Test the VM consumption. | 4 |
| `MeltdownMonitor.Mobile/Views/NowView.axaml` | Bind new field props; recolour the shutdown line. | 5 |
| `MeltdownMonitor.App/Regulation/RegulationFieldView.cs` | Zone/halo/arrow/labels; recolour tag; pulse rename; LF/HF comment fix. | 6 |

---

## Task 1: Core — `HypoarousalVisual` pure helper (TDD)

**Files:**
- Create: `MeltdownMonitor.Core/Regulation/HypoarousalVisual.cs`
- Test: `MeltdownMonitor.Tests/HypoarousalVisualTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `MeltdownMonitor.Tests/HypoarousalVisualTests.cs`:

```csharp
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

[TestClass]
public class HypoarousalVisualTests
{
	private static RegulationDynamics Rising => new(0.03, RegulationTrend.Escalating, 0.6);
	private static RegulationDynamics Falling => new(-0.03, RegulationTrend.DeEscalating, 0.6);
	private static RegulationDynamics Flat => RegulationDynamics.Steady;

	[TestMethod]
	public void Intensity_ZeroAtOrBelowFloor()
	{
		Assert.AreEqual(0.0, HypoarousalVisual.Intensity(0.0), 1e-9);
		Assert.AreEqual(0.0, HypoarousalVisual.Intensity(HypoarousalVisual.Floor), 1e-9);
		Assert.AreEqual(0.0, HypoarousalVisual.Intensity(HypoarousalVisual.Floor - 0.01), 1e-9);
	}

	[TestMethod]
	public void Intensity_RampsLinearlyAboveFloorToOne()
	{
		Assert.AreEqual(1.0, HypoarousalVisual.Intensity(1.0), 1e-9);
		// Halfway between floor and 1 -> 0.5.
		double mid = HypoarousalVisual.Floor + ((1.0 - HypoarousalVisual.Floor) / 2.0);
		Assert.AreEqual(0.5, HypoarousalVisual.Intensity(mid), 1e-9);
	}

	[TestMethod]
	public void Intensity_NonFiniteIsZero()
	{
		Assert.AreEqual(0.0, HypoarousalVisual.Intensity(double.NaN), 1e-9);
		Assert.AreEqual(0.0, HypoarousalVisual.Intensity(double.PositiveInfinity), 1e-9);
	}

	[TestMethod]
	public void ShowCollapseArrow_TrueOnlyWhenAboveFloorAndRising()
	{
		Assert.IsTrue(HypoarousalVisual.ShowCollapseArrow(0.7, Rising));
		Assert.IsFalse(HypoarousalVisual.ShowCollapseArrow(0.7, Flat), "deep but steady is not an approach");
		Assert.IsFalse(HypoarousalVisual.ShowCollapseArrow(0.7, Falling), "receding from collapse is not a warning");
		Assert.IsFalse(HypoarousalVisual.ShowCollapseArrow(HypoarousalVisual.Floor, Rising), "below/at floor stays dormant");
	}

	[TestMethod]
	public void SuppressIndexArrow_TrueWhenCollapsePresentAndIndexEasing()
	{
		// The exact false-reassurance case: in the shutdown zone while the index arrow would say "calming".
		Assert.IsTrue(HypoarousalVisual.SuppressIndexArrow(0.7, Falling));
		// Not suppressed when collapse is absent, or when the index is escalating (toward meltdown).
		Assert.IsFalse(HypoarousalVisual.SuppressIndexArrow(0.0, Falling));
		Assert.IsFalse(HypoarousalVisual.SuppressIndexArrow(0.7, Rising));
	}
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~HypoarousalVisualTests"`
Expected: FAIL to compile — `HypoarousalVisual` does not exist.

- [ ] **Step 3: Implement**

Create `MeltdownMonitor.Core/Regulation/HypoarousalVisual.cs`:

```csharp
namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Pure presentation mappings for the low-arousal / collapse cues on the Regulation Field,
/// shared by both heads so desktop and mobile stay visually consistent and the decision logic
/// is unit-testable. Inputs are the per-sample <c>Hypoarousal</c> scalar [0, 1]
/// (<see cref="RegulationReading.Hypoarousal"/>) and the rate-of-change of that scalar from a
/// second <see cref="RegulationVelocityTracker"/>. Outputs are unitless [0, 1] factors the
/// renderers scale their own pixel/alpha constants by, plus two arrow-decision predicates.
/// </summary>
public static class HypoarousalVisual
{
	/// <summary>
	/// Scalar at or below which collapse cues stay fully dormant — a deadband so beat-to-beat
	/// noise and genuine cool-but-steady rest never tint the field. Matches the spirit of
	/// <c>HypoarousalThresholds.EnterSignal</c> but is a display floor, not a detection threshold.
	/// </summary>
	public const double Floor = 0.15;

	/// <summary>
	/// [0, 1] intensity for the shutdown-zone fill and the marker's collapse halo: 0 at or below
	/// <see cref="Floor"/>, ramping linearly to 1 as the scalar approaches 1. Non-finite → 0.
	/// </summary>
	public static double Intensity(double hypoScalar)
	{
		if (!double.IsFinite(hypoScalar) || hypoScalar <= Floor)
		{
			return 0.0;
		}

		return Math.Clamp((hypoScalar - Floor) / (1.0 - Floor), 0.0, 1.0);
	}

	/// <summary>
	/// True when the trajectory cue should show a collapse WARNING (an arrow toward the shutdown
	/// zone) instead of the index-derived arrow: the collapse scalar is meaningfully present AND
	/// rising.
	/// </summary>
	public static bool ShowCollapseArrow(double hypoScalar, RegulationDynamics hypoDynamics)
		=> hypoScalar > Floor && hypoDynamics.Trend == RegulationTrend.Escalating;

	/// <summary>
	/// True when the existing index arrow must be suppressed to avoid contradicting the shutdown
	/// zone: the collapse scalar is present AND the index arrow would read as "de-escalating"
	/// (calming). Prevents a slide into collapse from being cued as relaxing.
	/// </summary>
	public static bool SuppressIndexArrow(double hypoScalar, RegulationDynamics indexDynamics)
		=> hypoScalar > Floor && indexDynamics.Trend == RegulationTrend.DeEscalating;
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~HypoarousalVisualTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```
git add MeltdownMonitor.Core/Regulation/HypoarousalVisual.cs MeltdownMonitor.Tests/HypoarousalVisualTests.cs
git commit -m "feat: add HypoarousalVisual collapse-cue mappings (pure, tested)"
```

---

## Task 2: Core — second velocity tracker for the Hypoarousal scalar (both pipelines)

A mechanical mirror of the existing `_velocity` (index) wiring, which is itself verified by build + downstream consumers rather than a loop-level unit test (the loop is private and async; `MobilePipelineThresholdTests` bypasses it). Correctness here is the wiring being a verbatim parallel of the trusted index path; behaviour is exercised by Task 1 (helper) + Task 4 (VM) + live validation. No new isolated unit test — build-verify only.

**Files:**
- Modify: `MeltdownMonitor.Mobile/Pipeline.cs`
- Modify: `MeltdownMonitor.App/Pipeline.cs`

- [ ] **Step 1: Mobile — add the field**

In `MeltdownMonitor.Mobile/Pipeline.cs`, after the existing tracker field (`private readonly RegulationVelocityTracker _velocity = new();`, line 26):

```csharp
	private readonly RegulationVelocityTracker _hypoVelocity = new();
```

- [ ] **Step 2: Mobile — add the property** (after `LatestDynamics`, line 62)

```csharp
	/// <summary>Velocity/trend of the <c>Hypoarousal</c> scalar — the rate of approach to (or
	/// retreat from) low-arousal collapse. <see cref="RegulationDynamics.Steady"/> until the
	/// baseline is warm. Peer to <see cref="LatestDynamics"/> (which tracks the arousal index).</summary>
	public RegulationDynamics LatestHypoarousalDynamics { get; private set; } = RegulationDynamics.Steady;
```

- [ ] **Step 3: Mobile — add the event** (after `DynamicsUpdated`, line 97)

```csharp
	/// <summary>Fires after <see cref="DynamicsUpdated"/> with the velocity/trend of the
	/// Hypoarousal scalar, derived from the same sample. Steady while calibrating or off-contact.</summary>
	public event Action<RegulationDynamics>? HypoarousalDynamicsUpdated;
```

- [ ] **Step 4: Mobile — wire it in `RunAsync`** (replace the velocity block, lines 297–310)

Replace:

```csharp
				// Velocity/trend of the arousal index. Only fold usable samples (baseline warm,
				// sensor in contact) into the tracker; otherwise reset it so the resumed stream
				// re-seeds rather than computing a spike across the gap or off the cold->warm jump.
				if (_baseline.IsWarm && LatestContact != SensorContactStatus.NotDetected)
				{
					_velocity.Update(reading.Index, finalSample.Timestamp);
				}
				else
				{
					_velocity.Reset();
				}

				LatestDynamics = _velocity.Latest;
				DynamicsUpdated?.Invoke(LatestDynamics);
```

with:

```csharp
				// Velocity/trend of the arousal index and of the Hypoarousal scalar. Only fold usable
				// samples (baseline warm, sensor in contact) into the trackers; otherwise reset them so
				// the resumed stream re-seeds rather than computing a spike across the gap or off the
				// cold->warm jump. The two trackers move together so the index and collapse trajectories
				// stay phase-aligned.
				if (_baseline.IsWarm && LatestContact != SensorContactStatus.NotDetected)
				{
					_velocity.Update(reading.Index, finalSample.Timestamp);
					_hypoVelocity.Update(reading.Hypoarousal, finalSample.Timestamp);
				}
				else
				{
					_velocity.Reset();
					_hypoVelocity.Reset();
				}

				LatestDynamics = _velocity.Latest;
				DynamicsUpdated?.Invoke(LatestDynamics);

				LatestHypoarousalDynamics = _hypoVelocity.Latest;
				HypoarousalDynamicsUpdated?.Invoke(LatestHypoarousalDynamics);
```

- [ ] **Step 5: App — add the field** (after `private readonly RegulationVelocityTracker _velocity = new();`, line 23)

```csharp
	private readonly RegulationVelocityTracker _hypoVelocity = new();
```

- [ ] **Step 6: App — add the property** (after `LatestDynamics`, line 55)

```csharp
	/// <summary>Velocity/trend of the <c>Hypoarousal</c> scalar — the rate of approach to (or
	/// retreat from) low-arousal collapse. <see cref="RegulationDynamics.Steady"/> until the
	/// baseline is warm. Peer to <see cref="LatestDynamics"/> (which tracks the arousal index).</summary>
	public RegulationDynamics LatestHypoarousalDynamics { get; private set; } = RegulationDynamics.Steady;
```

- [ ] **Step 7: App — wire it in `RunAsync`** (replace the velocity block, lines 248–258)

Replace:

```csharp
				// Velocity/trend of the arousal index — see the mobile pipeline for the gating rationale.
				if (_baseline.IsWarm && LatestContact != SensorContactStatus.NotDetected)
				{
					_velocity.Update(LatestReading.Index, finalSample.Timestamp);
				}
				else
				{
					_velocity.Reset();
				}

				LatestDynamics = _velocity.Latest;
```

with:

```csharp
				// Velocity/trend of the arousal index and the Hypoarousal scalar — see the mobile
				// pipeline for the gating rationale. The two trackers move together so the index and
				// collapse trajectories stay phase-aligned.
				if (_baseline.IsWarm && LatestContact != SensorContactStatus.NotDetected)
				{
					_velocity.Update(LatestReading.Index, finalSample.Timestamp);
					_hypoVelocity.Update(LatestReading.Hypoarousal, finalSample.Timestamp);
				}
				else
				{
					_velocity.Reset();
					_hypoVelocity.Reset();
				}

				LatestDynamics = _velocity.Latest;
				LatestHypoarousalDynamics = _hypoVelocity.Latest;
```

- [ ] **Step 8: Build-verify**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: Build succeeded, 0 warnings. (The App project builds on Windows in Task 7; it compiles cleanly there.)

Run the full suite to confirm nothing regressed: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj`
Expected: all existing tests still pass.

- [ ] **Step 9: Commit**

```
git add MeltdownMonitor.Mobile/Pipeline.cs MeltdownMonitor.App/Pipeline.cs
git commit -m "feat: track Hypoarousal-scalar velocity in both pipelines"
```

---

## Task 3: Collapse colour in both palettes

**Files:**
- Modify: `MeltdownMonitor.App/Regulation/MacchiatoPalette.cs`
- Modify: `MeltdownMonitor.Mobile/Controls/RegulationField.cs`

No unit test — colour constants are verified by build + live tuning (the exact shade is a first cut per the spec).

- [ ] **Step 1: Desktop — add the colour** (in `MacchiatoPalette.cs`, after the `Lavender` line, line 14)

```csharp
	/// <summary>Collapse / dorsal-vagal "shutdown" hue — a dim, desaturated slate-indigo that reads
	/// cold/withdrawn, deliberately distinct from the soft <see cref="Lavender"/> used for the
	/// window-of-tolerance and crossover. First-cut shade; live-tune against a real sensor.</summary>
	public static readonly Vector4 Slate = Hex(0x5d, 0x6a, 0x9e);
```

- [ ] **Step 2: Mobile — add the colour** (in `RegulationField.cs`, beside the existing `Lavender` colour field, line 78)

```csharp
	// Collapse / shutdown hue — dim slate-indigo, distinct from Lavender (window-of-tolerance + crossover).
	private static readonly Color Slate = Color.FromRgb(0x5d, 0x6a, 0x9e);
```

- [ ] **Step 3: Build-verify**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: Build succeeded, 0 warnings. (`Slate` is unused until Tasks 5/6; if warnings-as-errors flags the unused desktop field, it is consumed in Task 6 — implement Tasks 3→5→6 in sequence and commit Task 3 together with no intervening build gate on the App project. The Mobile `Slate` is consumed in Task 5. To avoid an unused-field error, add the colours in the same commit that first uses them if the analyzer objects; otherwise commit here.)

- [ ] **Step 4: Commit**

```
git add MeltdownMonitor.App/Regulation/MacchiatoPalette.cs MeltdownMonitor.Mobile/Controls/RegulationField.cs
git commit -m "feat: add Slate collapse colour to both palettes"
```

> **Note for the implementer:** unused `private`/`internal` fields can trip warnings-as-errors. If `dotnet build` errors on an unused `Slate`, fold this task's two additions into the first task that consumes each (Task 6 for desktop, Task 5 for mobile) rather than committing standalone. The colour values are unchanged either way.

---

## Task 4: Mobile ViewModel — consume `HypoarousalDynamics` (TDD)

**Files:**
- Modify: `MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs`
- Test: `MeltdownMonitor.Tests/NowViewModelTests.cs`

- [ ] **Step 1: Write the failing test** (append inside `NowViewModelTests`)

```csharp
	[TestMethod]
	public void HypoarousalDynamics_IsSteady_ByDefault()
	{
		var vm = new NowViewModel();
		Assert.AreEqual(RegulationDynamics.Steady, vm.HypoarousalDynamics);
	}

	[TestMethod]
	public void OnHypoarousalDynamicsUpdated_PublishesAFreshValue()
	{
		var vm = new NowViewModel();
		var rising = new RegulationDynamics(0.03, RegulationTrend.Escalating, 0.6);

		vm.OnHypoarousalDynamicsUpdated(rising);

		Assert.AreEqual(rising, vm.HypoarousalDynamics);
	}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~NowViewModelTests"`
Expected: FAIL to compile — `HypoarousalDynamics` / `OnHypoarousalDynamicsUpdated` don't exist.

- [ ] **Step 3: Implement** — in `NowViewModel.cs`

Add a backing field beside `_isShutdown` (line 38):

```csharp
	private RegulationDynamics _hypoarousalDynamics = RegulationDynamics.Steady;
```

Add the property beside the existing `Dynamics` property (search for `public RegulationDynamics Dynamics`):

```csharp
	/// <summary>Velocity/trend of the Hypoarousal scalar — the rate of approach to collapse —
	/// for the Regulation Field's collapse arrow. Bound to <c>RegulationField.HypoarousalDynamics</c>.</summary>
	public RegulationDynamics HypoarousalDynamics
	{
		get => _hypoarousalDynamics;
		private set => SetField(ref _hypoarousalDynamics, value);
	}
```

Add the handler beside `OnHypoarousalStateChanged` (line 486):

```csharp
	/// <summary>Reflect the Hypoarousal-scalar velocity from <see cref="Pipeline.HypoarousalDynamicsUpdated"/>.</summary>
	public void OnHypoarousalDynamicsUpdated(RegulationDynamics dynamics) =>
		RunOnUi(() => HypoarousalDynamics = dynamics);
```

Subscribe where the pipeline is attached — beside `pipeline.HypoarousalStateChanged += OnHypoarousalStateChanged;` (line 422):

```csharp
		pipeline.HypoarousalDynamicsUpdated += OnHypoarousalDynamicsUpdated;
```

And prime it next to the existing initial-state prime (`OnHypoarousalStateChanged(pipeline.CurrentHypoarousalState);`, line 431):

```csharp
		OnHypoarousalDynamicsUpdated(pipeline.LatestHypoarousalDynamics);
```

> If the pipeline subscriptions are torn down anywhere (an `Unsubscribe`/`Detach`/`Dispose` that does `-=`), mirror the new `+=` with a matching `-=` there. Search the file for `HypoarousalStateChanged -=`; add the parallel line only if that pattern exists.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~NowViewModelTests"`
Expected: PASS (existing + 2 new).

- [ ] **Step 5: Commit**

```
git add MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs MeltdownMonitor.Tests/NowViewModelTests.cs
git commit -m "feat: expose HypoarousalDynamics on NowViewModel"
```

---

## Task 5: Mobile renderer — zone, halo, arrow, labels (+ pulse rename)

Render code is **not** unit-tested (TFM + the BLE/visual rule); verify by build, and treat shade/opacity/radius/threshold as a first cut to **tune live with a real sensor**.

**Files:**
- Modify: `MeltdownMonitor.Mobile/Controls/RegulationField.cs`
- Modify: `MeltdownMonitor.Mobile/Views/NowView.axaml`

- [ ] **Step 1: Add the styled properties** — in `RegulationField.cs`, beside the existing `HeartRateProperty` / `Dynamics` properties (around lines 55–121)

```csharp
	public static readonly StyledProperty<double> HypoarousalProperty =
		AvaloniaProperty.Register<RegulationField, double>(nameof(Hypoarousal));

	public static readonly StyledProperty<RegulationDynamics> HypoarousalDynamicsProperty =
		AvaloniaProperty.Register<RegulationField, RegulationDynamics>(
			nameof(HypoarousalDynamics), RegulationDynamics.Steady);

	/// <summary>[0,1] low-arousal collapse signal driving the shutdown zone + marker halo.</summary>
	public double Hypoarousal
	{
		get => GetValue(HypoarousalProperty);
		set => SetValue(HypoarousalProperty, value);
	}

	/// <summary>Velocity/trend of the collapse signal; selects the hypoarousal-aware arrow.</summary>
	public RegulationDynamics HypoarousalDynamics
	{
		get => GetValue(HypoarousalDynamicsProperty);
		set => SetValue(HypoarousalDynamicsProperty, value);
	}
```

Ensure both properties trigger a redraw. Find the `static RegulationField()` constructor's `AffectsRender<RegulationField>(...)` call (it lists `ReadingProperty`, `DynamicsProperty`, etc.) and add `HypoarousalProperty, HypoarousalDynamicsProperty` to the argument list. If there is no `AffectsRender` registration, add:

```csharp
		AffectsRender<RegulationField>(HypoarousalProperty, HypoarousalDynamicsProperty);
```

- [ ] **Step 2: Draw the shutdown zone** — add a method and call it right after the window-of-tolerance ellipse (the `DrawEllipse(Brush(Lavender, 0.08 * confidence), ...)` at line 199)

```csharp
	// Upper-cool quadrant (cool side, low variability) = collapse territory. Fill it with Slate
	// at an opacity that tracks the collapse signal, so approach is visible before the latch.
	private void DrawShutdownZone(DrawingContext context, Vector2 centre, float halfWidth, float lobeHeight, double confidence)
	{
		double intensity = HypoarousalVisual.Intensity(Hypoarousal);
		if (intensity <= 0.0)
		{
			return;
		}

		// Cool (left) half, upper (fragile) band: a rectangle from the left edge to centre, above the axis.
		double top = centre.Y - (lobeHeight * 0.95);
		var rect = new Rect(centre.X - halfWidth, top, halfWidth, centre.Y - top);
		context.FillRectangle(Brush(Slate, 0.22 * intensity * confidence), rect);
	}
```

Add the call (with `using MeltdownMonitor.Core.Regulation;` already present — it is, for `RegulationDynamics`):

```csharp
		DrawShutdownZone(context, centreV, halfWidth, lobeHeight, confidence);
```

placed immediately after the existing window-of-tolerance ellipse draw (line 199), using the same `centre`/`lobeHeight` locals in scope there (match the parameter names already used by the neighbouring draw calls — `centreV` is the `Vector2` used by `DrawMarker` at line 206; if the WoT ellipse uses a `Point centre`, pass the `Vector2` form `centreV` as shown).

- [ ] **Step 3: Add the collapse halo** — in `DrawMarker` (lines 421–430), after the existing state halo (`context.DrawEllipse(Brush(StateColor, 0.18 * confidence), null, at, halo, halo);`, line 428) and before the core dot

```csharp
		// Outer collapse halo: Slate, non-pulsing, radius grows with the collapse signal. Layers
		// outside the pulsing state halo so the two read as distinct (different colour + motion).
		double collapse = HypoarousalVisual.Intensity(Hypoarousal);
		if (collapse > 0.0)
		{
			double ring = 14 + (10 * collapse);
			context.DrawEllipse(Brush(Slate, 0.30 * collapse * confidence), null, at, ring, ring);
		}
```

- [ ] **Step 4: Hypoarousal-aware velocity arrow** — in `DrawVelocityArrow` (lines 434+)

Locate where the method decides direction/colour from `Dynamics.Trend` (Escalating → Peach/right, DeEscalating → Sky/left). Wrap that logic so the collapse arrow takes precedence and the calming arrow is suppressed during collapse:

```csharp
		double scalar = Hypoarousal;

		// A rising collapse signal overrides the index arrow with a Slate WARNING toward the shutdown
		// (cool/upper-left) side — never let a slide into collapse read as a calming de-escalation.
		if (HypoarousalVisual.ShowCollapseArrow(scalar, HypoarousalDynamics))
		{
			DrawArrow(context, markerAt, dir: -1, hue: Slate, speed: HypoarousalDynamics.NormalizedSpeed, confidence);
			return;
		}

		// Otherwise the index arrow — but suppress it when it would contradict the shutdown zone.
		if (HypoarousalVisual.SuppressIndexArrow(scalar, Dynamics))
		{
			return;
		}

		// ... existing index-arrow drawing unchanged ...
```

Adapt the `DrawArrow(...)` call to the method's existing arrow-drawing helper/inline code (match its real signature — direction sign, hue/`Color`, magnitude source, and `confidence`). The contract: `dir: -1` points toward the cool/left (shutdown) side; `hue: Slate`; magnitude from `HypoarousalDynamics.NormalizedSpeed`. If arrow drawing is inline rather than a helper, duplicate the minimal inline draw with `Slate` and the leftward direction.

- [ ] **Step 5: Bind the new properties + recolour the shutdown line** — in `NowView.axaml`

Find the `<controls:RegulationField .../>` element (it already binds `Reading`, `Dynamics`, `HeartRate`, `StateColor`). Add:

```xml
                       Hypoarousal="{Binding Reading.Hypoarousal}"
                       HypoarousalDynamics="{Binding HypoarousalDynamics}"
```

(`Reading` is already bound; `Reading.Hypoarousal` reuses it. `HypoarousalDynamics` is the new VM property from Task 4.)

Recolour the existing shutdown line (`NowView.axaml:73`, "Low arousal · shutdown — not calm rest") from Lavender to the Slate collapse hue so it matches the field. Find its `Foreground="..."` and set it to the Slate value `#5D6A9E` (use the same literal the codebehind uses; XAML can't reference the C# `Slate` field, so use the hex):

```xml
                           Foreground="#5D6A9E"
```

If the line currently uses a Lavender resource/hex (`#B7BDF8`), replace it with `#5D6A9E`.

- [ ] **Step 6: Build-verify**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: Build succeeded, 0 warnings.

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj`
Expected: all tests pass (no render tests, but confirms nothing else broke).

- [ ] **Step 7: Commit**

```
git add MeltdownMonitor.Mobile/Controls/RegulationField.cs MeltdownMonitor.Mobile/Views/NowView.axaml
git commit -m "feat: render shutdown zone, collapse halo and hypoarousal arrow (mobile)"
```

---

## Task 6: Desktop renderer — zone, halo, arrow, labels (+ pulse rename, LF/HF comment fix)

Render code is **not** unit-tested. Build on Windows (to a temp `--output` so a running instance doesn't lock the DLLs); the visuals are a first cut to live-tune.

**Files:**
- Modify: `MeltdownMonitor.App/Regulation/RegulationFieldView.cs`

- [ ] **Step 1: Draw the shutdown zone** — add a method and call it after `DrawWindowOfTolerance` (line 206)

Add the method beside `DrawWindowOfTolerance` (after line 258):

```csharp
	// Upper-cool quadrant (cool side, fragile/low-variability) = collapse territory. Fill it with
	// Slate at an opacity tracking the collapse signal, so approach is visible before the latch.
	private void DrawShutdownZone(ImDrawListPtr draw, Vector2 centre, float halfWidth, float lobeHeight, RegulationReading r, float confidence)
	{
		double intensity = HypoarousalVisual.Intensity(r.Hypoarousal);
		if (intensity <= 0.0)
		{
			return;
		}

		// Left (cool) half, above the crossover line (fragile band).
		Vector2 tl = new(centre.X - halfWidth, centre.Y - (lobeHeight * 0.95f));
		Vector2 br = new(centre.X, centre.Y);
		draw.AddRectFilled(tl, br, Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Slate, (float)(0.22 * intensity) * confidence)));
	}
```

Add the call right after `DrawWindowOfTolerance(...)` (line 206):

```csharp
			DrawShutdownZone(draw, centre, halfWidth, baseLobeHeight, disp, confidence);
```

- [ ] **Step 2: Add the collapse halo** — in `DrawMarker` (lines 554–563), after the existing pulsing halo (line 560) and before the `6.5f` core circle (line 561)

```csharp
		// Outer collapse halo: Slate, non-pulsing, radius grows with the collapse signal — layered
		// outside the pulsing state halo so the two read as distinct (different colour + motion).
		double collapse = HypoarousalVisual.Intensity(disp.Hypoarousal);
		if (collapse > 0.0)
		{
			float ring = (16f + (10f * (float)collapse)) * _drawScale;
			draw.AddCircleFilled(p, ring, Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Slate, (float)(0.30 * collapse) * confidence)));
		}
```

- [ ] **Step 3: Hypoarousal-aware velocity arrow** — in `DrawVelocityArrow` (lines 525+)

The method already early-returns when `confidence < 0.999f || dyn.Trend == Steady || _arrowSpeed < 0.02f`. The desktop view reads the pipeline directly (`_pipeline`), so read the collapse signals there. Immediately after the existing early-return guard (line 530), insert:

```csharp
		double hypoScalar = disp.Hypoarousal;
		RegulationDynamics hypoDyn = _pipeline.LatestHypoarousalDynamics;

		// A rising collapse signal overrides with a Slate WARNING arrow toward the cool/upper-left
		// shutdown side; a slide into collapse must never read as a calming de-escalation.
		if (HypoarousalVisual.ShowCollapseArrow(hypoScalar, hypoDyn))
		{
			Vector2 cp = MarkerScreenPos(centre, halfWidth, liveLobeHeight, disp);
			DrawArrowHead(draw, cp, dir: -1f, hue: MacchiatoPalette.Slate, magnitude: (float)hypoDyn.NormalizedSpeed, confidence: confidence);
			return;
		}

		// Otherwise the existing index arrow — but suppress it when it would contradict the zone.
		if (HypoarousalVisual.SuppressIndexArrow(hypoScalar, dyn))
		{
			return;
		}
```

`DrawArrowHead` stands in for the method's existing arrow-drawing code (the `start`/`tip`/`AddLine`/`AddTriangleFilled` block at lines ~540–549). Either (a) extract that block into a private `DrawArrowHead(ImDrawListPtr draw, Vector2 from, float dir, Vector4 hue, float magnitude, float confidence)` and call it from both the existing path and the collapse path, or (b) inline the same draw with `dir = -1f` and `hue = MacchiatoPalette.Slate`. Extraction (a) is preferred (DRY). Match the existing `len`/`gap`/`head` scaling and `Col(...)`/`WithAlpha` usage.

- [ ] **Step 4: Split the cool-side label** — in `DrawLabelsAndLock`

Find where the cool pole is labelled `"REST"` (the cool/left pole text). Keep `REST` at the lower-cool position and add a `SHUTDOWN` zone label at the upper-cool position (above the crossover, left side). The existing latched-episode tag block (lines 699–707) already draws `"SHUTDOWN"` when `CurrentHypoarousalState == LowArousal`; this new label is the **always-present quadrant label** for the zone. To avoid two "SHUTDOWN" strings on screen during a latched episode, render the static quadrant label dimmer and only when not latched:

```csharp
		// Static quadrant label so the upper-cool region reads as collapse territory, not rest.
		// Suppressed during a latched episode (the brighter episode tag below takes over).
		if (_pipeline.CurrentHypoarousalState != HypoarousalState.LowArousal)
		{
			const string zoneTag = "SHUTDOWN";
			Vector2 zsz = ImGui.CalcTextSize(zoneTag);
			draw.AddText(
				new Vector2(centre.X - halfWidth - poleGap - zsz.X, midY - lineH - 2f),
				Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Slate, 0.6f * confidence)),
				zoneTag);
		}
```

Use the same `poleGap`/`midY`/`lineH` locals the existing tag block uses (lines 701–706). Place this near that block.

- [ ] **Step 5: Recolour the latched tag** — in the existing latched-episode block (lines 701–706), change the tag colour from `MacchiatoPalette.Lavender` to `MacchiatoPalette.Slate`:

```csharp
			draw.AddText(
				new Vector2(centre.X - halfWidth - poleGap - tagSize.X, midY + lineH + 2f),
				Col(MacchiatoPalette.Slate),
				tag);
```

- [ ] **Step 6: Cleanups in this file** (folded in — same file)

(a) Fix the stale LF/HF comment (lines 226–228). `UseLfHfCorroboration` defaults `true` (2026-06-01 audit flip), so the halo shows by default. Replace:

```csharp
	// Soft, asymmetric glow biased toward the dominant autonomic pole. Gated on the LF/HF
	// corroboration setting — LF/HF is noisy and off by default, so it only surfaces here
	// for users who have opted into trusting it.
```

with:

```csharp
	// Soft, asymmetric glow biased toward the dominant autonomic pole. Gated on the LF/HF
	// corroboration setting (on by default since the 2026-06-01 audit), and only once a real
	// LF/HF baseline exists — LF/HF is laggy/noisy, so it is a low-commitment lean cue, not a gate.
```

(b) Rename the HR-pulsed halo from "breathing" to "pulse" so the name matches what it is (it pulses at heart rate, not respiration). Rename the field `_breathPhase` → `_pulsePhase` (line 50) and update its two uses (lines 189, 559). Update the eased-HR comment (line 52, "smooth breathing cadence" → "smooth pulse cadence"), the update comment (line 186, "breathing pulse rate" → "pulse rate"), the RR-texture comment (line 192, "flows like the breathing pulse" → "flows like the pulse"), and the `DrawMarker` comment (line 553, "Breathing halo pulses at the current heart rate." → "Pulse halo pulses at the current heart rate."). Behaviour is unchanged.

- [ ] **Step 7: Build-verify (Windows)**

Run: `dotnet build MeltdownMonitor.App/MeltdownMonitor.App.csproj -o artifacts/plan-verify`
Expected: Build succeeded, 0 warnings. (Building to `-o artifacts/plan-verify` avoids locking a running app's DLLs.)

- [ ] **Step 8: Commit**

```
git add MeltdownMonitor.App/Regulation/RegulationFieldView.cs
git commit -m "feat: render shutdown zone, collapse halo and hypoarousal arrow (desktop)"
```

---

## Task 7: Mobile pulse rename + full gates

**Files:**
- Modify: `MeltdownMonitor.Mobile/Controls/RegulationField.cs`
- Modify: `MeltdownMonitor.Mobile/Controls/RegulationFieldAnimator.cs`

- [ ] **Step 1: Rename the mobile "breathing" cue to "pulse"**

In `RegulationFieldAnimator.cs`, the HR-driven halo factor is exposed as `HaloPulse` (already neutral — keep the name) but its comments call it "breathe(s)". In `RegulationField.cs` the doc comments say the halo "breathes at the current HR cadence" (lines 25–27, 35, 423–424). Replace "breathe(s)/breathing" with "pulse(s)" in those comments so the cue is described as the heartbeat-cadence indicator it is. In `RegulationFieldAnimator.cs`, do the same for any "breath"/"breathe" comment on the halo-pulse calculation. No identifier or behaviour change beyond comment wording (and any local named `breath*`, if present, → `pulse*`).

- [ ] **Step 2: Build-verify**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 3: Commit**

```
git add MeltdownMonitor.Mobile/Controls/RegulationField.cs MeltdownMonitor.Mobile/Controls/RegulationFieldAnimator.cs
git commit -m "refactor: name the HR halo a pulse cue, not breathing (mobile)"
```

- [ ] **Step 4: Full suite + Core gate**

Run: `dotnet build MeltdownMonitor.Core/MeltdownMonitor.Core.csproj`
Expected: Build succeeded, 0 warnings.

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj`
Expected: all tests pass (pre-existing + 7 new from Tasks 1 & 4).

- [ ] **Step 5: Windows App gate** (on a Windows machine)

Run: `dotnet build MeltdownMonitor.App/MeltdownMonitor.App.csproj -o artifacts/plan-verify`
Expected: Build succeeded, 0 warnings.

> iOS heads are untouched by this work; `ios.yml` on macOS/CI remains the gate for `Ble.Apple`/`iOS` and needs no change here.

---

## Live validation (post-merge, requires a real Polar sensor)

Per the BLE/visual rule, none of the render behaviour is gated by tests. After merge, with a sensor:
- Confirm the shutdown zone deepens smoothly as the collapse signal climbs (no flicker; opacity ramp readable on the dark overlay).
- Confirm the Slate collapse halo is distinguishable from the pulsing state halo, and that the two layers don't blur.
- Confirm a slide into collapse shows the Slate warning arrow (toward the cool/upper-left), never a calming Sky arrow.
- Tune: the `HypoarousalVisual.Floor`, zone opacity (`0.22`), halo opacity/radius (`0.30` / `+10`), and the `Slate` shade (`#5D6A9E`) are first-cut values.
- Separately, validate the provisional signature itself with `AnalyzeHypoarousal` against real `Shutdown` check-ins (out of scope here).

---

## Self-review

**Spec coverage:**
- Quadrant treatment (label cool+fragile SHUTDOWN vs cool+steady REST) → Task 5 (mobile), Task 6 (desktop labels). ✓
- Deepening zone + marker halo from the scalar → `HypoarousalVisual.Intensity` (Task 1) consumed in Tasks 5/6. ✓
- Hypoarousal-aware velocity arrow via a 2nd `RegulationVelocityTracker` → Task 2 (Core wiring), `ShowCollapseArrow`/`SuppressIndexArrow` (Task 1), consumed in Tasks 5/6. ✓
- Distinct collapse colour, Lavender kept on WoT/crossover → Task 3 + recolours in Tasks 5/6. ✓
- Both heads → Tasks 5 (mobile) + 6 (desktop); Core once (Tasks 1–2). ✓
- Halo-audit cleanups (pulse rename, LF/HF comment) → Task 6 (desktop), Task 7 (mobile). ✓
- Testing split (Core tested; render live-tuned) → Tasks 1 & 4 are TDD; Tasks 2/5/6/7 build-verified; live-validation section. ✓
- Clinical humility → preserved in existing copy; no confident-calm cue added (zone/halo only *raise* a candidate). ✓

**Type consistency:** `HypoarousalVisual.{Floor, Intensity, ShowCollapseArrow, SuppressIndexArrow}`, `LatestHypoarousalDynamics`, `HypoarousalDynamicsUpdated`, `_hypoVelocity`, `NowViewModel.{HypoarousalDynamics, OnHypoarousalDynamicsUpdated}`, `RegulationField.{Hypoarousal, HypoarousalDynamics}` (+ `*Property`), `MacchiatoPalette.Slate` / mobile `Slate` — names consistent across tasks. ✓

**Placeholders:** Render steps that adapt to existing inline code (`DrawArrowHead` extraction, the mobile arrow helper, the `AffectsRender`/pole-label/XAML `Foreground` lookups) are flagged as "match the existing signature/locals" with the exact contract specified — these are integration points in untested, live-tuned render files, not unspecified logic. All Core/VM logic is given in full. ✓
