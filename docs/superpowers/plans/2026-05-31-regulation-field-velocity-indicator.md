# Regulation Field velocity indicator — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a directional, velocity-aware indicator to the Regulation Field (desktop + mobile) that shows whether arousal is escalating, steady, or de-escalating, and how fast.

**Architecture:** A new stateful Core component `RegulationVelocityTracker` turns the per-sample `RegulationReading.Index` into a `RegulationDynamics(velocity, trend, normalizedSpeed)`. Each head's `Pipeline` owns one tracker, updates it once per usable (warm, in-contact) sample, and exposes `LatestDynamics`. Both renderers consume it as three layered visuals: a velocity-scaled comet-trail leading edge, a directional arrow on the marker, and a trend/rate readout. `RegulationReading` stays a pure single-sample value.

**Tech Stack:** C# / .NET 10, MSTest, Dear ImGui (desktop `RegulationFieldView`), Avalonia `DrawingContext` (mobile `RegulationField`).

**Spec:** `docs/superpowers/specs/2026-05-31-regulation-field-velocity-indicator-design.md`

**Branch:** `claude/regulation-field-velocity` (already rebased on current `main`).

---

## File structure

**Core (new):**
- `MeltdownMonitor.Core/Regulation/RegulationTrend.cs` — tri-state direction enum.
- `MeltdownMonitor.Core/Regulation/RegulationDynamics.cs` — `(Velocity, Trend, NormalizedSpeed)` readonly record struct + `Steady` default.
- `MeltdownMonitor.Core/Regulation/RegulationVelocityTracker.cs` — stateful derivative/EWMA/deadband/normalize, with seed-don't-emit and `Reset()`.

**Pipelines (modify):**
- `MeltdownMonitor.App/Pipeline.cs` — add tracker, `LatestDynamics`, gated update in `RunAsync`.
- `MeltdownMonitor.Mobile/Pipeline.cs` — same, plus a `DynamicsUpdated` event.

**Mobile UI (modify):**
- `MeltdownMonitor.Mobile/Controls/RegulationFieldAnimator.cs` — eased `DisplayedSpeed`.
- `MeltdownMonitor.Mobile/Controls/RegulationField.cs` — `Dynamics` styled property, arrow, trail tint.
- `MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs` — `Dynamics` + `OnDynamicsUpdated` + display getters.
- `MeltdownMonitor.Mobile/Views/NowView.axaml` — bind `Dynamics`, add readout.

**Desktop UI (modify):**
- `MeltdownMonitor.App/Regulation/RegulationFieldView.cs` — arrow, trail tint, readout, eased speed.

**Tests (new / modify):**
- `MeltdownMonitor.Tests/RegulationVelocityTrackerTests.cs` (new).
- `MeltdownMonitor.Tests/RegulationFieldAnimatorTests.cs` (add methods).
- `MeltdownMonitor.Tests/NowViewModelTests.cs` (add methods).

**Testability note:** Core (`RegulationVelocityTracker`), the mobile `RegulationFieldAnimator`, and `NowViewModel` getters are reachable by the test project → full TDD. The two renderers' draw code and the App pipeline are in `net10.0-windows`/draw-only code the test project can't reference → verified by clean build (under warnings-as-errors) + the live app on a real sensor (the only gate for real-time visual behaviour, per CLAUDE.md).

---

### Task 1: Core types — `RegulationTrend` + `RegulationDynamics`

**Files:**
- Create: `MeltdownMonitor.Core/Regulation/RegulationTrend.cs`
- Create: `MeltdownMonitor.Core/Regulation/RegulationDynamics.cs`
- Test: `MeltdownMonitor.Tests/RegulationVelocityTrackerTests.cs` (created here, expanded in Task 2)

- [ ] **Step 1: Write the failing test**

Create `MeltdownMonitor.Tests/RegulationVelocityTrackerTests.cs`:

```csharp
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

[TestClass]
public class RegulationVelocityTrackerTests
{
	private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

	[TestMethod]
	public void Steady_Default_IsZeroAndSteady()
	{
		var s = RegulationDynamics.Steady;
		Assert.AreEqual(0.0, s.Velocity, 1e-12);
		Assert.AreEqual(RegulationTrend.Steady, s.Trend);
		Assert.AreEqual(0.0, s.NormalizedSpeed, 1e-12);
	}
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RegulationVelocityTrackerTests"`
Expected: FAIL to compile — `RegulationDynamics` / `RegulationTrend` do not exist.

- [ ] **Step 3: Create the enum**

Create `MeltdownMonitor.Core/Regulation/RegulationTrend.cs`:

```csharp
namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Direction of change of the arousal index, with a deadband around zero so small
/// noise reads as <see cref="Steady"/> rather than flickering between the poles.
/// </summary>
public enum RegulationTrend
{
	DeEscalating,
	Steady,
	Escalating,
}
```

- [ ] **Step 4: Create the dynamics struct**

Create `MeltdownMonitor.Core/Regulation/RegulationDynamics.cs`:

```csharp
namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// The rate of change of a <see cref="RegulationReading.Index"/> over time, plus the
/// direction (with deadband) and a normalised magnitude for driving visuals. Produced
/// by <see cref="RegulationVelocityTracker"/>; <see cref="RegulationReading"/> itself
/// stays a pure single-sample value.
/// </summary>
/// <param name="Velocity">Signed d(Index)/dt in index-units per second (+ = escalating).</param>
/// <param name="Trend">Tri-state direction derived from <paramref name="Velocity"/> via a deadband.</param>
/// <param name="NormalizedSpeed">|Velocity| mapped to [0, 1] against a reference rate, for visual magnitude.</param>
public readonly record struct RegulationDynamics(
	double Velocity,
	RegulationTrend Trend,
	double NormalizedSpeed)
{
	/// <summary>A steady reading with no motion — the value before any sample, or while calibrating.</summary>
	public static RegulationDynamics Steady { get; } = new(0.0, RegulationTrend.Steady, 0.0);
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RegulationVelocityTrackerTests"`
Expected: PASS (1 test).

- [ ] **Step 6: Commit**

```bash
git add MeltdownMonitor.Core/Regulation/RegulationTrend.cs MeltdownMonitor.Core/Regulation/RegulationDynamics.cs MeltdownMonitor.Tests/RegulationVelocityTrackerTests.cs
git commit -m "[minor] feat: add RegulationTrend and RegulationDynamics Core types"
```

---

### Task 2: `RegulationVelocityTracker` (full TDD)

**Files:**
- Create: `MeltdownMonitor.Core/Regulation/RegulationVelocityTracker.cs`
- Test: `MeltdownMonitor.Tests/RegulationVelocityTrackerTests.cs` (add methods)

- [ ] **Step 1: Write the failing tests**

Add these methods to `RegulationVelocityTrackerTests.cs` (inside the class):

```csharp
	[TestMethod]
	public void Latest_BeforeAnyUpdate_IsSteady()
	{
		var t = new RegulationVelocityTracker();
		Assert.AreEqual(RegulationDynamics.Steady, t.Latest);
	}

	[TestMethod]
	public void FirstUpdate_Seeds_ReturnsSteadyEvenForLargeIndex()
	{
		var t = new RegulationVelocityTracker();
		var d = t.Update(0.9, T0);
		Assert.AreEqual(RegulationTrend.Steady, d.Trend);
		Assert.AreEqual(0.0, d.Velocity, 1e-12);
		Assert.AreEqual(0.0, d.NormalizedSpeed, 1e-12);
	}

	[TestMethod]
	public void RisingIndex_IsEscalating_WithPositiveVelocity()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.0, T0);                              // seed
		var d = t.Update(0.3, T0.AddSeconds(5));        // raw = 0.06/s; EWMA from 0 -> 0.03/s
		Assert.AreEqual(RegulationTrend.Escalating, d.Trend);
		Assert.AreEqual(0.03, d.Velocity, 1e-9);
		Assert.AreEqual(0.6, d.NormalizedSpeed, 1e-9);  // 0.03 / 0.05 reference
	}

	[TestMethod]
	public void FallingIndex_IsDeEscalating_WithNegativeVelocity()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.5, T0);
		var d = t.Update(0.2, T0.AddSeconds(5));        // raw = -0.06/s; EWMA -> -0.03/s
		Assert.AreEqual(RegulationTrend.DeEscalating, d.Trend);
		Assert.AreEqual(-0.03, d.Velocity, 1e-9);
		Assert.AreEqual(0.6, d.NormalizedSpeed, 1e-9);
	}

	[TestMethod]
	public void SmallChangeWithinDeadband_IsSteady()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.0, T0);
		var d = t.Update(0.02, T0.AddSeconds(5));       // raw = 0.004/s; EWMA -> 0.002/s < 0.01 deadband
		Assert.AreEqual(RegulationTrend.Steady, d.Trend);
		Assert.AreEqual(0.002, d.Velocity, 1e-9);
	}

	[TestMethod]
	public void Velocity_ConvergesAndNormalizedSpeedClampsToOne()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.0, T0);
		double index = 0.0;
		var when = T0;
		RegulationDynamics d = default;
		for (int i = 0; i < 10; i++)
		{
			index += 0.3;                               // constant raw rate 0.06/s
			when = when.AddSeconds(5);
			d = t.Update(index, when);
		}

		Assert.AreEqual(RegulationTrend.Escalating, d.Trend);
		Assert.IsTrue(d.Velocity > 0.05 && d.Velocity <= 0.06 + 1e-9, $"velocity converging to 0.06, was {d.Velocity}");
		Assert.AreEqual(1.0, d.NormalizedSpeed, 1e-9);  // 0.06/0.05 > 1 -> clamped
	}

	[TestMethod]
	public void ShortInterval_IsClampedToMinDt()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.0, T0);
		var d = t.Update(0.3, T0.AddSeconds(0.1));      // dt clamped 0.1 -> 0.5; raw = 0.6/s; EWMA -> 0.3/s
		Assert.AreEqual(0.3, d.Velocity, 1e-9);         // not 1.5 (which an unclamped 0.1 s would give)
	}

	[TestMethod]
	public void LongGap_IsClampedToMaxDt()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.0, T0);
		var d = t.Update(0.3, T0.AddSeconds(600));      // dt clamped 600 -> 30; raw = 0.01/s; EWMA -> 0.005/s
		Assert.AreEqual(0.005, d.Velocity, 1e-9);
		Assert.AreEqual(RegulationTrend.Steady, d.Trend);
	}

	[TestMethod]
	public void Reset_ThenUpdate_ReseedsWithoutSpike()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.0, T0);
		t.Update(0.3, T0.AddSeconds(5));                // escalating
		t.Reset();
		var d = t.Update(0.9, T0.AddSeconds(10));       // big jump, but post-reset -> seed -> Steady/0
		Assert.AreEqual(RegulationTrend.Steady, d.Trend);
		Assert.AreEqual(0.0, d.Velocity, 1e-12);
		Assert.AreEqual(0.0, d.NormalizedSpeed, 1e-12);
	}

	[TestMethod]
	public void NonFiniteIndex_IsIgnored_ReturnsLastDynamics()
	{
		var t = new RegulationVelocityTracker();
		t.Update(0.0, T0);
		var prev = t.Update(0.3, T0.AddSeconds(5));
		var d = t.Update(double.NaN, T0.AddSeconds(10));
		Assert.AreEqual(prev, d);
	}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RegulationVelocityTrackerTests"`
Expected: FAIL to compile — `RegulationVelocityTracker` does not exist.

- [ ] **Step 3: Implement the tracker**

Create `MeltdownMonitor.Core/Regulation/RegulationVelocityTracker.cs`:

```csharp
namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Tracks the velocity (rate of change) of the arousal index across successive readings
/// and classifies it into an escalating / steady / de-escalating trend. Stateful: it
/// remembers the previous index and timestamp, EWMA-smooths the derivative, and applies a
/// deadband for the trend. Single-threaded; the owning pipeline updates it once per sample.
///
/// Tuning constants are initial estimates to validate against a live sensor (RR data is
/// batched, so real-time feel is only gated by the running app — see CLAUDE.md).
/// </summary>
public sealed class RegulationVelocityTracker
{
	// EWMA weight for the per-sample derivative (~2-sample memory at the 5 s emit cadence).
	private const double SmoothingAlpha = 0.5;
	// |velocity| (index-units/s) below this is treated as Steady — hysteresis around zero.
	private const double TrendDeadband = 0.01;
	// |velocity| that maps to full visual magnitude (~ baseline->saturate in ~20 s).
	private const double ReferenceSpeed = 0.05;
	// dt clamp, matching the desktop view's inter-sample interval clamp.
	private const double MinDtSeconds = 0.5;
	private const double MaxDtSeconds = 30.0;

	private bool _seeded;
	private double _prevIndex;
	private DateTimeOffset _prevTimestamp;
	private double _velocity;

	/// <summary>The latest computed dynamics. <see cref="RegulationDynamics.Steady"/> before the first update.</summary>
	public RegulationDynamics Latest { get; private set; } = RegulationDynamics.Steady;

	/// <summary>
	/// Folds a new reading index into the velocity estimate and returns the updated dynamics.
	/// The first call after construction or <see cref="Reset"/> only seeds the previous sample
	/// and returns <see cref="RegulationDynamics.Steady"/> — so the cold->warm jump in index
	/// (0 -> real value) never registers as a spurious spike. Non-finite inputs are ignored.
	/// </summary>
	public RegulationDynamics Update(double index, DateTimeOffset timestamp)
	{
		if (!double.IsFinite(index))
		{
			return Latest;
		}

		if (!_seeded)
		{
			_seeded = true;
			_prevIndex = index;
			_prevTimestamp = timestamp;
			_velocity = 0.0;
			Latest = RegulationDynamics.Steady;
			return Latest;
		}

		double dt = Math.Clamp((timestamp - _prevTimestamp).TotalSeconds, MinDtSeconds, MaxDtSeconds);
		double rawVelocity = (index - _prevIndex) / dt;
		_velocity = (SmoothingAlpha * rawVelocity) + ((1.0 - SmoothingAlpha) * _velocity);

		_prevIndex = index;
		_prevTimestamp = timestamp;

		RegulationTrend trend = _velocity > TrendDeadband ? RegulationTrend.Escalating
			: _velocity < -TrendDeadband ? RegulationTrend.DeEscalating
			: RegulationTrend.Steady;
		double normalizedSpeed = Math.Clamp(Math.Abs(_velocity) / ReferenceSpeed, 0.0, 1.0);

		Latest = new RegulationDynamics(_velocity, trend, normalizedSpeed);
		return Latest;
	}

	/// <summary>
	/// Forgets the previous sample so the next <see cref="Update"/> re-seeds (returns Steady)
	/// rather than computing a derivative across a gap — used when the sensor goes off-contact
	/// or the baseline is not yet warm, so the resumed stream doesn't produce a spurious spike.
	/// </summary>
	public void Reset()
	{
		_seeded = false;
		_prevIndex = 0.0;
		_velocity = 0.0;
		Latest = RegulationDynamics.Steady;
	}
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RegulationVelocityTrackerTests"`
Expected: PASS (11 tests).

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Core/Regulation/RegulationVelocityTracker.cs MeltdownMonitor.Tests/RegulationVelocityTrackerTests.cs
git commit -m "feat: add RegulationVelocityTracker (escalation velocity + trend)"
```

---

### Task 3: Wire dynamics into the Mobile pipeline

**Files:**
- Modify: `MeltdownMonitor.Mobile/Pipeline.cs`

- [ ] **Step 1: Add the tracker field, property, and event**

In `MeltdownMonitor.Mobile/Pipeline.cs`, add a field alongside the other readonly members (after `private readonly DysregulationDetector _detector;`):

```csharp
	private readonly RegulationVelocityTracker _velocity = new();
```

Add a property after `LatestReading`:

```csharp
	/// <summary>Latest escalation/de-escalation velocity + trend of the arousal index.
	/// <see cref="RegulationDynamics.Steady"/> until the baseline is warm.</summary>
	public RegulationDynamics LatestDynamics { get; private set; } = RegulationDynamics.Steady;
```

Add an event after `ReadingUpdated`:

```csharp
	/// <summary>Fires after <see cref="ReadingUpdated"/> with the velocity/trend of the
	/// arousal index, derived from the same sample. Steady while calibrating or off-contact.</summary>
	public event Action<RegulationDynamics>? DynamicsUpdated;
```

(`MeltdownMonitor.Core.Regulation` is already imported in this file.)

- [ ] **Step 2: Update the tracker in `RunAsync`**

In `RunAsync`, immediately after the existing `ReadingUpdated?.Invoke(reading);` line, add:

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

- [ ] **Step 3: Build and run the full suite**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj`
Expected: PASS — all existing tests plus the Task 2 tests still green; Mobile compiles under warnings-as-errors.

- [ ] **Step 4: Commit**

```bash
git add MeltdownMonitor.Mobile/Pipeline.cs
git commit -m "feat: expose LatestDynamics + DynamicsUpdated from the mobile pipeline"
```

---

### Task 4: Wire dynamics into the App pipeline

**Files:**
- Modify: `MeltdownMonitor.App/Pipeline.cs`

- [ ] **Step 1: Add the tracker field and property**

In `MeltdownMonitor.App/Pipeline.cs`, add a field after `private readonly DysregulationDetector _detector;`:

```csharp
	private readonly RegulationVelocityTracker _velocity = new();
```

Add a property after the existing `LatestReading` property:

```csharp
	/// <summary>Latest escalation/de-escalation velocity + trend of the arousal index.
	/// <see cref="RegulationDynamics.Steady"/> until the baseline is warm.</summary>
	public RegulationDynamics LatestDynamics { get; private set; } = RegulationDynamics.Steady;
```

(`MeltdownMonitor.Core.Regulation` is already imported in this file.)

- [ ] **Step 2: Update the tracker in `RunAsync`**

In `RunAsync`, immediately after the existing `LatestReading = RegulationFieldCalculator.Compute(...)` assignment, add:

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

- [ ] **Step 3: Build (App is not test-referenced)**

Run: `dotnet build MeltdownMonitor.App/MeltdownMonitor.App.csproj`
Expected: Build succeeded, 0 errors (under warnings-as-errors). If the build fails with `MSB3021` file locks, the desktop app is running — close it (or build to a throwaway dir: append `-o "$env:TEMP/mm_app_verify"`).

- [ ] **Step 4: Commit**

```bash
git add MeltdownMonitor.App/Pipeline.cs
git commit -m "feat: expose LatestDynamics from the desktop pipeline"
```

---

### Task 5: Eased `DisplayedSpeed` on the mobile animator (TDD)

**Files:**
- Modify: `MeltdownMonitor.Mobile/Controls/RegulationFieldAnimator.cs`
- Test: `MeltdownMonitor.Tests/RegulationFieldAnimatorTests.cs` (add methods)

- [ ] **Step 1: Write the failing tests**

Add these methods to `RegulationFieldAnimatorTests.cs` (inside the class):

```csharp
	[TestMethod]
	public void Step_EasesDisplayedSpeedTowardTarget()
	{
		var a = new RegulationFieldAnimator();

		a.Step(0.033, 0.0, 70, targetSpeed: 1.0);
		Assert.IsTrue(a.DisplayedSpeed > 0.0 && a.DisplayedSpeed < 1.0,
			$"speed should advance part-way, was {a.DisplayedSpeed}");

		for (int i = 0; i < 300; i++)
		{
			a.Step(0.033, 0.0, 70, targetSpeed: 1.0);
		}

		Assert.AreEqual(1.0, a.DisplayedSpeed, 1e-3);
	}

	[TestMethod]
	public void Step_DisplayedSpeedEasesBackToZero()
	{
		var a = new RegulationFieldAnimator();
		for (int i = 0; i < 300; i++)
		{
			a.Step(0.033, 0.0, 70, targetSpeed: 1.0);
		}

		for (int i = 0; i < 300; i++)
		{
			a.Step(0.033, 0.0, 70, targetSpeed: 0.0);
		}

		Assert.AreEqual(0.0, a.DisplayedSpeed, 1e-3);
	}

	[TestMethod]
	public void Step_ClampsTargetSpeedToUnitRange()
	{
		var a = new RegulationFieldAnimator();
		for (int i = 0; i < 300; i++)
		{
			a.Step(0.033, 0.0, 70, targetSpeed: 2.0);   // over-range
		}

		Assert.AreEqual(1.0, a.DisplayedSpeed, 1e-3);
	}

	[TestMethod]
	public void Step_NonFiniteTargetSpeed_HoldsDisplayedSpeed()
	{
		var a = new RegulationFieldAnimator();
		a.Step(0.033, 0.0, 70, targetSpeed: 0.5);
		double held = a.DisplayedSpeed;

		a.Step(0.033, 0.0, 70, targetSpeed: double.NaN);

		Assert.AreEqual(held, a.DisplayedSpeed, 1e-9);
	}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RegulationFieldAnimatorTests"`
Expected: FAIL to compile — `Step` has no `targetSpeed` parameter and `DisplayedSpeed` does not exist.

- [ ] **Step 3: Implement the eased speed**

In `RegulationFieldAnimator.cs`, add a constant next to `MarkerEaseRate`:

```csharp
	private const double SpeedEaseRate = 6.0;      // matches the marker ease so the arrow grows/shrinks in step
```

Add a property next to `MarkerPos`:

```csharp
	/// <summary>Eased magnitude of the velocity arrow, glided toward the latest
	/// <c>RegulationDynamics.NormalizedSpeed</c> so it grows/shrinks smoothly.</summary>
	public double DisplayedSpeed { get; private set; }
```

Change the `Step` signature and body to add `targetSpeed` (default 0 keeps existing callers valid). Replace the existing `Step` method with:

```csharp
	public void Step(double dt, double targetIndex, double heartRate, double targetSpeed = 0.0)
	{
		if (!double.IsFinite(dt) || dt <= 0.0)
		{
			return;
		}

		dt = Math.Min(dt, MaxStepSeconds);

		double target = double.IsFinite(targetIndex) ? targetIndex : MarkerPos;
		MarkerPos += (target - MarkerPos) * (1.0 - Math.Exp(-dt * MarkerEaseRate));

		double speedTarget = double.IsFinite(targetSpeed) ? Math.Clamp(targetSpeed, 0.0, 1.0) : DisplayedSpeed;
		DisplayedSpeed += (speedTarget - DisplayedSpeed) * (1.0 - Math.Exp(-dt * SpeedEaseRate));

		double bpm = Math.Max(MinBreathBpm, double.IsFinite(heartRate) ? heartRate : 0.0);
		BreathPhase = (BreathPhase + (dt * (bpm / 60.0) * Math.Tau)) % Math.Tau;

		AnimTime += dt;
	}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RegulationFieldAnimatorTests"`
Expected: PASS — the 4 new tests plus all existing animator tests (which call the 3-arg `Step` and don't assert `DisplayedSpeed`).

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Mobile/Controls/RegulationFieldAnimator.cs MeltdownMonitor.Tests/RegulationFieldAnimatorTests.cs
git commit -m "feat: ease a DisplayedSpeed on the regulation field animator"
```

---

### Task 6: Arrow + trail tint on the mobile `RegulationField` control

**Files:**
- Modify: `MeltdownMonitor.Mobile/Controls/RegulationField.cs`

- [ ] **Step 1: Add the `Dynamics` styled property**

In `RegulationField.cs`, add after the `HeartRateProperty` registration:

```csharp
	public static readonly StyledProperty<RegulationDynamics> DynamicsProperty =
		AvaloniaProperty.Register<RegulationField, RegulationDynamics>(
			nameof(Dynamics), RegulationDynamics.Steady);
```

Add the property accessor after the `HeartRate` property:

```csharp
	/// <summary>Latest escalation/de-escalation velocity + trend; drives the marker's
	/// direction arrow and tints the trail's leading edge.</summary>
	public RegulationDynamics Dynamics
	{
		get => GetValue(DynamicsProperty);
		set => SetValue(DynamicsProperty, value);
	}
```

Add `DynamicsProperty` to the `AffectsRender` registration in the static constructor:

```csharp
	static RegulationField() =>
		AffectsRender<RegulationField>(ReadingProperty, TrailProperty, StateColorProperty, DynamicsProperty);
```

- [ ] **Step 2: Feed the target speed to the animator**

In `OnFrame`, replace the `_animator.Step(...)` call with:

```csharp
		_animator.Step(dt, Reading.Index, HeartRate, Dynamics.NormalizedSpeed);
```

- [ ] **Step 3: Draw the arrow from the marker**

In `DrawMarker`, after the three existing `context.DrawEllipse(...)` calls (halo/core/pupil), append a call to a new helper:

```csharp
		DrawVelocityArrow(context, at, confidence);
```

Then add the helper method to the class (e.g. directly after `DrawMarker`):

```csharp
	private void DrawVelocityArrow(DrawingContext context, Point markerAt, double confidence)
	{
		var dyn = Dynamics;
		double speed = _animator.DisplayedSpeed;
		if (confidence < 0.999 || dyn.Trend == RegulationTrend.Steady || speed < 0.02)
		{
			return;
		}

		double dir = dyn.Trend == RegulationTrend.Escalating ? 1.0 : -1.0;
		Color hue = dyn.Trend == RegulationTrend.Escalating ? Peach : Sky;
		double alpha = confidence * (0.35 + (0.65 * speed));

		double gap = 12.0;
		double len = 10.0 + (speed * 46.0);
		var start = new Point(markerAt.X + (dir * gap), markerAt.Y);
		var tip = new Point(start.X + (dir * len), start.Y);
		context.DrawLine(new Pen(Brush(hue, alpha), 3), start, tip);

		const double head = 7.0;
		var geo = new StreamGeometry();
		using (var g = geo.Open())
		{
			g.BeginFigure(tip, isFilled: true);
			g.LineTo(new Point(tip.X - (dir * head), tip.Y - (head * 0.7)));
			g.LineTo(new Point(tip.X - (dir * head), tip.Y + (head * 0.7)));
			g.EndFigure(isClosed: true);
		}

		context.DrawGeometry(Brush(hue, alpha), null, geo);
	}
```

- [ ] **Step 4: Tint the trail's leading edge by trend**

In `DrawTrail`, replace the loop body's colour with a trend-tinted leading edge. Replace:

```csharp
			double radius = 1.5 + (3.0 * frac);
			context.DrawEllipse(Brush(StateColor, 0.5 * frac * confidence), null, P(p), radius, radius);
```

with:

```csharp
			double radius = 1.5 + (3.0 * frac);
			// Leading edge (newest, frac->1) brightens with speed and tints by trend so the
			// comet visibly "leans" the way arousal is heading; the tail stays the state colour.
			Color tint = Dynamics.Trend switch
			{
				RegulationTrend.Escalating => Lerp(StateColor, Peach, frac * _animator.DisplayedSpeed),
				RegulationTrend.DeEscalating => Lerp(StateColor, Sky, frac * _animator.DisplayedSpeed),
				_ => StateColor,
			};
			double alpha = (0.5 + (0.3 * _animator.DisplayedSpeed)) * frac * confidence;
			context.DrawEllipse(Brush(tint, alpha), null, P(p), radius, radius);
```

- [ ] **Step 5: Build the mobile project**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: Build succeeded, 0 errors (under warnings-as-errors). (`StreamGeometry`, `Pen`, `Color` are already available via the file's `using Avalonia.Media;`.)

- [ ] **Step 6: Commit**

```bash
git add MeltdownMonitor.Mobile/Controls/RegulationField.cs
git commit -m "feat: draw velocity arrow + trend-tinted trail on the mobile field"
```

---

### Task 7: `Dynamics` + display getters on `NowViewModel` (TDD)

**Files:**
- Modify: `MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs`
- Test: `MeltdownMonitor.Tests/NowViewModelTests.cs` (add methods)

- [ ] **Step 1: Write the failing tests**

Add these methods to `NowViewModelTests.cs` (inside the class):

```csharp
	[TestMethod]
	public void Dynamics_IsSteady_ByDefault()
	{
		var vm = new NowViewModel();
		Assert.AreEqual(RegulationDynamics.Steady, vm.Dynamics);
		Assert.AreEqual("Steady", vm.TrendLabel);
		Assert.AreEqual("steady", vm.VelocityText);
		Assert.AreEqual(0.0, vm.NormalizedSpeed, 1e-12);
		Assert.IsFalse(vm.IsTrendVisible);
	}

	[TestMethod]
	public void OnDynamicsUpdated_Escalating_SetsLabelVelocityAndVisibility()
	{
		var vm = new NowViewModel();
		// A confident reading is required for the trend to be visible.
		vm.OnReadingUpdated(new RegulationReading(0.2, 0.8, 1.0, 0.5, 0.0));

		vm.OnDynamicsUpdated(new RegulationDynamics(0.03, RegulationTrend.Escalating, 0.6));

		Assert.AreEqual("Escalating", vm.TrendLabel);
		Assert.AreEqual("+0.03 /s", vm.VelocityText);
		Assert.AreEqual(0.6, vm.NormalizedSpeed, 1e-9);
		Assert.IsTrue(vm.IsTrendVisible);
	}

	[TestMethod]
	public void OnDynamicsUpdated_DeEscalating_FormatsNegativeRate()
	{
		var vm = new NowViewModel();
		vm.OnReadingUpdated(new RegulationReading(0.2, 0.8, 1.0, 0.5, 0.0));

		vm.OnDynamicsUpdated(new RegulationDynamics(-0.03, RegulationTrend.DeEscalating, 0.6));

		Assert.AreEqual("Easing", vm.TrendLabel);
		Assert.AreEqual("-0.03 /s", vm.VelocityText);
	}

	[TestMethod]
	public void IsTrendVisible_IsFalse_WhileCalibrating()
	{
		var vm = new NowViewModel();
		// Low confidence (baseline still warming) hides the trend even if escalating.
		vm.OnReadingUpdated(new RegulationReading(0.0, 1.0, 0.4, 0.5, 0.0));
		vm.OnDynamicsUpdated(new RegulationDynamics(0.03, RegulationTrend.Escalating, 0.6));

		Assert.IsFalse(vm.IsTrendVisible);
	}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~NowViewModelTests"`
Expected: FAIL to compile — `Dynamics`, `OnDynamicsUpdated`, `TrendLabel`, `VelocityText`, `NormalizedSpeed`, `IsTrendVisible` do not exist.

- [ ] **Step 3: Implement the property, handler, and getters**

In `NowViewModel.cs`, add a backing field next to `_reading`:

```csharp
	private RegulationDynamics _dynamics = RegulationDynamics.Steady;
```

Add the property and getters after the `Reading` property:

```csharp
	/// <summary>Latest escalation/de-escalation velocity + trend driving the field's arrow.</summary>
	public RegulationDynamics Dynamics
	{
		get => _dynamics;
		private set
		{
			if (SetField(ref _dynamics, value))
			{
				Raise(nameof(TrendLabel));
				Raise(nameof(VelocityText));
				Raise(nameof(NormalizedSpeed));
				Raise(nameof(IsTrendVisible));
			}
		}
	}

	/// <summary>Human-readable trend word for the readout.</summary>
	public string TrendLabel => _dynamics.Trend switch
	{
		RegulationTrend.Escalating => "Escalating",
		RegulationTrend.DeEscalating => "Easing",
		_ => "Steady",
	};

	/// <summary>Signed rate for the readout, or "steady" inside the deadband.</summary>
	public string VelocityText => _dynamics.Trend == RegulationTrend.Steady
		? "steady"
		: $"{_dynamics.Velocity:+0.00;-0.00} /s";

	/// <summary>[0,1] magnitude for the readout bar.</summary>
	public double NormalizedSpeed => _dynamics.NormalizedSpeed;

	/// <summary>Whether to show the trend readout — only when moving and the baseline is warm.</summary>
	public bool IsTrendVisible => _dynamics.Trend != RegulationTrend.Steady && _reading.Confidence >= 0.999;
```

Add the handler next to `OnReadingUpdated`:

```csharp
	/// <summary>
	/// Push fresh velocity/trend dynamics into the VM. Wired to
	/// <see cref="Pipeline.DynamicsUpdated"/> and marshalled to the UI thread like the
	/// other handlers. Public so tests can drive it without a live pipeline.
	/// </summary>
	public void OnDynamicsUpdated(RegulationDynamics dynamics) => RunOnUi(() => Dynamics = dynamics);
```

Wire it in `AttachPipeline`, after the existing `pipeline.ReadingUpdated += OnReadingUpdated;` line:

```csharp
		pipeline.DynamicsUpdated += OnDynamicsUpdated;
```

Also raise `IsTrendVisible` when a reading arrives (confidence gates it). In `OnReadingUpdated`, after `Reading = reading;`, add:

```csharp
			Raise(nameof(IsTrendVisible));
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~NowViewModelTests"`
Expected: PASS — the 4 new tests plus all existing `NowViewModelTests`.

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs MeltdownMonitor.Tests/NowViewModelTests.cs
git commit -m "feat: expose velocity trend + readout strings on NowViewModel"
```

---

### Task 8: Bind dynamics + readout in `NowView.axaml`

**Files:**
- Modify: `MeltdownMonitor.Mobile/Views/NowView.axaml`

- [ ] **Step 1: Bind the control's `Dynamics`**

In `NowView.axaml`, add the `Dynamics` binding to the `RegulationField` element (after its `StateColor` binding):

```xml
                    <ctl:RegulationField Grid.Row="0"
                                         MinHeight="200"
                                         Reading="{Binding Reading}"
                                         Trail="{Binding RegulationTrail}"
                                         HeartRate="{Binding HeartRate}"
                                         StateColor="{Binding RegulationStateColor}"
                                         Dynamics="{Binding Dynamics}" />
```

- [ ] **Step 2: Add the trend readout**

Add a trend readout `TextBlock` inside the vertical `StackPanel` (Grid.Row="2"), after the `BaselineText` block and before the contact-lost block:

```xml
                <StackPanel Orientation="Horizontal"
                            HorizontalAlignment="Center"
                            Spacing="8"
                            IsVisible="{Binding IsTrendVisible}">
                    <TextBlock Text="{Binding TrendLabel}"
                               Foreground="#CAD3F5"
                               FontSize="13"
                               FontWeight="SemiBold" />
                    <TextBlock Text="{Binding VelocityText}"
                               Foreground="#8A8F98"
                               FontSize="13" />
                    <ProgressBar Width="60"
                                 Minimum="0"
                                 Maximum="1"
                                 Value="{Binding NormalizedSpeed}"
                                 VerticalAlignment="Center" />
                </StackPanel>
```

- [ ] **Step 3: Build the mobile project**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: Build succeeded, 0 errors. (Compiled XAML binding validates `Dynamics`/`IsTrendVisible`/`TrendLabel`/`VelocityText`/`NormalizedSpeed` against `NowViewModel` because `x:DataType` is set.)

- [ ] **Step 4: Commit**

```bash
git add MeltdownMonitor.Mobile/Views/NowView.axaml
git commit -m "feat: bind velocity arrow + trend readout in the Now view"
```

---

### Task 9: Arrow + trail tint + readout on the desktop `RegulationFieldView`

**Files:**
- Modify: `MeltdownMonitor.App/Regulation/RegulationFieldView.cs`

- [ ] **Step 1: Add the eased-speed field**

In `RegulationFieldView.cs`, add to the animation-state region (next to `_breathPhase`):

```csharp
	private float _arrowSpeed;               // eased displayed normalized speed for the velocity arrow
```

Add `using MeltdownMonitor.Core.Regulation;` is already present (the file uses `RegulationReading`/`RegulationFieldCalculator`).

- [ ] **Step 2: Ease the arrow speed in `Draw`**

In `Draw()`, after the existing HR-easing block (`_hrDisplay += ...; _breathPhase += ...;`), add:

```csharp
		// Ease the arrow magnitude toward the latest dynamics so it grows/shrinks smoothly.
		var dynamics = _pipeline.LatestDynamics;
		_arrowSpeed += ((float)dynamics.NormalizedSpeed - _arrowSpeed) * (1f - MathF.Exp(-dt * 6f));
```

- [ ] **Step 3: Extract a marker-position helper and draw the arrow**

Add a helper method (e.g. after `DrawMarker`):

```csharp
	private static Vector2 MarkerScreenPos(Vector2 centre, float halfWidth, float liveLobeHeight, RegulationReading disp)
	{
		Vector2 p = LemniscateGeometry.MarkerPoint((float)disp.Index, centre, halfWidth);
		float clamp = liveLobeHeight * MarkerYSpan;
		float yOff = ((float)disp.VariabilityQuality - 0.5f) * liveLobeHeight * MarkerYSpan;
		p.Y += Math.Clamp(yOff, -clamp, clamp);
		return p;
	}

	private void DrawVelocityArrow(ImDrawListPtr draw, Vector2 centre, float halfWidth, float liveLobeHeight, RegulationReading disp, RegulationDynamics dyn, float confidence)
	{
		if (confidence < 0.999f || dyn.Trend == RegulationTrend.Steady || _arrowSpeed < 0.02f)
		{
			return;
		}

		Vector2 p = MarkerScreenPos(centre, halfWidth, liveLobeHeight, disp);
		float dir = dyn.Trend == RegulationTrend.Escalating ? 1f : -1f;
		Vector4 hue = dyn.Trend == RegulationTrend.Escalating ? MacchiatoPalette.Peach : MacchiatoPalette.Sky;
		float alpha = confidence * (0.35f + (0.65f * _arrowSpeed));
		uint col = Col(MacchiatoPalette.WithAlpha(hue, alpha));

		float gap = 12f;
		float len = 10f + (_arrowSpeed * 46f);
		Vector2 start = p + new Vector2(dir * gap, 0f);
		Vector2 tip = start + new Vector2(dir * len, 0f);
		draw.AddLine(start, tip, col, 3f);

		const float head = 7f;
		draw.AddTriangleFilled(
			tip,
			tip + new Vector2(-dir * head, -head * 0.7f),
			tip + new Vector2(-dir * head, head * 0.7f),
			col);
	}
```

Refactor `DrawMarker` to use the shared helper — replace its first three lines:

```csharp
		Vector2 p = LemniscateGeometry.MarkerPoint((float)disp.Index, centre, halfWidth);
		float clamp = liveLobeHeight * MarkerYSpan;
		float yOff = ((float)disp.VariabilityQuality - 0.5f) * liveLobeHeight * MarkerYSpan;
		p.Y += Math.Clamp(yOff, -clamp, clamp);
```

with:

```csharp
		Vector2 p = MarkerScreenPos(centre, halfWidth, liveLobeHeight, disp);
```

Call the arrow in `Draw()` immediately after `DrawMarker(draw, centre, halfWidth, liveLobeHeight, disp, confidence);`:

```csharp
		DrawVelocityArrow(draw, centre, halfWidth, liveLobeHeight, disp, dynamics, confidence);
```

- [ ] **Step 4: Append the trend to the readout**

In `DrawReadout`, replace the final `ImGui.Text(...)` line with a trend-augmented readout (ASCII glyphs — the default ImGui font has no `▲▼` glyphs):

```csharp
		var dyn = _pipeline.LatestDynamics;
		string trend = dyn.Trend switch
		{
			RegulationTrend.Escalating => $"^ escalating {dyn.Velocity:+0.00;-0.00}/s",
			RegulationTrend.DeEscalating => $"v easing {dyn.Velocity:+0.00;-0.00}/s",
			_ => "- steady",
		};
		ImGui.Text($"HR {s.MeanHr:F0} bpm    RMSSD {s.Rmssd:F0} ms ({rel})    {_pipeline.CurrentState}    {trend}");
```

- [ ] **Step 5: Build the App project**

Run: `dotnet build MeltdownMonitor.App/MeltdownMonitor.App.csproj`
Expected: Build succeeded, 0 errors (under warnings-as-errors). If `MSB3021` file-lock errors appear, the desktop app is running — close it or build to a throwaway dir with `-o "$env:TEMP/mm_app_verify"`.

- [ ] **Step 6: Commit**

```bash
git add MeltdownMonitor.App/Regulation/RegulationFieldView.cs
git commit -m "feat: draw velocity arrow + trend readout on the desktop field"
```

---

### Task 10: Full verification

**Files:** none (verification only)

- [ ] **Step 1: Run the whole suite**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj`
Expected: PASS — all prior tests plus the new tracker (11), animator (4), and NowViewModel (4) tests.

- [ ] **Step 2: Build every non-iOS project under warnings-as-errors**

Run:
```
dotnet build MeltdownMonitor.Core/MeltdownMonitor.Core.csproj
dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj
dotnet build MeltdownMonitor.Ble.Windows/MeltdownMonitor.Ble.Windows.csproj
dotnet build MeltdownMonitor.App/MeltdownMonitor.App.csproj
```
Expected: all succeed, 0 errors. (iOS heads build only on macOS/CI; the feature touches only shared Mobile/Core code the iOS head consumes, so CI will validate the `net10.0-ios` build.)

- [ ] **Step 3: Live-app smoke (the only gate for real-time visual behaviour)**

Run the desktop app on a real Polar sensor and confirm: an escalation produces a warm arrow pointing toward MELTDOWN whose length grows with the rate; easing produces a cool arrow toward REST; a settled state shows no arrow; the readout reads "escalating/easing/steady" with a plausible rate; nothing renders while "Calibrating baseline…". Note any tuning that feels off — the tracker constants (`SmoothingAlpha`, `TrendDeadband`, `ReferenceSpeed`) are the dials.

- [ ] **Step 4: Push the branch and open the PR**

```bash
git push -u origin claude/regulation-field-velocity
gh pr create --base main --title "Regulation Field: escalation/de-escalation velocity indicator" --body "Implements docs/superpowers/specs/2026-05-31-regulation-field-velocity-indicator-design.md. Shared Core RegulationVelocityTracker (TDD) + LatestDynamics on both pipelines, arrow + trend-tinted trail + readout on both renderers. Tracker constants are initial estimates to tune live."
```

Watch CI (the `net10.0-ios` build is the only build not verifiable locally).

---

## Self-review

**Spec coverage:**
- Core `RegulationTrend` / `RegulationDynamics` / `RegulationVelocityTracker` (seed-don't-emit, EWMA, deadband, normalize, reset) → Tasks 1–2. ✓
- Pipeline `LatestDynamics` + warm/contact gating, mobile `DynamicsUpdated` → Tasks 3–4. ✓
- Three layers — arrow, velocity-scaled/tinted trail, readout — on both renderers → mobile Tasks 6/8, desktop Task 9. ✓
- Shared eased magnitude → animator `DisplayedSpeed` (Task 5, mobile) and `_arrowSpeed` (Task 9, desktop). ✓
- Warm/contact gating + hide-while-calibrating → pipeline gating (3/4) + `confidence < 0.999` guards in arrow draws + `IsTrendVisible` (7). ✓
- Testing: tracker + animator + NowViewModel unit-tested; renderers/App build+live-verified → Tasks 2/5/7 + 9/10. ✓
- Out of scope (OverlayMetric HUD, predictive time-to-threshold, detector feedback) → correctly omitted.

**Placeholder scan:** none — every code step shows full content; build/verify steps give exact commands and expected output.

**Type consistency:** `RegulationDynamics(Velocity, Trend, NormalizedSpeed)` + static `Steady`; `RegulationTrend { DeEscalating, Steady, Escalating }`; `RegulationVelocityTracker.Update(double, DateTimeOffset)` / `.Reset()` / `.Latest`; pipeline `LatestDynamics` (both) + `DynamicsUpdated` (mobile only — desktop view polls `LatestDynamics`); animator `Step(dt, targetIndex, heartRate, targetSpeed=0)` + `DisplayedSpeed`; control `DynamicsProperty`/`Dynamics`; VM `Dynamics`/`OnDynamicsUpdated`/`TrendLabel`/`VelocityText`/`NormalizedSpeed`/`IsTrendVisible`. Names used consistently across all tasks. ✓
