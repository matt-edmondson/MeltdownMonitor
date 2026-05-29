# Regulation Field Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Regulation Field — a live figure-8 instrument that shows the user's autonomic arousal as a position within their window of tolerance, with a comet trail showing trajectory — as the signature view of the status window.

**Architecture:** A pure, unit-tested **Core** calculator (`RegulationFieldCalculator`) turns each `HrvSample` + thresholds into a signed `RegulationReading` (arousal-vs-baseline, variability quality, confidence). Pure **Core** geometry (`LemniscateGeometry`) maps that reading to screen points. A windows-only **App** view (`RegulationFieldView`) renders it with Dear ImGui's draw list (custom 2D drawing — not ImPlot), animated from the render loop, and is added as the first tab of `StatusWindow`. The Core split exists so the same calculator/geometry feed the planned iOS port verbatim.

**Tech Stack:** C# / .NET 10, MSTest, `Hexa.NET.ImGui` (`ImDrawList` custom drawing), `System.Numerics`.

**Honest-signal constraint (from design review):** RMSSD/HR-vs-baseline can only distinguish *above-baseline arousal* from *below-baseline arousal*. It cannot detect dorsal-vagal shutdown. So the **cool lobe = rest/recovery** (calmer than baseline, normally good), the **warm lobe = sympathetic activation** (the validated meltdown signal). True shutdown detection is explicitly deferred. The index is a continuous reading; the detector state (colour) is the *confirmed* call — the index will sometimes lead the state, and that early-warning lead is intentional, not a bug.

---

## File structure

| File | Responsibility | Tested |
|---|---|---|
| `MeltdownMonitor.Core/Regulation/RegulationReading.cs` | Immutable result: `Index`, `VariabilityQuality`, `Confidence` | via calculator tests |
| `MeltdownMonitor.Core/Regulation/RegulationFieldCalculator.cs` | Pure `HrvSample` → `RegulationReading` | ✅ Core |
| `MeltdownMonitor.Core/Regulation/LemniscateGeometry.cs` | Pure geometry: marker needle point + figure-8 polyline | ✅ Core |
| `MeltdownMonitor.App/Regulation/MacchiatoPalette.cs` | Catppuccin Macchiato colours + `State(DetectorState)` | build/visual |
| `MeltdownMonitor.App/Regulation/RegulationFieldView.cs` | ImGui renderer + animation + thread-safe trail | build/run |
| `MeltdownMonitor.App/StatusWindow.cs` (modify) | Add the field as the first tab; refactor `StateColor` onto the palette; dispose wiring | build/run |
| `docs/superpowers/specs/2026-05-29-icon-set-and-branding-design.md` (modify) | Revise §7/§10 to the honest arousal-axis semantics | review |
| `MeltdownMonitor.Tests/RegulationFieldCalculatorTests.cs` | Calculator unit tests | — |
| `MeltdownMonitor.Tests/LemniscateGeometryTests.cs` | Geometry unit tests | — |
| `MeltdownMonitor.Tests/RegulationFieldRampTests.cs` | Synthetic dysregulation-ramp acceptance test | — |

**Note on App verification:** `MeltdownMonitor.Tests` references Core only (it is cross-platform; the App is windows-only). App tasks (palette, view, StatusWindow) therefore verify by **building and running the app and observing behaviour**, not by unit tests. All genuinely testable logic lives in Core for exactly this reason.

**Note on ImGui signatures:** exact `ImDrawListPtr` / `ImGui` method signatures vary slightly across `Hexa.NET.ImGui` versions. The code below is idiomatic; if a signature mismatches, check IntelliSense on the installed version and adjust the call (the parameters are the same in spirit). `StatusWindow.cs` already uses `ImGui`/`ImPlot` so the using directives and patterns are established.

---

## Task 1: Core — `RegulationReading` + `RegulationFieldCalculator`

**Files:**
- Create: `MeltdownMonitor.Core/Regulation/RegulationReading.cs`
- Create: `MeltdownMonitor.Core/Regulation/RegulationFieldCalculator.cs`
- Test: `MeltdownMonitor.Tests/RegulationFieldCalculatorTests.cs`

- [ ] **Step 1: Write the result type**

Create `MeltdownMonitor.Core/Regulation/RegulationReading.cs`:

```csharp
namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// A single live reading of autonomic regulation, derived from an <c>HrvSample</c>
/// relative to the personal baseline.
/// </summary>
/// <param name="Index">
/// Signed arousal-vs-baseline in [-1, 1].
/// Positive = sympathetic activation (toward the warm "meltdown" lobe);
/// 0 = at baseline (centre of the window of tolerance);
/// negative = calmer than baseline = rest/recovery (the cool lobe).
/// This is NOT a shutdown signal — true shutdown detection is not yet possible
/// from RMSSD/HR alone.
/// </param>
/// <param name="VariabilityQuality">
/// RMSSD relative to baseline in [0, 1]: 1 = healthy variability (a fat, lively
/// trace), 0 = collapsed/metronomic (the stress signature). Drives stroke fatness.
/// </param>
/// <param name="Confidence">
/// [0, 1]: 0 while the baseline is unusable/cold, ramping to 1 once warm.
/// The view dims the whole field by this value.
/// </param>
public readonly record struct RegulationReading(double Index, double VariabilityQuality, double Confidence);
```

- [ ] **Step 2: Write the failing tests**

Create `MeltdownMonitor.Tests/RegulationFieldCalculatorTests.cs`:

```csharp
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

[TestClass]
public class RegulationFieldCalculatorTests
{
	private static readonly DetectionThresholds Thresholds = new()
	{
		RmssdWarningDropFraction = 0.30,
		HrWarningRiseFraction = 0.15,
		RmssdAlertingDropFraction = 0.50,
	};

	private static HrvSample Sample(double rmssd, double meanHr,
		double baselineRmssd = 50, double baselineHr = 70) =>
		new(DateTimeOffset.UtcNow, rmssd, 20, meanHr, baselineRmssd, baselineHr, DetectorState.Watching);

	[TestMethod]
	public void AtBaseline_IndexIsZero()
	{
		var r = RegulationFieldCalculator.Compute(Sample(50, 70), Thresholds, warmUpProgress: 1, baselineWarm: true);
		Assert.AreEqual(0.0, r.Index, 0.001);
	}

	[TestMethod]
	public void AtWarningThreshold_IndexIsAboutPointSix()
	{
		// RMSSD 30% below, HR 15% above — both exactly at their Warning thresholds.
		var r = RegulationFieldCalculator.Compute(Sample(35, 80.5), Thresholds, 1, true);
		Assert.AreEqual(0.6, r.Index, 0.02);
	}

	[TestMethod]
	public void SevereActivation_IndexSaturatesToOne()
	{
		// RMSSD 60% below, HR 40% above — well past Warning.
		var r = RegulationFieldCalculator.Compute(Sample(20, 98), Thresholds, 1, true);
		Assert.AreEqual(1.0, r.Index, 0.001);
	}

	[TestMethod]
	public void CalmerThanBaseline_IndexIsNegative()
	{
		// RMSSD above baseline, HR below baseline = rest/recovery.
		var r = RegulationFieldCalculator.Compute(Sample(70, 60), Thresholds, 1, true);
		Assert.IsTrue(r.Index < 0, $"expected negative index, got {r.Index}");
	}

	[TestMethod]
	public void VariabilityQuality_IsRmssdOverBaselineClampedToOne()
	{
		Assert.AreEqual(0.5, RegulationFieldCalculator.Compute(Sample(25, 70), Thresholds, 1, true).VariabilityQuality, 0.001);
		Assert.AreEqual(1.0, RegulationFieldCalculator.Compute(Sample(80, 70), Thresholds, 1, true).VariabilityQuality, 0.001);
		Assert.AreEqual(0.0, RegulationFieldCalculator.Compute(Sample(0, 70), Thresholds, 1, true).VariabilityQuality, 0.001);
	}

	[TestMethod]
	public void NotWarm_ConfidenceFollowsWarmUpProgress()
	{
		var r = RegulationFieldCalculator.Compute(Sample(50, 70), Thresholds, warmUpProgress: 0.4, baselineWarm: false);
		Assert.AreEqual(0.4, r.Confidence, 0.001);
	}

	[TestMethod]
	public void InvalidBaseline_ReturnsNeutralWithNoConfidence()
	{
		var r = RegulationFieldCalculator.Compute(Sample(50, 70, baselineRmssd: 0, baselineHr: 0), Thresholds, 1, true);
		Assert.AreEqual(0.0, r.Index, 0.001);
		Assert.AreEqual(0.0, r.Confidence, 0.001);
	}
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~RegulationFieldCalculatorTests"`
Expected: FAIL — `RegulationFieldCalculator` does not exist (compile error).

- [ ] **Step 4: Write the calculator**

Create `MeltdownMonitor.Core/Regulation/RegulationFieldCalculator.cs`:

```csharp
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Turns an <see cref="HrvSample"/> into a <see cref="RegulationReading"/> relative
/// to the personal baseline. Pure and deterministic so it can be unit-tested and
/// shared with the iOS port.
/// </summary>
public static class RegulationFieldCalculator
{
	// RMSSD is the gold-standard parasympathetic marker, so it carries more weight
	// than HR in the combined arousal index.
	private const double RmssdWeight = 0.6;
	private const double HrWeight = 0.4;

	// A combined deviation equal to 1.0 (both metrics at their Warning thresholds)
	// maps to this index magnitude, leaving head-room toward the saturating ±1.
	private const double WarningIndex = 0.6;

	public static RegulationReading Compute(
		HrvSample sample,
		DetectionThresholds thresholds,
		double warmUpProgress,
		bool baselineWarm)
	{
		double confidence = baselineWarm ? 1.0 : Math.Clamp(warmUpProgress, 0.0, 1.0);

		if (sample.BaselineRmssd <= 0 || sample.BaselineHr <= 0)
		{
			// Baseline not usable yet — neutral position, no confidence.
			return new RegulationReading(0.0, 1.0, 0.0);
		}

		double rmssdDrop = (sample.BaselineRmssd - sample.Rmssd) / sample.BaselineRmssd; // + when stressed
		double hrRise = (sample.MeanHr - sample.BaselineHr) / sample.BaselineHr;         // + when stressed

		// Express each deviation in units of its Warning threshold so the two
		// differently-scaled fractions can be combined on a common axis.
		double warnR = Math.Max(thresholds.RmssdWarningDropFraction, 1e-6);
		double warnH = Math.Max(thresholds.HrWarningRiseFraction, 1e-6);

		double activation = (RmssdWeight * Math.Max(0.0, rmssdDrop) / warnR)
						  + (HrWeight * Math.Max(0.0, hrRise) / warnH);
		double rest = (RmssdWeight * Math.Max(0.0, -rmssdDrop) / warnR)
					+ (HrWeight * Math.Max(0.0, -hrRise) / warnH);

		double index = Math.Clamp((activation - rest) * WarningIndex, -1.0, 1.0);

		double quality = Math.Clamp(sample.Rmssd / sample.BaselineRmssd, 0.0, 1.0);

		return new RegulationReading(index, quality, confidence);
	}
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~RegulationFieldCalculatorTests"`
Expected: PASS (7/7).

- [ ] **Step 6: Commit**

```bash
git add MeltdownMonitor.Core/Regulation/RegulationReading.cs MeltdownMonitor.Core/Regulation/RegulationFieldCalculator.cs MeltdownMonitor.Tests/RegulationFieldCalculatorTests.cs
git commit -m "feat: add RegulationField arousal-vs-baseline calculator"
```

---

## Task 2: Core — `LemniscateGeometry`

The marker is a **needle moving along the major axis** (centre → lobe tip), not a point orbiting the figure-8. The polyline is only the drawn *track*. Geometry is pure so it lives in Core and is tested.

**Files:**
- Create: `MeltdownMonitor.Core/Regulation/LemniscateGeometry.cs`
- Test: `MeltdownMonitor.Tests/LemniscateGeometryTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `MeltdownMonitor.Tests/LemniscateGeometryTests.cs`:

```csharp
using System.Numerics;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

[TestClass]
public class LemniscateGeometryTests
{
	[TestMethod]
	public void MarkerAtZero_IsCentre()
	{
		var p = LemniscateGeometry.MarkerPoint(0f, new Vector2(100, 50), halfWidth: 40f);
		Assert.AreEqual(100f, p.X, 0.001f);
		Assert.AreEqual(50f, p.Y, 0.001f);
	}

	[TestMethod]
	public void MarkerAtPlusOne_IsRightTip()
	{
		var p = LemniscateGeometry.MarkerPoint(1f, new Vector2(100, 50), halfWidth: 40f);
		Assert.AreEqual(140f, p.X, 0.001f);
	}

	[TestMethod]
	public void MarkerAtMinusOne_IsLeftTip()
	{
		var p = LemniscateGeometry.MarkerPoint(-1f, new Vector2(100, 50), halfWidth: 40f);
		Assert.AreEqual(60f, p.X, 0.001f);
	}

	[TestMethod]
	public void Polyline_IsClosedAndSymmetric()
	{
		var pts = LemniscateGeometry.Polyline(new Vector2(0, 0), halfWidth: 40f, lobeHeight: 20f, segments: 64);
		Assert.AreEqual(64, pts.Count);
		// Closed figure-8: first and last points are within ~one segment of each other
		// (a broken/open polyline would leave them a whole lobe-width apart).
		Assert.IsTrue(Vector2.Distance(pts[0], pts[^1]) < 12f,
			$"polyline should be ~closed, gap was {Vector2.Distance(pts[0], pts[^1])}");
		// Symmetric about the vertical axis: max |x| on each side is equal.
		float maxRight = pts.Max(p => p.X);
		float maxLeft = -pts.Min(p => p.X);
		Assert.AreEqual(maxRight, maxLeft, 0.5f);
	}
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~LemniscateGeometryTests"`
Expected: FAIL — `LemniscateGeometry` does not exist.

- [ ] **Step 3: Write the geometry**

Create `MeltdownMonitor.Core/Regulation/LemniscateGeometry.cs`:

```csharp
using System.Numerics;

namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Pure geometry for the Regulation Field's figure-8 (lemniscate of Bernoulli).
/// Screen space: +X right, +Y down. The marker is a needle on the major axis;
/// the polyline is the drawn track.
/// </summary>
public static class LemniscateGeometry
{
	/// <summary>
	/// Marker position for a regulation index in [-1, 1]: the needle slides along
	/// the major axis from the cool (left) tip through the centre to the warm
	/// (right) tip. Depth, not orbit.
	/// </summary>
	public static Vector2 MarkerPoint(float index, Vector2 centre, float halfWidth)
		=> new(centre.X + (Math.Clamp(index, -1f, 1f) * halfWidth), centre.Y);

	/// <summary>
	/// Samples the lemniscate outline as a closed polyline of <paramref name="segments"/>
	/// points, centred at <paramref name="centre"/>. <paramref name="halfWidth"/> is the
	/// distance from centre to a lobe tip; <paramref name="lobeHeight"/> the half-height.
	/// </summary>
	public static IReadOnlyList<Vector2> Polyline(Vector2 centre, float halfWidth, float lobeHeight, int segments)
	{
		var points = new List<Vector2>(segments);
		for (int i = 0; i < segments; i++)
		{
			double t = (i / (double)segments) * 2.0 * Math.PI;
			double denom = 1.0 + (Math.Sin(t) * Math.Sin(t));
			double x = Math.Cos(t) / denom;
			double y = Math.Sin(t) * Math.Cos(t) / denom;
			// y is scaled so the lobe half-height is lobeHeight; the parametric y peaks
			// at ~0.354 (1/(2√2)), so divide by that to normalise to ±1 before scaling.
			points.Add(new Vector2(
				centre.X + ((float)x * halfWidth),
				centre.Y + ((float)(y / 0.35355339) * lobeHeight)));
		}

		return points;
	}
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~LemniscateGeometryTests"`
Expected: PASS (4/4).

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Core/Regulation/LemniscateGeometry.cs MeltdownMonitor.Tests/LemniscateGeometryTests.cs
git commit -m "feat: add lemniscate geometry for the Regulation Field"
```

---

## Task 3: Core — synthetic dysregulation-ramp acceptance test

This is the real acceptance check the design review demanded: drive a *known* ramp from calm → dysregulated → recovering and assert the index tracks it (the marker follows the data, with a correct trail direction). No rendering — index *is* the marker position.

**Files:**
- Test: `MeltdownMonitor.Tests/RegulationFieldRampTests.cs`

- [ ] **Step 1: Write the test**

Create `MeltdownMonitor.Tests/RegulationFieldRampTests.cs`:

```csharp
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

[TestClass]
public class RegulationFieldRampTests
{
	private static readonly DetectionThresholds Thresholds = new();

	private static HrvSample At(double rmssd, double hr) =>
		new(DateTimeOffset.UtcNow, rmssd, 20, hr, 50, 70, DetectorState.Watching);

	[TestMethod]
	public void Index_RisesThroughActivation_ThenFallsOnRecovery()
	{
		// Ramp: baseline → progressively lower RMSSD + higher HR → back to baseline.
		HrvSample[] rampUp =
		[
			At(50, 70),   // at baseline
			At(45, 73),
			At(40, 77),
			At(35, 80.5), // ~Warning threshold
			At(28, 86),
			At(20, 95),   // severe
		];

		double[] indices = rampUp
			.Select(s => RegulationFieldCalculator.Compute(s, Thresholds, 1, true).Index)
			.ToArray();

		// Monotonically increasing — the marker steadily enters the warm lobe.
		for (int i = 1; i < indices.Length; i++)
		{
			Assert.IsTrue(indices[i] > indices[i - 1],
				$"index should rise at step {i}: {indices[i - 1]} -> {indices[i]}");
		}

		// Crosses into clear activation by the Warning-threshold sample.
		Assert.IsTrue(indices[3] >= 0.55, $"expected ~0.6 at Warning, got {indices[3]}");
		Assert.AreEqual(1.0, indices[^1], 0.001);

		// Recovery: returning toward baseline pulls the index back down (trail reverses).
		double recovering = RegulationFieldCalculator.Compute(At(48, 71), Thresholds, 1, true).Index;
		Assert.IsTrue(recovering < indices[^1], "recovery should move the marker back toward centre");
	}
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test --filter "FullyQualifiedName~RegulationFieldRampTests"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add MeltdownMonitor.Tests/RegulationFieldRampTests.cs
git commit -m "test: Regulation Field index tracks a synthetic dysregulation ramp"
```

---

## Task 4: App — `MacchiatoPalette` and refactor `StateColor`

Centralise the Catppuccin Macchiato colours so the field, header indicator, and any future surface share one source of truth, aligned with the branding spec.

**Files:**
- Create: `MeltdownMonitor.App/Regulation/MacchiatoPalette.cs`
- Modify: `MeltdownMonitor.App/StatusWindow.cs` (the `StateColor` method, lines ~814-826)

- [ ] **Step 1: Write the palette**

Create `MeltdownMonitor.App/Regulation/MacchiatoPalette.cs`:

```csharp
using System.Numerics;
using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.App.Regulation;

/// <summary>Catppuccin Macchiato palette (linear RGBA 0..1) used across the UI.</summary>
internal static class MacchiatoPalette
{
	public static readonly Vector4 Base = Hex(0x24, 0x27, 0x3a);
	public static readonly Vector4 Mantle = Hex(0x1e, 0x20, 0x30);
	public static readonly Vector4 Text = Hex(0xca, 0xd3, 0xf5);
	public static readonly Vector4 Subtext0 = Hex(0xa5, 0xad, 0xcb);
	public static readonly Vector4 Overlay1 = Hex(0x80, 0x87, 0xa2);
	public static readonly Vector4 Lavender = Hex(0xb7, 0xbd, 0xf8);
	public static readonly Vector4 Sky = Hex(0x91, 0xd7, 0xe3);
	public static readonly Vector4 Sapphire = Hex(0x7d, 0xc4, 0xe4);
	public static readonly Vector4 Green = Hex(0xa6, 0xda, 0x95);
	public static readonly Vector4 Peach = Hex(0xf5, 0xa9, 0x7f);
	public static readonly Vector4 Maroon = Hex(0xee, 0x99, 0xa0);
	public static readonly Vector4 Red = Hex(0xed, 0x87, 0x96);

	/// <summary>Detector-state accent, per the branding spec's state colour mapping.</summary>
	public static Vector4 State(DetectorState state) => state switch
	{
		DetectorState.Idle => Overlay1,
		DetectorState.Watching => Green,
		DetectorState.Warning => Peach,
		DetectorState.Alerting => Red,
		DetectorState.Cooldown => Sapphire,
		_ => Overlay1,
	};

	public static Vector4 Lerp(Vector4 a, Vector4 b, float t) => Vector4.Lerp(a, b, Math.Clamp(t, 0f, 1f));

	public static Vector4 WithAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);

	private static Vector4 Hex(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);
}
```

- [ ] **Step 2: Refactor `StatusWindow.StateColor` to use the palette**

In `MeltdownMonitor.App/StatusWindow.cs`, replace the `StateColor` method body (the `switch` returning hard-coded `Vector4`s) with:

```csharp
	private static ImColor StateColor(DetectorState state) =>
		new() { Value = MeltdownMonitor.App.Regulation.MacchiatoPalette.State(state) };
```

(Add `using MeltdownMonitor.App.Regulation;` to the usings if you prefer the short name.)

- [ ] **Step 3: Build to verify**

Run: `dotnet build MeltdownMonitor.App/MeltdownMonitor.App.csproj`
Expected: Build succeeded, 0 errors. (If the app is running, build to an isolated dir: `dotnet build MeltdownMonitor.App/MeltdownMonitor.App.csproj -o "$env:TEMP\mm_build"`.)

- [ ] **Step 4: Commit**

```bash
git add MeltdownMonitor.App/Regulation/MacchiatoPalette.cs MeltdownMonitor.App/StatusWindow.cs
git commit -m "feat: centralise Catppuccin Macchiato palette; align state colours"
```

---

## Task 5: App — `RegulationFieldView` renderer

The renderer. It subscribes to the pipeline (background thread) to keep an immutable latest reading and a trail ring buffer (thread-safe), and draws with the ImGui draw list each frame, animating from `ImGui.GetIO().DeltaTime`.

**Files:**
- Create: `MeltdownMonitor.App/Regulation/RegulationFieldView.cs`

- [ ] **Step 1: Write the skeleton (data + layout + dispose)**

Create `MeltdownMonitor.App/Regulation/RegulationFieldView.cs`:

```csharp
using System.Numerics;
using Hexa.NET.ImGui;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.App.Regulation;

/// <summary>
/// Signature visualization: the Regulation Field. Draws the lemniscate instrument
/// with a live marker (arousal-vs-baseline), comet trail, ghost baseline, and a
/// monospace readout. Custom ImGui draw-list rendering — not ImPlot.
/// </summary>
public sealed class RegulationFieldView : IDisposable
{
	private const int TrailLength = 48;     // ~last few minutes at the emit cadence
	private const int LobeSegments = 96;

	private readonly Pipeline _pipeline;
	private readonly object _lock = new();
	private readonly RegulationReading[] _trail = new RegulationReading[TrailLength];
	private int _trailCount;

	// Animation state (UI thread only).
	private float _markerPos;       // eased toward the latest index
	private float _breathPhase;
	private float _animTime;

	public RegulationFieldView(Pipeline pipeline)
	{
		_pipeline = pipeline;
		_pipeline.SampleUpdated += OnSampleUpdated;
	}

	private void OnSampleUpdated(HrvSample sample)
	{
		var reading = RegulationFieldCalculator.Compute(
			sample,
			_pipeline.LatestThresholds,
			_pipeline.Baseline.WarmUpProgress,
			_pipeline.Baseline.IsWarm);

		lock (_lock)
		{
			// Append to the trail ring (newest last).
			if (_trailCount < TrailLength)
			{
				_trail[_trailCount++] = reading;
			}
			else
			{
				Array.Copy(_trail, 1, _trail, 0, TrailLength - 1);
				_trail[^1] = reading;
			}
		}
	}

	private (RegulationReading latest, RegulationReading[] trail) Snapshot()
	{
		lock (_lock)
		{
			if (_trailCount == 0)
			{
				return (new RegulationReading(0, 1, 0), []);
			}

			var copy = new RegulationReading[_trailCount];
			Array.Copy(_trail, copy, _trailCount);
			return (copy[^1], copy);
		}
	}

	public void Draw()
	{
		// Filled in across the next steps.
	}

	public void Dispose() => _pipeline.SampleUpdated -= OnSampleUpdated;
}
```

This references `_pipeline.LatestThresholds`, added next.

- [ ] **Step 2: Expose thresholds on the pipeline**

In `MeltdownMonitor.App/Pipeline.cs`, add a read-only accessor (the detector reads `_settings.Thresholds` via a provider; the view needs the same value):

```csharp
	public DetectionThresholds LatestThresholds => _settings.Thresholds;
```

Place it next to the other public members (near `CurrentState`).

- [ ] **Step 3: Build to verify the skeleton compiles**

Run: `dotnet build MeltdownMonitor.App/MeltdownMonitor.App.csproj -o "$env:TEMP\mm_build"`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Implement `Draw` — layout, background, geometry**

Replace the empty `Draw()` body:

```csharp
	public void Draw()
	{
		var (latest, trail) = Snapshot();
		float dt = ImGui.GetIO().DeltaTime;
		_animTime += dt;

		// Ease the marker toward the latest index so it glides between 5 s samples.
		float target = (float)latest.Index;
		_markerPos += (target - _markerPos) * (1f - MathF.Exp(-dt * 6f));

		// Breathing cadence from current HR (fallback 60 bpm).
		double hr = _pipeline.LatestSample?.MeanHr ?? 60.0;
		_breathPhase += dt * (float)(Math.Max(40.0, hr) / 60.0) * MathF.Tau;

		// Reserve a wide panel and centre the instrument in it.
		Vector2 avail = ImGui.GetContentRegionAvail();
		float height = MathF.Min(avail.Y - 8f, 360f);
		float width = avail.X;
		Vector2 origin = ImGui.GetCursorScreenPos();
		ImGui.Dummy(new Vector2(width, height)); // reserve layout space

		var draw = ImGui.GetWindowDrawList();
		Vector2 centre = origin + new Vector2(width * 0.5f, height * 0.46f);
		float halfWidth = MathF.Min(width * 0.34f, 260f);
		float lobeHeight = MathF.Min(height * 0.28f, halfWidth * 0.62f);

		float confidence = (float)latest.Confidence;

		DrawWindowOfTolerance(draw, centre, halfWidth, lobeHeight, confidence);
		DrawLemniscate(draw, centre, halfWidth, lobeHeight, latest, confidence);
		DrawTrail(draw, centre, halfWidth, trail, latest, confidence);
		DrawMarker(draw, centre, halfWidth, latest, confidence);
		DrawCrossover(draw, centre, confidence);
		DrawLabelsAndLock(draw, origin, centre, halfWidth, lobeHeight, latest);
		DrawReadout(origin, height);

		if (confidence < 0.999f)
		{
			DrawCalibratingOverlay(draw, centre, confidence);
		}
	}
```

- [ ] **Step 5: Implement the drawing helpers**

Append these private methods to the class. They use `ImGui.ColorConvertFloat4ToU32` (an alias for `ImGui.GetColorU32(Vector4)` on some versions — use whichever the installed binding exposes).

```csharp
	private static uint Col(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

	private void DrawWindowOfTolerance(ImDrawListPtr draw, Vector2 centre, float halfWidth, float lobeHeight, float confidence)
	{
		// Soft translucent Lavender zone marking the regulated centre.
		var zone = MacchiatoPalette.WithAlpha(MacchiatoPalette.Lavender, 0.08f * confidence);
		draw.AddEllipseFilled(centre, new Vector2(halfWidth * 0.32f, lobeHeight * 0.7f), Col(zone));
	}

	private void DrawLemniscate(ImDrawListPtr draw, Vector2 centre, float halfWidth, float lobeHeight, RegulationReading r, float confidence)
	{
		var ghost = LemniscateGeometry.Polyline(centre, halfWidth, lobeHeight, LobeSegments);

		// Ghost baseline (symmetric resting frame).
		uint ghostCol = Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Overlay1, 0.28f * confidence));
		for (int i = 0; i < ghost.Count; i++)
		{
			draw.AddLine(ghost[i], ghost[(i + 1) % ghost.Count], ghostCol, 2f);
		}

		// Live two-tone trace: warm (x>centre) swells with positive index, cool with negative.
		float warmSwell = 1f + (MathF.Max(0f, (float)r.Index) * 1.4f);
		float coolSwell = 1f + (MathF.Max(0f, -(float)r.Index) * 1.4f);
		float quality = (float)r.VariabilityQuality;              // thin when collapsed
		float baseThick = 4f + (6f * quality);

		for (int i = 0; i < ghost.Count; i++)
		{
			Vector2 a = ghost[i];
			Vector2 b = ghost[(i + 1) % ghost.Count];
			float midX = (a.X + b.X) * 0.5f;
			bool warm = midX >= centre.X;
			float depth = MathF.Min(1f, MathF.Abs(midX - centre.X) / halfWidth);

			Vector4 c = warm
				? MacchiatoPalette.Lerp(MacchiatoPalette.Peach, MacchiatoPalette.Maroon, depth)
				: MacchiatoPalette.Lerp(MacchiatoPalette.Sky, MacchiatoPalette.Sapphire, depth);
			c = MacchiatoPalette.WithAlpha(c, confidence);

			// Animated variability jitter on the outer half of each lobe.
			float jitter = quality * 1.5f * MathF.Sin((_animTime * 6f) + (i * 0.7f)) * depth;
			Vector2 n = Normal(a, b) * jitter;

			float thick = baseThick * (warm ? warmSwell : coolSwell);
			draw.AddLine(a + n, b + n, Col(c), thick);
		}
	}

	private void DrawTrail(ImDrawListPtr draw, Vector2 centre, float halfWidth, RegulationReading[] trail, RegulationReading latest, float confidence)
	{
		if (trail.Length < 2)
		{
			return;
		}

		Vector4 stateCol = MacchiatoPalette.State(_pipeline.CurrentState);
		// Oldest faint → newest bright, ending just behind the marker.
		for (int i = 0; i < trail.Length - 1; i++)
		{
			float frac = i / (float)(trail.Length - 1);
			Vector2 p = LemniscateGeometry.MarkerPoint((float)trail[i].Index, centre, halfWidth);
			float radius = 1.5f + (3f * frac);
			draw.AddCircleFilled(p, radius, Col(MacchiatoPalette.WithAlpha(stateCol, 0.5f * frac * confidence)));
		}
	}

	private void DrawMarker(ImDrawListPtr draw, Vector2 centre, float halfWidth, RegulationReading r, float confidence)
	{
		Vector2 p = LemniscateGeometry.MarkerPoint(_markerPos, centre, halfWidth);
		Vector4 stateCol = MacchiatoPalette.State(_pipeline.CurrentState);

		float pulse = 1f + (0.18f * MathF.Sin(_breathPhase));
		// Halo (breathing).
		draw.AddCircleFilled(p, 16f * pulse, Col(MacchiatoPalette.WithAlpha(stateCol, 0.18f * confidence)));
		// Core.
		draw.AddCircleFilled(p, 6.5f, Col(MacchiatoPalette.WithAlpha(stateCol, confidence)));
		draw.AddCircleFilled(p, 2.6f, Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Base, confidence)));
	}

	private void DrawCrossover(ImDrawListPtr draw, Vector2 centre, float confidence)
	{
		draw.AddCircleFilled(centre, 7f, Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Lavender, confidence)));
		draw.AddCircleFilled(centre, 3f, Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Text, confidence)));
	}

	private void DrawLabelsAndLock(ImDrawListPtr draw, Vector2 origin, Vector2 centre, float halfWidth, float lobeHeight, RegulationReading r)
	{
		uint rest = Col(MacchiatoPalette.Sapphire);
		uint melt = Col(MacchiatoPalette.Peach);
		uint dim = Col(MacchiatoPalette.Subtext0);
		draw.AddText(new Vector2(centre.X - halfWidth - 6f, centre.Y - 6f), rest, "REST");
		draw.AddText(new Vector2(centre.X + halfWidth - 30f, centre.Y - 6f), melt, "MELTDOWN");
		draw.AddText(new Vector2(centre.X - 56f, centre.Y - lobeHeight - 20f), dim, "WINDOW OF TOLERANCE");

		// Baseline freezes during Warning/Alerting — show a lock.
		var state = _pipeline.CurrentState;
		if (state is DetectorState.Warning or DetectorState.Alerting)
		{
			draw.AddText(new Vector2(origin.X + 8f, origin.Y + 8f), Col(MacchiatoPalette.Overlay1), "BASELINE LOCKED");
		}
	}

	private void DrawReadout(Vector2 origin, float height)
	{
		// Reuse ImGui text below the instrument for the monospace numerics.
		ImGui.SetCursorScreenPos(new Vector2(origin.X + 8f, origin.Y + height - 22f));
		var s = _pipeline.LatestSample;
		if (s is null)
		{
			ImGui.TextDisabled("Waiting for beats…");
			return;
		}

		double drop = s.BaselineRmssd > 0 ? (s.BaselineRmssd - s.Rmssd) / s.BaselineRmssd : 0;
		ImGui.Text($"HR {s.MeanHr:F0} bpm    RMSSD {s.Rmssd:F0} ms ({drop * 100:+0;-0;0}% vs base)    {_pipeline.CurrentState}");
	}

	private void DrawCalibratingOverlay(ImDrawListPtr draw, Vector2 centre, float confidence)
	{
		string msg = $"Calibrating baseline… {confidence * 100:F0}%";
		draw.AddText(new Vector2(centre.X - 70f, centre.Y + 30f), Col(MacchiatoPalette.Subtext0), msg);
	}

	private static Vector2 Normal(Vector2 a, Vector2 b)
	{
		Vector2 d = b - a;
		float len = d.Length();
		return len < 1e-4f ? Vector2.Zero : new Vector2(-d.Y / len, d.X / len);
	}
```

> If `AddEllipseFilled` is absent on the installed binding, substitute `AddCircleFilled(centre, halfWidth * 0.32f, ...)` (a circular zone reads fine).

- [ ] **Step 6: Build to verify**

Run: `dotnet build MeltdownMonitor.App/MeltdownMonitor.App.csproj -o "$env:TEMP\mm_build"`
Expected: Build succeeded, 0 errors. Fix any `ImDrawListPtr` signature mismatches flagged by the compiler.

- [ ] **Step 7: Commit**

```bash
git add MeltdownMonitor.App/Regulation/RegulationFieldView.cs MeltdownMonitor.App/Pipeline.cs
git commit -m "feat: add Regulation Field ImGui renderer"
```

---

## Task 6: App — wire the field in as the first tab

**Files:**
- Modify: `MeltdownMonitor.App/StatusWindow.cs`

- [ ] **Step 1: Add the field as a member and first tab**

In `StatusWindow.cs`:

1. Add a field near the other readonly members:

```csharp
	private readonly Regulation.RegulationFieldView _regulationField;
```

2. In the constructor, **before** `_tabs.AddTab("Overview", ...)`, construct the view and add its tab first so it is the default:

```csharp
		_regulationField = new Regulation.RegulationFieldView(_pipeline);

		_tabs = new ImGuiWidgets.TabPanel("status-tabs");
		_tabs.AddTab("Regulation Field", _regulationField.Draw);
		_tabs.AddTab("Overview", DrawOverviewTab);
		// ... existing AddTab calls unchanged ...
```

(Move the `_tabs = new ...` line if needed so construction order is: view, then panel, then tabs.)

- [ ] **Step 2: Dispose the view**

In `StatusWindow.Dispose()` (or `ReleaseSubscriptions`), dispose the field so it unsubscribes from the pipeline:

```csharp
	public void Dispose()
	{
		Close();
		ReleaseSubscriptions();
		_regulationField.Dispose();
	}
```

- [ ] **Step 3: Build**

Run: `dotnet build MeltdownMonitor.App/MeltdownMonitor.App.csproj -o "$env:TEMP\mm_build"`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Run and verify behaviour**

Close any running instance first, then:

Run: `dotnet run --project MeltdownMonitor.App`
Then open the status window from the tray (double-click). Verify:
- A **"Regulation Field"** tab is present and selected first.
- The figure-8 draws with a cool (left) and warm (right) lobe, Lavender centre node, and "REST" / "MELTDOWN" / "WINDOW OF TOLERANCE" labels.
- Before the baseline is warm, a "Calibrating baseline… N%" message shows and the field is dimmed.
- With live (or synthetic) beats, the marker sits near centre when calm and the trail follows it. Driving stress (lower RMSSD / higher HR) moves the marker toward the warm lobe; the warm lobe thickens; the marker colour matches the detector state.

> No automated test covers the renderer (App is windows-only and untested). This manual run **is** the verification step — actually launch it and observe before committing.

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.App/StatusWindow.cs
git commit -m "feat: add Regulation Field as the status window's first tab"
```

---

## Task 7: Update the branding spec to the honest arousal-axis semantics

The spec's §7 currently frames both lobes as dysregulation poles (cool = shutdown). That isn't honest with the available signals. Revise it.

**Files:**
- Modify: `docs/superpowers/specs/2026-05-29-icon-set-and-branding-design.md` (§7 "Conceptual frame" and §10 open questions)

- [ ] **Step 1: Revise §7's conceptual frame**

Replace the "Conceptual frame: window of tolerance" bullet list so the cool lobe reads as rest/recovery, not shutdown:

```markdown
### Conceptual frame: window of tolerance

The crossover centre is regulation; the lobes are *directions of departure from your
baseline*:

- **Warm lobe** — sympathetic activation → toward **meltdown** (high-arousal). This is
  the validated signal: RMSSD dropping and HR rising relative to baseline.
- **Cool lobe** — calmer than baseline → **rest / recovery** (normally healthy).
- **Crossover centre** — the **window of tolerance**, at baseline.

**Honest-signal note:** RMSSD/HR-vs-baseline distinguishes only *above-* vs
*below-baseline arousal*. It cannot detect dorsal-vagal **shutdown** (which often
presents with low HRV *and* low arousal, so it does not sit cleanly at the cool
extreme). True shutdown detection is **deferred** — it needs signals this app does
not yet have (e.g. movement/posture, EDA). v1's cool lobe therefore means rest, not
shutdown. The marker position is a continuous reading; the detector **state** (colour)
is the confirmed call, and position will sometimes *lead* state — that early-warning
lead is the point.
```

- [ ] **Step 2: Resolve the §10 open questions that this work answers**

Update §10 to record the decisions: the Regulation Field is the **first tab** of the status window (does not replace the ImPlot sparklines, which remain as drill-down tabs), and the cool-lobe semantics are settled per §7. Remove those two items from "open" and note them as decided.

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/specs/2026-05-29-icon-set-and-branding-design.md
git commit -m "docs: revise Regulation Field semantics to honest arousal axis"
```

---

## Self-review notes

- **Spec coverage:** §7's state→encoding contract is covered — marker position (Task 1 index + Task 2 needle), stroke/variability (Task 1 `VariabilityQuality` + Task 5 thickness/jitter), ghost baseline (Task 5), comet trail (Task 5 `DrawTrail`), detector-state colour (Task 4 palette + Task 5), HR breathing cadence (Task 5), baseline-locked glyph (Task 5). Poincaré SD1/SD2 → lobe geometry and LF/HF halo from §7 are intentionally **out of v1 scope** (the lobe geometry is fixed; variability quality stands in for SD1) — note this when closing the plan so it is a conscious deferral, not a silent gap.
- **Annotation dots on the trail** (§7) are deferred — annotations are written but not yet surfaced on the field; flag as a fast-follow.
- **Type consistency:** `RegulationReading(Index, VariabilityQuality, Confidence)` and `RegulationFieldCalculator.Compute(sample, thresholds, warmUpProgress, baselineWarm)` and `LemniscateGeometry.MarkerPoint(index, centre, halfWidth)` / `Polyline(centre, halfWidth, lobeHeight, segments)` are used identically in every consuming task.
- **Acceptance:** Task 3 is the data-tracking acceptance test the design review required; Task 6 Step 4 is the visual acceptance.
```
