# Clinical Audit Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the safe (Tier 1) and default-preserving (Tier 2) remediations from the 2026-06-01 clinical audit, in Core, with full MSTest coverage.

**Architecture:** All changes are in `MeltdownMonitor.Core` and tested in `MeltdownMonitor.Tests` (Core+Mobile only, no BLE/DB). Tier 1 is additive and does not change steady-state behaviour. Tier 2 ships behind new `DetectionThresholds` options whose defaults reproduce today's behaviour exactly, so existing tests and runtime behaviour are untouched until the user opts in.

**Tech Stack:** C# / .NET 10, MSTest. Repo conventions: tabs, CRLF, file-scoped namespaces, usings inside namespace where the file already does so, braces always, explicit accessibility, no `this.`, nullable enabled, warnings-as-errors.

**Source spec:** `docs/superpowers/specs/2026-06-01-clinical-audit-remediation-design.md`

---

## Task 1: Detection efficacy analyzer (audit rec H) — pure, zero runtime risk

**Files:**
- Create: `MeltdownMonitor.Core/Detection/DetectionEfficacyAnalyzer.cs`
- Test: `MeltdownMonitor.Tests/DetectionEfficacyAnalyzerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.Tests;

[TestClass]
public class DetectionEfficacyAnalyzerTests
{
	private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
	private static AnnotationRecord Ann(double minutes, AnnotationLabel label) =>
		new(T0.AddMinutes(minutes), label, null);

	[TestMethod]
	public void AlertBeforeEscalation_CountsAsPrecededWithLeadTime()
	{
		var alerts = new[] { T0.AddMinutes(3) };
		var annotations = new[] { Ann(6, AnnotationLabel.Escalating) };

		var r = DetectionEfficacyAnalyzer.Analyze(alerts, annotations, TimeSpan.FromMinutes(10));

		Assert.AreEqual(1, r.EscalationAnnotations);
		Assert.AreEqual(1, r.PrecededByAlert);
		Assert.AreEqual(1.0, r.Sensitivity, 0.001);
		Assert.AreEqual(TimeSpan.FromMinutes(3), r.MedianLeadTime);
	}

	[TestMethod]
	public void AlertOutsideLeadWindow_DoesNotCount()
	{
		var alerts = new[] { T0 };
		var annotations = new[] { Ann(30, AnnotationLabel.Blown) };

		var r = DetectionEfficacyAnalyzer.Analyze(alerts, annotations, TimeSpan.FromMinutes(10));

		Assert.AreEqual(1, r.EscalationAnnotations);
		Assert.AreEqual(0, r.PrecededByAlert);
		Assert.AreEqual(0.0, r.Sensitivity, 0.001);
		Assert.IsNull(r.MedianLeadTime);
	}

	[TestMethod]
	public void AlertAfterAnnotation_DoesNotCountAsLead()
	{
		var alerts = new[] { T0.AddMinutes(8) };
		var annotations = new[] { Ann(5, AnnotationLabel.Escalating) };

		var r = DetectionEfficacyAnalyzer.Analyze(alerts, annotations, TimeSpan.FromMinutes(10));

		Assert.AreEqual(0, r.PrecededByAlert);
	}

	[TestMethod]
	public void FineAndEdged_AreNotEscalations()
	{
		var annotations = new[] { Ann(5, AnnotationLabel.Fine), Ann(6, AnnotationLabel.Edged) };

		var r = DetectionEfficacyAnalyzer.Analyze([], annotations, TimeSpan.FromMinutes(10));

		Assert.AreEqual(0, r.EscalationAnnotations);
		Assert.AreEqual(0.0, r.Sensitivity, 0.001);
	}

	[TestMethod]
	public void AlertWithNoFollowingEscalation_IsCountedAsFalseAlarmProxy()
	{
		var alerts = new[] { T0, T0.AddMinutes(40) };
		var annotations = new[] { Ann(5, AnnotationLabel.Escalating) }; // follows the first alert only

		var r = DetectionEfficacyAnalyzer.Analyze(alerts, annotations, TimeSpan.FromMinutes(10));

		Assert.AreEqual(1, r.AlertsWithNoFollowingEscalation);
	}

	[TestMethod]
	public void MedianLeadTime_IsTrueMedianAcrossMultiple()
	{
		var alerts = new[] { T0.AddMinutes(1), T0.AddMinutes(11), T0.AddMinutes(21) };
		var annotations = new[]
		{
			Ann(3, AnnotationLabel.Escalating),  // lead 2m
			Ann(15, AnnotationLabel.Blown),      // lead 4m
			Ann(27, AnnotationLabel.Escalating), // lead 6m
		};

		var r = DetectionEfficacyAnalyzer.Analyze(alerts, annotations, TimeSpan.FromMinutes(10));

		Assert.AreEqual(3, r.PrecededByAlert);
		Assert.AreEqual(TimeSpan.FromMinutes(4), r.MedianLeadTime);
	}
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~DetectionEfficacyAnalyzerTests"`
Expected: FAIL to compile — `DetectionEfficacyAnalyzer` does not exist.

- [ ] **Step 3: Implement**

```csharp
using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.Core.Detection;

/// <summary>
/// Result of measuring whether alerts actually preceded felt escalation. All inputs are
/// supplied by the caller so the analysis is pure and unit-testable; it never reads the clock
/// or the database.
/// </summary>
public sealed record AlertEfficacyResult(
	int EscalationAnnotations,
	int PrecededByAlert,
	double Sensitivity,
	TimeSpan? MedianLeadTime,
	int AlertsWithNoFollowingEscalation);

/// <summary>
/// Measures detection efficacy from the data the app already persists: alert timestamps and
/// self check-in annotations. Answers the README's unvalidated claim that alerts fire "seconds
/// to minutes before the person consciously registers it" — and surfaces a false-alarm proxy.
/// </summary>
public static class DetectionEfficacyAnalyzer
{
	/// <summary>Self-report labels treated as "the user felt escalated".</summary>
	private static bool IsEscalation(AnnotationLabel label) =>
		label is AnnotationLabel.Escalating or AnnotationLabel.Blown;

	/// <param name="alertTimes">Alert timestamps (any order).</param>
	/// <param name="annotations">Self check-ins (any order).</param>
	/// <param name="leadWindow">How long before an escalation an alert may fire and still "count".</param>
	public static AlertEfficacyResult Analyze(
		IReadOnlyList<DateTimeOffset> alertTimes,
		IReadOnlyList<AnnotationRecord> annotations,
		TimeSpan leadWindow)
	{
		List<DateTimeOffset> alerts = [.. alertTimes.OrderBy(t => t)];
		List<AnnotationRecord> escalations = [.. annotations.Where(a => IsEscalation(a.Label))];

		var leads = new List<TimeSpan>();
		foreach (AnnotationRecord e in escalations)
		{
			// Nearest alert at or before the annotation, within the lead window.
			DateTimeOffset windowStart = e.Timestamp - leadWindow;
			TimeSpan? best = null;
			foreach (DateTimeOffset a in alerts)
			{
				if (a <= e.Timestamp && a >= windowStart)
				{
					TimeSpan lead = e.Timestamp - a;
					if (best is null || lead < best)
					{
						best = lead;
					}
				}
			}

			if (best is not null)
			{
				leads.Add(best.Value);
			}
		}

		int precededByAlert = leads.Count;
		double sensitivity = escalations.Count == 0 ? 0.0 : (double)precededByAlert / escalations.Count;

		int alertsWithNoFollowingEscalation = alerts.Count(a =>
			!escalations.Any(e => e.Timestamp >= a && e.Timestamp <= a + leadWindow));

		return new AlertEfficacyResult(
			escalations.Count,
			precededByAlert,
			sensitivity,
			Median(leads),
			alertsWithNoFollowingEscalation);
	}

	private static TimeSpan? Median(IReadOnlyList<TimeSpan> values)
	{
		if (values.Count == 0)
		{
			return null;
		}

		long[] ticks = [.. values.Select(v => v.Ticks).OrderBy(t => t)];
		int mid = ticks.Length / 2;
		long median = ticks.Length % 2 == 0
			? (ticks[mid - 1] + ticks[mid]) / 2
			: ticks[mid];
		return TimeSpan.FromTicks(median);
	}
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~DetectionEfficacyAnalyzerTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Core/Detection/DetectionEfficacyAnalyzer.cs MeltdownMonitor.Tests/DetectionEfficacyAnalyzerTests.cs
git commit -m "feat: add detection efficacy analyzer (alert lead-time vs annotations)"
```

---

## Task 2: Minimum-beat floor + gap reset in ShortWindowHrvCalculator (audit E, F)

**Files:**
- Modify: `MeltdownMonitor.Core/Hrv/ShortWindowHrvCalculator.cs`
- Test: `MeltdownMonitor.Tests/ShortWindowHrvCalculatorTests.cs` (new)

- [ ] **Step 1: Write the failing tests**

```csharp
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class ShortWindowHrvCalculatorTests
{
	private static Beat Beat(DateTimeOffset ts, double rr) => new(ts, rr, (int)Math.Round(60_000.0 / rr), IsArtifact: false);

	[TestMethod]
	public void DoesNotEmit_BeforeMinimumBeatCount()
	{
		var calc = new ShortWindowHrvCalculator { EmitIntervalSeconds = 0, MinBeatsForMetrics = 5 };
		var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

		// Four beats — one below the floor.
		HrvSample? last = null;
		for (int i = 0; i < 4; i++)
		{
			last = calc.AddBeat(Beat(start.AddMilliseconds(i * 800), 800), 50, 75, DetectorState.Watching);
		}

		Assert.IsNull(last, "Must not emit until the short window holds MinBeatsForMetrics beats.");
	}

	[TestMethod]
	public void Emits_OnceMinimumBeatCountReached()
	{
		var calc = new ShortWindowHrvCalculator { EmitIntervalSeconds = 0, MinBeatsForMetrics = 5 };
		var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

		HrvSample? last = null;
		for (int i = 0; i < 5; i++)
		{
			last = calc.AddBeat(Beat(start.AddMilliseconds(i * 800), 800), 50, 75, DetectorState.Watching);
		}

		Assert.IsNotNull(last, "Should emit once the floor is reached.");
	}

	[TestMethod]
	public void GapLongerThanThreshold_ResetsWindow_NoBridgingDifference()
	{
		// 800ms beats, then a long gap, then 600ms beats. Without a reset the first
		// post-gap difference (800→600) would inflate RMSSD. After the reset the window
		// rebuilds from the new level, so once it re-emits, RMSSD reflects 600ms beats only.
		var calc = new ShortWindowHrvCalculator { EmitIntervalSeconds = 0, MinBeatsForMetrics = 5, MaxBeatGapSeconds = 5 };
		var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

		for (int i = 0; i < 6; i++)
		{
			calc.AddBeat(Beat(start.AddMilliseconds(i * 800), 800), 50, 75, DetectorState.Watching);
		}

		// 30-second gap, then six identical 600ms beats.
		var resume = start.AddSeconds(30);
		HrvSample? last = null;
		for (int i = 0; i < 6; i++)
		{
			last = calc.AddBeat(Beat(resume.AddMilliseconds(i * 600), 600), 50, 75, DetectorState.Watching);
		}

		Assert.IsNotNull(last);
		Assert.AreEqual(0.0, last.Rmssd, 0.001, "RMSSD must reflect only post-gap (identical 600ms) beats.");
	}
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~ShortWindowHrvCalculatorTests"`
Expected: FAIL — `MinBeatsForMetrics` / `MaxBeatGapSeconds` don't exist; gap test fails (RMSSD non-zero).

- [ ] **Step 3: Implement**

Add the two tunables near the other settable properties (after `EmitIntervalSeconds`, `ShortWindowHrvCalculator.cs:27`):

```csharp
	/// <summary>
	/// Minimum beats in the short window before any sample is emitted. RMSSD from 1–2 beats
	/// is meaningless and volatile; this floor suppresses garbage from sparse/post-dropout data.
	/// 5 never affects steady state (a 60 s window holds dozens of beats); higher = more stable,
	/// less responsive. Not a clinical reliability threshold (that is ~20+).
	/// </summary>
	public int MinBeatsForMetrics { get; set; } = 5;

	/// <summary>
	/// If a beat arrives more than this many seconds after the previous clean beat, the rolling
	/// windows are cleared so no successive difference bridges a dropout. Longer than any
	/// physiological RR (MaxRrMs = 2 s), so only genuine gaps trip it.
	/// </summary>
	public double MaxBeatGapSeconds { get; set; } = 5.0;
```

Add a field next to the other private fields (after `_lastExtendedComputeTime`, ~`:32`):

```csharp
	private DateTimeOffset _lastBeatTimestamp = DateTimeOffset.MinValue;
```

In `AddBeat`, immediately after the `if (beat.IsArtifact) { return null; }` guard and before `_shortWindow.AddLast(beat);`, insert:

```csharp
		// Reset the difference chain across a temporal gap so no successive difference
		// bridges a dropout (which would inject a spurious large diff and inflate RMSSD).
		if (_lastBeatTimestamp != DateTimeOffset.MinValue &&
			(beat.Timestamp - _lastBeatTimestamp).TotalSeconds > MaxBeatGapSeconds)
		{
			_shortWindow.Clear();
			_extendedWindow.Clear();
			_latestExtended = null;
		}

		_lastBeatTimestamp = beat.Timestamp;
```

Change the floor guard (`ShortWindowHrvCalculator.cs:61`) from:

```csharp
		if (_shortWindow.Count < 2)
```

to:

```csharp
		if (_shortWindow.Count < MinBeatsForMetrics)
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~ShortWindowHrvCalculatorTests"`
Expected: PASS (3 tests). Also run `--filter "FullyQualifiedName~HrvCalculatorTests"` → still PASS.

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Core/Hrv/ShortWindowHrvCalculator.cs MeltdownMonitor.Tests/ShortWindowHrvCalculatorTests.cs
git commit -m "fix: add min-beat floor and gap reset to short-window HRV"
```

---

## Task 3: Artifact-filter staleness escape (audit G)

**Files:**
- Modify: `MeltdownMonitor.Core/Beats/RrArtifactFilter.cs`
- Test: `MeltdownMonitor.Tests/RrArtifactFilterTests.cs:87` (append before closing brace)

- [ ] **Step 1: Write the failing test** (append inside the existing class)

```csharp
	[TestMethod]
	public void SustainedRegimeShift_RecoversAfterConsecutiveRejections()
	{
		var filter = new RrArtifactFilter();
		// Establish a stable ~800ms median.
		filter.IsArtifact(800);
		filter.IsArtifact(810);
		filter.IsArtifact(790);

		// An abrupt sustained drop to 590ms (≈26% step, in absolute bounds). The first few
		// are rejected; after MaxConsecutiveRejections (4) the filter re-seeds and accepts.
		Assert.IsTrue(filter.IsArtifact(590), "1st rejected");
		Assert.IsTrue(filter.IsArtifact(590), "2nd rejected");
		Assert.IsTrue(filter.IsArtifact(590), "3rd rejected");
		Assert.IsFalse(filter.IsArtifact(590), "4th accepted — regime shift, median re-seeded");

		// New level is now the baseline; subsequent 590s are clean.
		Assert.IsFalse(filter.IsArtifact(590));
	}

	[TestMethod]
	public void LoneEctopic_StillRejected_AfterRegimeShiftLogicAdded()
	{
		var filter = new RrArtifactFilter();
		filter.IsArtifact(800);
		filter.IsArtifact(810);
		filter.IsArtifact(790);
		Assert.IsTrue(filter.IsArtifact(400), "A single ectopic is still rejected.");
		// A clean beat resets the streak, so the next ectopic is again rejected.
		Assert.IsFalse(filter.IsArtifact(805));
		Assert.IsTrue(filter.IsArtifact(400));
	}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RrArtifactFilterTests"`
Expected: FAIL — `SustainedRegimeShift...` expects the 4th 590 accepted, but today it is rejected forever.

- [ ] **Step 3: Implement**

Add a constant and field (after `MedianWindowSize`, `RrArtifactFilter.cs:10`/`:12`):

```csharp
	private const int MaxConsecutiveRejections = 4;

	private int _consecutiveRejections;
```

Replace the body of `IsArtifact` (`:18-37`) with:

```csharp
	public bool IsArtifact(double rrMs)
	{
		// Absolute bounds are a hard physiological limit and never count toward a "regime shift".
		if (rrMs < MinRrMs || rrMs > MaxRrMs)
		{
			return true;
		}

		if (_recentClean.Count >= 2)
		{
			double median = ComputeMedian(_recentClean.ToArray());
			if (Math.Abs(rrMs - median) / median > MaxDeviationFraction)
			{
				_consecutiveRejections++;

				// A *run* of in-bounds rejections is a sustained regime shift (or a resumed
				// stream after a gap), not a lone ectopic: re-seed the median from this beat
				// so the filter can't get stuck rejecting the new level forever.
				if (_consecutiveRejections >= MaxConsecutiveRejections)
				{
					_recentClean = new(MedianWindowSize);
					_recentClean.PushBack(rrMs);
					_consecutiveRejections = 0;
					return false;
				}

				return true;
			}
		}

		_consecutiveRejections = 0;
		_recentClean.PushBack(rrMs);

		return false;
	}
```

Update `Reset` (`:39`) to clear the counter:

```csharp
	public void Reset()
	{
		_recentClean = new(MedianWindowSize);
		_consecutiveRejections = 0;
	}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RrArtifactFilterTests"`
Expected: PASS (all, including the two new tests and the pre-existing ones).

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Core/Beats/RrArtifactFilter.cs MeltdownMonitor.Tests/RrArtifactFilterTests.cs
git commit -m "fix: recover artifact filter from stuck median on sustained regime shift"
```

---

## Task 4: Hypoarousal display signal (audit A(a))

**Files:**
- Modify: `MeltdownMonitor.Core/Regulation/RegulationReading.cs`
- Modify: `MeltdownMonitor.Core/Regulation/RegulationFieldCalculator.cs`
- Test: `MeltdownMonitor.Tests/RegulationFieldCalculatorTests.cs` (append)

- [ ] **Step 1: Write the failing tests** (append inside the existing class)

```csharp
	[TestMethod]
	public void Hypoarousal_HighWhenHrFarBelowBaselineAndVariabilityCollapsed()
	{
		// HR ~30% below baseline, RMSSD collapsed to 20% of baseline = low-arousal collapse.
		var r = RegulationFieldCalculator.Compute(Sample(rmssd: 10, meanHr: 49), Thresholds, 1, true);
		Assert.IsTrue(r.Hypoarousal > 0.7, $"expected strong hypoarousal, got {r.Hypoarousal}");
	}

	[TestMethod]
	public void Hypoarousal_ZeroForGenuineRest_HrBelowButVariabilityHigh()
	{
		// HR below baseline but RMSSD above baseline = real vagal rest, not shutdown.
		var r = RegulationFieldCalculator.Compute(Sample(rmssd: 70, meanHr: 60), Thresholds, 1, true);
		Assert.AreEqual(0.0, r.Hypoarousal, 0.001);
	}

	[TestMethod]
	public void Hypoarousal_ZeroWhenActivated()
	{
		// HR above baseline = sympathetic activation, not hypoarousal.
		var r = RegulationFieldCalculator.Compute(Sample(rmssd: 20, meanHr: 90), Thresholds, 1, true);
		Assert.AreEqual(0.0, r.Hypoarousal, 0.001);
	}

	[TestMethod]
	public void Hypoarousal_ZeroWhenBaselineUnusable()
	{
		var r = RegulationFieldCalculator.Compute(Sample(50, 70, baselineRmssd: 0, baselineHr: 0), Thresholds, 1, true);
		Assert.AreEqual(0.0, r.Hypoarousal, 0.001);
	}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RegulationFieldCalculatorTests"`
Expected: FAIL to compile — `RegulationReading` has no `Hypoarousal`.

- [ ] **Step 3: Implement**

In `RegulationReading.cs`, convert the bare record-struct declaration to one with a body and add an init-only property (keeps every existing positional `new(...)` call compiling, defaulting to 0):

```csharp
public readonly record struct RegulationReading(
	double Index,
	double VariabilityQuality,
	double Confidence,
	double LobeRoundness,
	double LfHfBalance)
{
	/// <summary>
	/// [0, 1] low-arousal collapse signal: rises when HR is well below baseline AND variability
	/// is not elevated (distinct from genuine high-vagal rest). 0 when activated, at rest with
	/// healthy variability, or when the baseline is unusable. Display-only — does NOT drive the
	/// detector (see audit A(b)). Provisional heuristic pending validation against real episodes.
	/// </summary>
	public double Hypoarousal { get; init; }
}
```

In `RegulationFieldCalculator.cs`, add band constants beside the weights (after `HrWeight`, `:16`):

```csharp
	// Hypoarousal display signal: only HR falling beyond HypoHrBand below baseline counts, and
	// it saturates HypoHrSpan further down. Suppressed by healthy variability (see Compute).
	private const double HypoHrBand = 0.10;
	private const double HypoHrSpan = 0.15;
```

At the end of `Compute`, replace the final `return new RegulationReading(index, quality, confidence, lobeRoundness, lfHfBalance);` (`:76`) with:

```csharp
		// Hypoarousal: HR well below baseline, gated by *low* variability so genuine vagal rest
		// (high RMSSD) does not read as collapse. `quality` is RMSSD/baseline clamped to [0,1].
		double hrFall = (sample.BaselineHr - sample.MeanHr) / sample.BaselineHr;
		double hypoarousal = Math.Clamp((hrFall - HypoHrBand) / HypoHrSpan, 0.0, 1.0) * (1.0 - quality);

		return new RegulationReading(index, quality, confidence, lobeRoundness, lfHfBalance)
		{
			Hypoarousal = hypoarousal,
		};
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RegulationFieldCalculatorTests"`
Expected: PASS (all, including the four new tests).

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Core/Regulation/RegulationReading.cs MeltdownMonitor.Core/Regulation/RegulationFieldCalculator.cs MeltdownMonitor.Tests/RegulationFieldCalculatorTests.cs
git commit -m "feat: compute hypoarousal display signal in regulation field"
```

---

## Task 5: Default-safe detector options — additive corroboration + severe-drop confirmation (audit C, D)

**Files:**
- Create: `MeltdownMonitor.Core/Detection/LfHfCorroborationMode.cs`
- Modify: `MeltdownMonitor.Core/Detection/DetectionThresholds.cs`
- Modify: `MeltdownMonitor.Core/Detection/DysregulationDetector.cs`
- Test: `MeltdownMonitor.Tests/DetectionStateMachineTests.cs` (append)

- [ ] **Step 1: Write the failing tests** (append inside the existing class)

```csharp
	private static HrvSample StressedWithLfHf(DateTimeOffset ts, double lfHfRatio, double baselineLfHf)
	{
		// Core Warning conditions met (RMSSD 40% below, HR 20% above), with extended LF/HF present.
		return new HrvSample(ts, 30, 20, 84, 50, 70, DetectorState.Watching)
		{
			BaselineLfHfRatio = baselineLfHf,
			Extended = new ExtendedHrvMetrics(0, 0, lfHfRatio, 0, 0, 0.4, 0),
		};
	}

	[TestMethod]
	public void VetoMode_LfHfNotElevated_SuppressesWarning()
	{
		var detector = new DysregulationDetector(FastThresholds); // default LfHfCorroborationMode.Veto
		var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		detector.Process(NormalSample(start), baselineIsWarm: true);

		// Core met but LF/HF flat (ratio == baseline) → veto blocks Warning.
		DetectorState? last = null;
		for (int i = 1; i <= 10; i++)
		{
			last = detector.Process(StressedWithLfHf(start.AddSeconds(i * 5), lfHfRatio: 1.5, baselineLfHf: 1.5), baselineIsWarm: true);
		}

		Assert.AreEqual(DetectorState.Watching, last, "Veto mode must suppress the Warning when LF/HF isn't elevated.");
	}

	[TestMethod]
	public void AdditiveMode_LfHfNotElevated_StillWarns()
	{
		var thresholds = FastThresholds with { LfHfCorroborationMode = LfHfCorroborationMode.Additive };
		var detector = new DysregulationDetector(thresholds);
		var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		detector.Process(NormalSample(start), baselineIsWarm: true);

		DetectorState? last = null;
		for (int i = 1; i <= 10; i++)
		{
			last = detector.Process(StressedWithLfHf(start.AddSeconds(i * 5), lfHfRatio: 1.5, baselineLfHf: 1.5), baselineIsWarm: true);
		}

		Assert.AreEqual(DetectorState.Warning, last, "Additive mode must not let a flat LF/HF veto a core-satisfied Warning.");
	}

	[TestMethod]
	public void SevereDropConfirmation_DefaultOne_FiresOnFirstSample()
	{
		var detector = new DysregulationDetector(FastThresholds); // SevereDropConfirmationCount default 1
		var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		detector.Process(NormalSample(start), baselineIsWarm: true);

		var state = detector.Process(SeverelySample(start.AddSeconds(5)), baselineIsWarm: true);

		Assert.AreEqual(DetectorState.Alerting, state);
	}

	[TestMethod]
	public void SevereDropConfirmation_Two_RequiresTwoConsecutive()
	{
		var thresholds = FastThresholds with { SevereDropConfirmationCount = 2 };
		var detector = new DysregulationDetector(thresholds);
		var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		detector.Process(NormalSample(start), baselineIsWarm: true);

		var afterFirst = detector.Process(SeverelySample(start.AddSeconds(5)), baselineIsWarm: true);
		Assert.AreEqual(DetectorState.Watching, afterFirst, "One severe sample must not fire when confirmation is 2.");

		var afterSecond = detector.Process(SeverelySample(start.AddSeconds(10)), baselineIsWarm: true);
		Assert.AreEqual(DetectorState.Alerting, afterSecond, "Second consecutive severe sample fires.");
	}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~DetectionStateMachineTests"`
Expected: FAIL to compile — `LfHfCorroborationMode` and the new threshold members don't exist.

- [ ] **Step 3: Implement**

Create `LfHfCorroborationMode.cs`:

```csharp
namespace MeltdownMonitor.Core.Detection;

/// <summary>How an LF/HF baseline participates in the Warning decision.</summary>
public enum LfHfCorroborationMode
{
	/// <summary>
	/// LF/HF can veto a core-satisfied Warning (today's behaviour). More specific, but the
	/// 5-minute LF/HF window lags a fast onset and can suppress the early Warning.
	/// </summary>
	Veto,

	/// <summary>
	/// LF/HF never vetoes; the core RMSSD+HR condition alone enters Warning. Avoids suppressing
	/// the early-warning value proposition. Recommended by the 2026-06-01 clinical audit.
	/// </summary>
	Additive,
}
```

In `DetectionThresholds.cs`, add (after `LfHfWarningRiseFraction`, `:62`):

```csharp
	/// <summary>
	/// Whether LF/HF corroboration can veto a Warning or only strengthen it. Default
	/// <see cref="LfHfCorroborationMode.Veto"/> preserves prior behaviour; the audit recommends
	/// <see cref="LfHfCorroborationMode.Additive"/>. Only consulted when
	/// <see cref="UseLfHfCorroboration"/> is true.
	/// </summary>
	public LfHfCorroborationMode LfHfCorroborationMode { get; init; } = LfHfCorroborationMode.Veto;

	/// <summary>
	/// Consecutive in-contact samples with an immediate-severe RMSSD drop required before the
	/// immediate alert fires. Default 1 (fire on the first qualifying sample). 2 rejects a
	/// transient regularisation (a breath-hold, Valsalva) at the cost of ~one sample of latency.
	/// </summary>
	public int SevereDropConfirmationCount { get; init; } = 1;
```

In `DysregulationDetector.cs`, add a field beside the others (after `_recoveryActive`, `:19`):

```csharp
	private int _severeDropStreak;
```

Add the LF/HF mode gate in `IsWarningConditionMet` — change the corroboration condition (`:209`) from:

```csharp
		if (_thresholds.UseLfHfCorroboration
			&& sample.BaselineLfHfRatio > 0
			&& sample.Extended is { LfHfRatio: > 0 } extended)
```

to:

```csharp
		if (_thresholds.UseLfHfCorroboration
			&& _thresholds.LfHfCorroborationMode == LfHfCorroborationMode.Veto
			&& sample.BaselineLfHfRatio > 0
			&& sample.Extended is { LfHfRatio: > 0 } extended)
```

Add a confirmation helper (place after `IsSevereDropping`, `:250`):

```csharp
	// Counts consecutive immediate-severe samples; fires only once the configured confirmation
	// count is reached. Default count 1 → fires on the first qualifying sample (prior behaviour).
	private bool IsSevereDropConfirmed(HrvSample sample)
	{
		if (IsSevereDropping(sample))
		{
			_severeDropStreak++;
			return _severeDropStreak >= Math.Max(1, _thresholds.SevereDropConfirmationCount);
		}

		_severeDropStreak = 0;
		return false;
	}
```

Replace `if (IsSevereDropping(sample))` in **both** `ProcessWatching` (`:101`) and `ProcessWarning` (`:130`) with `if (IsSevereDropConfirmed(sample))`.

In `Transition` (`:262-269`), reset the streak alongside the recovery flag — add after `_recoveryActive = false;`:

```csharp
		_severeDropStreak = 0;
```

In the contact-lost early return in `Process` (`:60-65`), reset the streak too — add inside the `if (contact == SensorContactStatus.NotDetected)` block alongside the other resets:

```csharp
			_severeDropStreak = 0;
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~DetectionStateMachineTests"`
Expected: PASS (all — the 4 new tests plus every pre-existing test, confirming defaults preserve behaviour).

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Core/Detection/LfHfCorroborationMode.cs MeltdownMonitor.Core/Detection/DetectionThresholds.cs MeltdownMonitor.Core/Detection/DysregulationDetector.cs MeltdownMonitor.Tests/DetectionStateMachineTests.cs
git commit -m "feat: add default-safe additive LF/HF mode and severe-drop confirmation"
```

---

## Task 6: Full suite + warnings-as-errors gate

- [ ] **Step 1: Build Core (warnings-as-errors)**

Run: `dotnet build MeltdownMonitor.Core/MeltdownMonitor.Core.csproj`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 2: Run the full Core+Mobile suite**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj`
Expected: all tests pass (pre-existing + ~19 new).

- [ ] **Step 3: Commit any final touch-ups** (only if needed)

---

## Deferred — Tier 3, awaiting user sign-off (NOT implemented here)

These are designed in the spec but intentionally left for the user to approve, because they add new
alerts/states/vocabulary or touch untested heads:

- **A(b)** — hypoarousal *detection* state + alert path (new alerts for all users; signature must be
  validated against real data via Task 1's analyzer first).
- **A(c)** — `AnnotationLabel.Shutdown` self-report label (backward-compatible persistence, but needs
  product wording + dialog wiring in both untested heads; should land with A(b)).
- **B** — cold-start "calibrate-during-symptom" guard (changes baseline/warm-up semantics across both
  pipelines; also revisit the mobile HealthKit RMSSD-from-sparse-HR seed).
- **Renderer wiring** of the new `RegulationReading.Hypoarousal` field in
  `App/Regulation/RegulationFieldView.cs` and `Mobile/Controls/RegulationField.cs` (untested heads).

## Self-review notes
- **Spec coverage:** Tier 1 (H, E, F, G, A(a)) and Tier 2 (C, D) each map to Tasks 1–5; Tier 3 deferred and listed. ✓
- **Type consistency:** `AlertEfficacyResult`, `DetectionEfficacyAnalyzer.Analyze`, `MinBeatsForMetrics`, `MaxBeatGapSeconds`, `MaxConsecutiveRejections`, `RegulationReading.Hypoarousal`, `LfHfCorroborationMode{Veto,Additive}`, `LfHfCorroborationMode` property, `SevereDropConfirmationCount`, `IsSevereDropConfirmed`, `_severeDropStreak` — names consistent across tasks. ✓
- **Defaults preserve behaviour:** MinBeatsForMetrics=5 (steady state unaffected), MaxBeatGapSeconds=5 (gaps only), Veto mode + SevereDropConfirmationCount=1 reproduce today's detector exactly. ✓
- **No placeholders:** every code step shows complete code. ✓
