# Time-Relative Plot Axes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the desktop Status-window trend charts and the mobile sparkline plot data against real elapsed time (a fixed scrolling window anchored to *now*, with relative tick labels and visible gaps) instead of sample index.

**Architecture:** Presentation-layer only. Two pure Core helpers (`RrTimeAxis`, `RelativeTimeAxis`) hold the testable math. The desktop wraps each metric's value buffer with a parallel timestamp buffer (`TimedSeries`) and switches ImPlot to the two-array `PlotLine`/`PlotStairs` overloads with a custom relative-time x-axis. The mobile sparkline gains timestamp-driven x positioning. Core HRV windowing, detection, baseline, and SQLite persistence are untouched.

**Tech Stack:** C# / .NET 10, MSTest, Hexa.NET.ImPlot 2.2.9 (desktop charts), Avalonia (mobile sparkline), ktsu.Containers `RingBuffer<T>`.

**Reference spec:** `docs/superpowers/specs/2026-06-01-time-relative-plot-axes-design.md`

**Conventions (from the repo / user global CLAUDE.md):**
- Tabs for indentation, CRLF, file-scoped namespaces, braces on all control flow, `Nullable` on, **warnings-as-errors** (no unused members may linger).
- MSTest with **semantic asserts** (`AreEqual`, `CollectionAssert`), not `IsTrue` where avoidable.
- **Do NOT add `Co-Authored-By` lines** to commits.
- Tick-label text uses an **ASCII hyphen** `-`, not `−` (U+2212): the default ImGui/ImPlot font atlas only guarantees basic-latin glyphs, so `−` can render as a missing-glyph box.

**Build/test commands:**
- Core + Mobile tests (any OS): `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj`
- Desktop (Windows only): `dotnet build MeltdownMonitor.App/MeltdownMonitor.App.csproj`
- Mobile (any OS): `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`

---

## Task 1: Core helper — `RrTimeAxis.CumulativeSeconds`

Reconstructs an exact time axis for a run of RR intervals (batched beats share one arrival timestamp, but each RR value *is* the inter-beat gap, so a running sum is exact). Newest beat at 0, older beats negative seconds.

**Files:**
- Create: `MeltdownMonitor.Core/Hrv/RrTimeAxis.cs`
- Test: `MeltdownMonitor.Tests/RrTimeAxisTests.cs`

- [ ] **Step 1: Write the failing test**

Create `MeltdownMonitor.Tests/RrTimeAxisTests.cs`:

```csharp
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class RrTimeAxisTests
{
	[TestMethod]
	public void CumulativeSeconds_NewestBeatIsAtZero()
	{
		double[] x = RrTimeAxis.CumulativeSeconds([800.0, 900.0, 1000.0]);

		Assert.AreEqual(0.0, x[^1], 1e-9, "the most recent beat anchors the axis at 0");
	}

	[TestMethod]
	public void CumulativeSeconds_OlderBeatsAreNegativeAndMonotonic()
	{
		double[] x = RrTimeAxis.CumulativeSeconds([800.0, 900.0, 1000.0]);

		Assert.IsTrue(x[0] < 0.0, "the oldest beat is before 'now'");
		for (int i = 1; i < x.Length; i++)
		{
			Assert.IsTrue(x[i] > x[i - 1], $"x must increase toward 0; x[{i}]={x[i]} x[{i - 1}]={x[i - 1]}");
		}
	}

	[TestMethod]
	public void CumulativeSeconds_SpacingEqualsRrIntervalInSeconds()
	{
		double[] x = RrTimeAxis.CumulativeSeconds([800.0, 900.0, 1000.0]);

		// Beat i sits its own RR interval (ms -> s) after beat i-1.
		Assert.AreEqual(0.900, x[1] - x[0], 1e-9);
		Assert.AreEqual(1.000, x[2] - x[1], 1e-9);
	}

	[TestMethod]
	public void CumulativeSeconds_SingleBeat_IsZero()
	{
		double[] x = RrTimeAxis.CumulativeSeconds([850.0]);

		Assert.AreEqual(1, x.Length);
		Assert.AreEqual(0.0, x[0], 1e-9);
	}

	[TestMethod]
	public void CumulativeSeconds_Empty_ReturnsEmpty()
	{
		double[] x = RrTimeAxis.CumulativeSeconds([]);

		Assert.AreEqual(0, x.Length);
	}
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RrTimeAxisTests"`
Expected: FAIL — `RrTimeAxis` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `MeltdownMonitor.Core/Hrv/RrTimeAxis.cs`:

```csharp
namespace MeltdownMonitor.Core.Hrv;

/// <summary>
/// Reconstructs a time axis for a run of RR intervals. Beats arrive from BLE in
/// batches that share one arrival timestamp, so wall-clock time can't space them
/// apart — but each RR interval <em>is</em> the gap (ms) to the previous beat, so a
/// running sum is an exact relative time axis. The newest beat sits at 0; older beats
/// are negative seconds before it.
/// </summary>
public static class RrTimeAxis
{
	/// <summary>
	/// Returns one x position (seconds) per RR interval, newest at 0 and older beats
	/// negative. Consecutive spacing equals the corresponding RR interval in seconds.
	/// Returns an empty array for empty input.
	/// </summary>
	public static double[] CumulativeSeconds(IReadOnlyList<double> rrMs)
	{
		ArgumentNullException.ThrowIfNull(rrMs);

		int n = rrMs.Count;
		if (n == 0)
		{
			return [];
		}

		var x = new double[n];
		x[0] = 0.0;
		for (int i = 1; i < n; i++)
		{
			x[i] = x[i - 1] + (rrMs[i] / 1000.0);
		}

		double newest = x[n - 1];
		for (int i = 0; i < n; i++)
		{
			x[i] -= newest;
		}

		return x;
	}
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RrTimeAxisTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Core/Hrv/RrTimeAxis.cs MeltdownMonitor.Tests/RrTimeAxisTests.cs
git commit -m "feat: add RrTimeAxis cumulative-RR time axis helper"
```

---

## Task 2: Core helper — `RelativeTimeAxis.Ticks`

Generates relative tick positions (`<= 0` seconds) and labels (`now`, `-30s`, `-1 min`) for a time-relative axis, coarsening the step as the window widens.

**Files:**
- Create: `MeltdownMonitor.Core/Hrv/RelativeTimeAxis.cs`
- Test: `MeltdownMonitor.Tests/RelativeTimeAxisTests.cs`

- [ ] **Step 1: Write the failing test**

Create `MeltdownMonitor.Tests/RelativeTimeAxisTests.cs`:

```csharp
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Tests;

[TestClass]
public class RelativeTimeAxisTests
{
	[TestMethod]
	public void Ticks_FirstTickIsNowAtZero()
	{
		(double[] positions, string[] labels) = RelativeTimeAxis.Ticks(300.0);

		Assert.AreEqual(0.0, positions[0], 1e-9);
		Assert.AreEqual("now", labels[0]);
	}

	[TestMethod]
	public void Ticks_PositionsDescendByStepAndStopAtWindow()
	{
		// 5-min window -> 2-min step.
		(double[] positions, _) = RelativeTimeAxis.Ticks(300.0);

		CollectionAssert.AreEqual(new[] { 0.0, -120.0, -240.0 }, positions);
	}

	[TestMethod]
	public void Ticks_SubMinuteWindow_LabelsSecondsThenMinutes()
	{
		// 60-s window -> 30-s step.
		(_, string[] labels) = RelativeTimeAxis.Ticks(60.0);

		CollectionAssert.AreEqual(new[] { "now", "-30s", "-1 min" }, labels);
	}

	[TestMethod]
	public void Ticks_IncludesTheWindowEdge()
	{
		(double[] positions, _) = RelativeTimeAxis.Ticks(60.0);

		Assert.AreEqual(-60.0, positions[^1], 1e-9, "the far edge of the window gets a tick");
	}

	[TestMethod]
	public void Ticks_WideWindow_UsesCoarseStep()
	{
		// 60-min window -> 10-min step.
		(double[] positions, _) = RelativeTimeAxis.Ticks(3600.0);

		Assert.AreEqual(-600.0, positions[1], 1e-9);
	}

	[TestMethod]
	public void Ticks_LabelsAndPositionsAreSameLength()
	{
		(double[] positions, string[] labels) = RelativeTimeAxis.Ticks(900.0);

		Assert.AreEqual(positions.Length, labels.Length);
	}
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RelativeTimeAxisTests"`
Expected: FAIL — `RelativeTimeAxis` does not exist.

- [ ] **Step 3: Write the implementation**

Create `MeltdownMonitor.Core/Hrv/RelativeTimeAxis.cs`:

```csharp
using System.Globalization;

namespace MeltdownMonitor.Core.Hrv;

/// <summary>
/// Generates relative tick marks ("now", "-1 min", "-30s") for a time-relative plot
/// axis that spans <c>windowSeconds</c> back from the present. Positions are &lt;= 0
/// (seconds before now); the step coarsens as the window widens so labels never crowd.
/// Pure and platform-neutral so both heads share one definition.
/// </summary>
public static class RelativeTimeAxis
{
	/// <summary>
	/// Returns evenly spaced tick positions (seconds, all &lt;= 0, starting at 0 = now
	/// and stepping back to at least -<paramref name="windowSeconds"/>) plus their
	/// labels. Labels under a minute read in seconds ("-30s"); the rest read in minutes.
	/// </summary>
	public static (double[] Positions, string[] Labels) Ticks(double windowSeconds)
	{
		double window = Math.Max(1.0, windowSeconds);
		double step = ChooseStep(window);

		var positions = new List<double>();
		var labels = new List<string>();
		for (double t = 0.0; t >= -window - 1e-6; t -= step)
		{
			positions.Add(t);
			labels.Add(Label(t));
		}

		return ([.. positions], [.. labels]);
	}

	// Tick spacing bands — coarse enough that even a 6-hour window doesn't crowd.
	private static double ChooseStep(double windowSeconds) => windowSeconds switch
	{
		<= 120.0 => 30.0,    // <= 2 min  -> every 30 s
		<= 600.0 => 120.0,   // <= 10 min -> every 2 min
		<= 3600.0 => 600.0,  // <= 60 min -> every 10 min
		_ => 1800.0,         // larger    -> every 30 min
	};

	private static string Label(double t)
	{
		if (t >= -1e-6)
		{
			return "now";
		}

		double seconds = -t;
		if (seconds < 60.0)
		{
			return string.Create(CultureInfo.InvariantCulture, $"-{seconds:0}s");
		}

		double minutes = seconds / 60.0;
		return minutes == Math.Floor(minutes)
			? string.Create(CultureInfo.InvariantCulture, $"-{minutes:0} min")
			: string.Create(CultureInfo.InvariantCulture, $"-{minutes:0.#} min");
	}
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RelativeTimeAxisTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Core/Hrv/RelativeTimeAxis.cs MeltdownMonitor.Tests/RelativeTimeAxisTests.cs
git commit -m "feat: add RelativeTimeAxis relative tick generator"
```

---

## Task 3: Desktop — `TimedSeries` + time-relative Status charts

Wrap each metric's value buffer with a parallel timestamp buffer, then convert every time-series plot in `StatusWindow.cs` to the two-array ImPlot overloads with a shared fixed-window relative-time x-axis. **One atomic task** — the buffer-type change and the plot-helper change must land together to compile under warnings-as-errors. App has no automated tests (Windows-only TFM); verification is `dotnet build` on Windows + a later live run.

**Files:**
- Create: `MeltdownMonitor.App/TimedSeries.cs`
- Modify: `MeltdownMonitor.App/StatusWindow.cs`

- [ ] **Step 1: Create `TimedSeries`**

Create `MeltdownMonitor.App/TimedSeries.cs`:

```csharp
using ktsu.Containers;

namespace MeltdownMonitor.App;

/// <summary>
/// A live series for the Status charts: values plus the wall-clock time each value was
/// recorded, kept in lock-step ring buffers. <see cref="Snapshot"/> hands ImPlot a
/// matching (x, y) pair so points plot against real time, not sample index. Timestamps
/// are stored as Unix epoch seconds.
/// </summary>
internal sealed class TimedSeries(int capacity)
{
	private readonly RingBuffer<float> _values = new(capacity);
	private readonly RingBuffer<double> _epochSeconds = new(capacity);

	public int Count => _values.Count;

	public void PushBack(DateTimeOffset timestamp, float value)
	{
		_values.PushBack(value);
		_epochSeconds.PushBack(timestamp.ToUnixTimeMilliseconds() / 1000.0);
	}

	public void Resize(int capacity)
	{
		_values.Resize(capacity);
		_epochSeconds.Resize(capacity);
	}

	public void Resample(int capacity)
	{
		_values.Resample(capacity);
		_epochSeconds.Resample(capacity);
	}

	/// <summary>
	/// Copies the series out as ImPlot-ready arrays: <paramref name="xs"/> are seconds
	/// relative to <paramref name="nowEpochSeconds"/> (newest near 0, older negative)
	/// and <paramref name="ys"/> are the values. Same length; both empty when no data.
	/// </summary>
	public void Snapshot(double nowEpochSeconds, out float[] xs, out float[] ys)
	{
		int n = _values.Count;
		xs = new float[n];
		ys = new float[n];
		for (int i = 0; i < n; i++)
		{
			xs[i] = (float)(_epochSeconds.At(i) - nowEpochSeconds);
			ys[i] = _values.At(i);
		}
	}
}
```

- [ ] **Step 2: Add the `using` for the Core helpers in `StatusWindow.cs`**

`StatusWindow.cs` already has `using MeltdownMonitor.Core.Hrv;` (line 10) — `RrTimeAxis` and `RelativeTimeAxis` live in that namespace, so no new using is needed. Confirm it's present; if not, add it.

- [ ] **Step 3: Convert the ring-buffer fields to `TimedSeries`**

In `StatusWindow.cs`, replace the field block (currently lines ~43–61):

```csharp
	private readonly RingBuffer<float> _rmssd = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _baselineRmssd = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _pnn50 = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _sdnn = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _meanHr = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _baselineHr = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _lfPower = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _hfPower = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _lfHfRatio = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _baselineLfHf = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _sd1 = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _sd2 = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _sd1Sd2 = new(InitialSparklineCapacity);
	private readonly RingBuffer<float> _contact = new(InitialSparklineCapacity);
	private readonly RingBuffer<double> _recentRr = new(InitialSparklineCapacity);

	// Battery is updated on its own slow cadence (a read on connect plus occasional
	// notifications), so it lives outside AllSparklines and isn't resampled with them.
	private readonly RingBuffer<float> _battery = new(InitialSparklineCapacity);
```

with (only `_recentRr` stays a raw `RingBuffer<double>`):

```csharp
	private readonly TimedSeries _rmssd = new(InitialSparklineCapacity);
	private readonly TimedSeries _baselineRmssd = new(InitialSparklineCapacity);
	private readonly TimedSeries _pnn50 = new(InitialSparklineCapacity);
	private readonly TimedSeries _sdnn = new(InitialSparklineCapacity);
	private readonly TimedSeries _meanHr = new(InitialSparklineCapacity);
	private readonly TimedSeries _baselineHr = new(InitialSparklineCapacity);
	private readonly TimedSeries _lfPower = new(InitialSparklineCapacity);
	private readonly TimedSeries _hfPower = new(InitialSparklineCapacity);
	private readonly TimedSeries _lfHfRatio = new(InitialSparklineCapacity);
	private readonly TimedSeries _baselineLfHf = new(InitialSparklineCapacity);
	private readonly TimedSeries _sd1 = new(InitialSparklineCapacity);
	private readonly TimedSeries _sd2 = new(InitialSparklineCapacity);
	private readonly TimedSeries _sd1Sd2 = new(InitialSparklineCapacity);
	private readonly TimedSeries _contact = new(InitialSparklineCapacity);

	// Raw per-beat RR intervals (ms). No usable per-beat timestamp (batched beats share
	// one arrival time), so its x axis is reconstructed via RrTimeAxis at snapshot time.
	private readonly RingBuffer<double> _recentRr = new(InitialSparklineCapacity);

	// Battery is updated on its own slow cadence (a read on connect plus occasional
	// notifications), so it lives outside AllSparklines and isn't resampled with them.
	private readonly TimedSeries _battery = new(InitialSparklineCapacity);
```

- [ ] **Step 4: Retype `AllSparklines`**

Replace (currently ~line 63):

```csharp
	private RingBuffer<float>[] AllSparklines => [
```

with:

```csharp
	private TimedSeries[] AllSparklines => [
```

(The member list inside the collection is unchanged. `ApplyCapacityIfChanged`'s `rb.Resample(desired)` and `BackfillFromRepository`'s `rb.Resize(desired)` already compile against `TimedSeries`, which now exposes both methods.)

- [ ] **Step 5: Update the push sites in `OnSampleUpdated`**

Replace the body of `OnSampleUpdated` (currently ~lines 236–253) so every `PushBack` passes `sample.Timestamp`:

```csharp
			_rmssd.PushBack(sample.Timestamp, (float)sample.Rmssd);
			_baselineRmssd.PushBack(sample.Timestamp, (float)sample.BaselineRmssd);
			_pnn50.PushBack(sample.Timestamp, (float)sample.Pnn50);
			_meanHr.PushBack(sample.Timestamp, (float)sample.MeanHr);
			_baselineHr.PushBack(sample.Timestamp, (float)sample.BaselineHr);
			_baselineLfHf.PushBack(sample.Timestamp, (float)sample.BaselineLfHfRatio);
			_contact.PushBack(sample.Timestamp, ContactToValue(sample.SensorContact));

			if (sample.Extended is { } ext)
			{
				_sdnn.PushBack(sample.Timestamp, (float)ext.Sdnn);
				_lfPower.PushBack(sample.Timestamp, (float)ext.LfPowerMs2);
				_hfPower.PushBack(sample.Timestamp, (float)ext.HfPowerMs2);
				_lfHfRatio.PushBack(sample.Timestamp, (float)ext.LfHfRatio);
				_sd1.PushBack(sample.Timestamp, (float)ext.SD1);
				_sd2.PushBack(sample.Timestamp, (float)ext.SD2);
				_sd1Sd2.PushBack(sample.Timestamp, (float)ext.SD1SD2Ratio);
			}
```

- [ ] **Step 6: Update `OnBatteryUpdated`**

Replace `_battery.PushBack(reading.Percent);` (currently ~line 274) with:

```csharp
			_battery.PushBack(reading.Timestamp, reading.Percent);
```

- [ ] **Step 7: Update the push sites in `BackfillFromRepository`**

In the sample loop (currently ~lines 314–331), pass `s.Timestamp`:

```csharp
			foreach (var s in samples.TakeLast(desired))
			{
				_rmssd.PushBack(s.Timestamp, (float)s.Rmssd);
				_baselineRmssd.PushBack(s.Timestamp, (float)s.BaselineRmssd);
				_pnn50.PushBack(s.Timestamp, (float)s.Pnn50);
				_meanHr.PushBack(s.Timestamp, (float)s.MeanHr);
				_baselineHr.PushBack(s.Timestamp, (float)s.BaselineHr);
				_baselineLfHf.PushBack(s.Timestamp, (float)s.BaselineLfHfRatio);
				_contact.PushBack(s.Timestamp, ContactToValue(s.SensorContact));

				if (s.Extended is { } ext)
				{
					_sdnn.PushBack(s.Timestamp, (float)ext.Sdnn);
					_lfPower.PushBack(s.Timestamp, (float)ext.LfPowerMs2);
					_hfPower.PushBack(s.Timestamp, (float)ext.HfPowerMs2);
					_lfHfRatio.PushBack(s.Timestamp, (float)ext.LfHfRatio);
					_sd1.PushBack(s.Timestamp, (float)ext.SD1);
					_sd2.PushBack(s.Timestamp, (float)ext.SD2);
					_sd1Sd2.PushBack(s.Timestamp, (float)ext.SD1SD2Ratio);
				}
			}
```

And the battery loop (currently ~lines 335–338):

```csharp
			_battery.Resize(desired);
			foreach (var b in batteries.TakeLast(desired))
			{
				_battery.PushBack(b.Timestamp, b.Percent);
			}
```

- [ ] **Step 8: Remove the now-unused `SnapshotF`, keep `SnapshotD`**

Delete the `SnapshotF` method (currently ~lines 346–355). All its callers are replaced in later steps; leaving it would trip warnings-as-errors (unused) once those callers are gone. Keep `SnapshotD` (still used by `_recentRr`).

- [ ] **Step 9: Add the time-axis helpers**

Add these members to `StatusWindow.cs` (place them near the other plot helpers, e.g. just above `Plot` at ~line 1402):

```csharp
	// Seconds of history the live charts span — the fixed, scrolling x-window width.
	private double WindowSeconds => Math.Max(1.0, _settings.SparklineWindowMinutes * 60.0);

	private static double NowEpochSeconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

	// Configure the shared time-relative x-axis: a fixed window [-window, +pad] re-asserted
	// every frame (so it scrolls with 'now' even when no new data arrives), with relative
	// tick labels. The Y axis flags are caller-chosen (auto-fit for value charts; locked
	// 0..1 with no labels for the contact strip).
	private void SetupTimeAxis(ImPlotAxisFlags yFlags = ImPlotAxisFlags.AutoFit)
	{
		double window = WindowSeconds;
		double rightPad = window * 0.02; // headroom so the newest point doesn't hug the edge

		ImPlot.SetupAxis(ImAxis.X1, string.Empty, ImPlotAxisFlags.None);
		ImPlot.SetupAxis(ImAxis.Y1, string.Empty, yFlags);
		ImPlot.SetupAxisLimits(ImAxis.X1, -window, rightPad, ImPlotCond.Always);

		(double[] positions, string[] labels) = RelativeTimeAxis.Ticks(window);
		if (positions.Length > 0)
		{
			ImPlot.SetupAxisTicks(ImAxis.X1, ref positions[0], positions.Length, labels);
		}
	}

	// Build the RR plot's (x, y): x is the cumulative-RR time axis (newest beat at 0,
	// seconds), y is the RR interval in ms. Reconstructed because batched beats share a
	// timestamp — see RrTimeAxis.
	private static (float[] xs, float[] ys) RrSeries(double[] rrMs)
	{
		double[] secs = RrTimeAxis.CumulativeSeconds(rrMs);
		var xs = new float[secs.Length];
		var ys = new float[rrMs.Length];
		for (int i = 0; i < secs.Length; i++)
		{
			xs[i] = (float)secs[i];
			ys[i] = (float)rrMs[i];
		}

		return (xs, ys);
	}
```

- [ ] **Step 10: Convert the `Plot` / `PlotPair` / `PlotRow` helpers to two-array time plots**

Replace `Plot` (currently ~lines 1402–1416):

```csharp
	private void Plot(string title, float[] xs, float[] ys, Vector2 size)
	{
		// Always draw the frame (even with no data) so rows stay aligned.
		if (ImPlot.BeginPlot(title, size, ImPlotFlags.NoLegend | ImPlotFlags.NoMouseText))
		{
			SetupTimeAxis();
			if (ys.Length >= 2)
			{
				ImPlot.PlotLine(title, ref xs[0], ref ys[0], ys.Length);
			}

			ImPlot.EndPlot();
		}
	}
```

Replace `PlotPair` (currently ~lines 1420–1441):

```csharp
	// One comparison chart (a series plus its baseline) sharing a single auto-fit Y axis,
	// capped to MaxPlotAspect and centred in the available width.
	private void PlotPair(float height, string title,
		string aLabel, float[] aXs, float[] aYs,
		string bLabel, float[] bXs, float[] bYs)
	{
		(Vector2 size, float indent) = CenteredCell(ImGui.GetContentRegionAvail().X, height);
		Indent(indent);

		if (ImPlot.BeginPlot(title, size, ImPlotFlags.NoMouseText))
		{
			SetupTimeAxis();

			if (aYs.Length >= 2)
			{
				ImPlot.PlotLine(aLabel, ref aXs[0], ref aYs[0], aYs.Length);
			}
			if (bYs.Length >= 2)
			{
				ImPlot.PlotLine(bLabel, ref bXs[0], ref bYs[0], bYs.Length);
			}

			ImPlot.EndPlot();
		}
	}
```

Replace `PlotRow` (currently ~lines 1445–1468):

```csharp
	// Lay out N plots in a single row, each sharing the width equally (capped to
	// MaxPlotAspect) and the group centred. Handles a single plot too.
	private void PlotRow(float height, params (string label, float[] xs, float[] ys)[] plots)
	{
		int n = plots.Length;
		if (n == 0)
		{
			return;
		}

		float spacing = ImGui.GetStyle().ItemSpacing.X;
		float avail = ImGui.GetContentRegionAvail().X;
		float cell = MathF.Min((avail - (spacing * (n - 1))) / n, height * MaxPlotAspect);
		float used = (cell * n) + (spacing * (n - 1));
		Indent((avail - used) * 0.5f);

		Vector2 size = new(cell, height);
		for (int i = 0; i < n; i++)
		{
			Plot(plots[i].label, plots[i].xs, plots[i].ys, size);
			if (i < n - 1)
			{
				ImGui.SameLine();
			}
		}
	}
```

- [ ] **Step 11: Convert `DrawHeartRateTab`**

Replace `DrawHeartRateTab` (currently ~lines 778–797):

```csharp
	private void DrawHeartRateTab()
	{
		double now = NowEpochSeconds();
		float[] hrX, hrY, baseHrX, baseHrY;
		double[] rrsD;
		lock (_historyLock)
		{
			_meanHr.Snapshot(now, out hrX, out hrY);
			_baselineHr.Snapshot(now, out baseHrX, out baseHrY);
			rrsD = SnapshotD(_recentRr);
		}

		(float[] rrX, float[] rrY) = RrSeries(rrsD);

		float h = FillRowHeight(2);
		PlotPair(h, "Heart rate vs baseline (bpm)", "HR", hrX, hrY, "Baseline HR", baseHrX, baseHrY);
		PlotRow(h, ("RR intervals (ms, last received beats)", rrX, rrY));
	}
```

- [ ] **Step 12: Convert `DrawTimeDomainTab`**

Replace `DrawTimeDomainTab` (currently ~lines 799–813):

```csharp
	private void DrawTimeDomainTab()
	{
		double now = NowEpochSeconds();
		float[] rmssdX, rmssdY, baseX, baseY, pnnX, pnnY, sdnnX, sdnnY;
		lock (_historyLock)
		{
			_rmssd.Snapshot(now, out rmssdX, out rmssdY);
			_baselineRmssd.Snapshot(now, out baseX, out baseY);
			_pnn50.Snapshot(now, out pnnX, out pnnY);
			_sdnn.Snapshot(now, out sdnnX, out sdnnY);
		}

		float h = FillRowHeight(2);
		PlotPair(h, "RMSSD (ms)", "RMSSD", rmssdX, rmssdY, "Baseline", baseX, baseY);
		PlotRow(h, ("pNN50 (%)", pnnX, pnnY), ("SDNN (ms)", sdnnX, sdnnY));
	}
```

- [ ] **Step 13: Convert `DrawFrequencyTab`**

Replace `DrawFrequencyTab` (currently ~lines 815–836):

```csharp
	private void DrawFrequencyTab()
	{
		double now = NowEpochSeconds();
		float[] lfX, lfY, hfX, hfY, ratioX, ratioY, baseRX, baseRY;
		lock (_historyLock)
		{
			_lfPower.Snapshot(now, out lfX, out lfY);
			_hfPower.Snapshot(now, out hfX, out hfY);
			_lfHfRatio.Snapshot(now, out ratioX, out ratioY);
			_baselineLfHf.Snapshot(now, out baseRX, out baseRY);
		}

		float h = FillRowHeight(2, ImGui.GetTextLineHeightWithSpacing());
		PlotPair(h, "LF/HF ratio (sympathovagal balance)", "LF/HF", ratioX, ratioY, "Baseline LF/HF", baseRX, baseRY);
		PlotRow(h,
			("LF power (ms², 0.04–0.15 Hz)", lfX, lfY),
			("HF power (ms², 0.15–0.40 Hz)", hfX, hfY));

		if (ratioY.Length < 2)
		{
			ImGui.TextDisabled("Frequency metrics need ≥2 minutes of clean beats to populate.");
		}
	}
```

- [ ] **Step 14: Convert `DrawPoincareTab` (line plots only; scatter unchanged)**

Replace `DrawPoincareTab` (currently ~lines 838–862):

```csharp
	private void DrawPoincareTab()
	{
		double now = NowEpochSeconds();
		float[] sd1X, sd1Y, sd2X, sd2Y, ratioX, ratioY;
		double[] rrsD;
		lock (_historyLock)
		{
			_sd1.Snapshot(now, out sd1X, out sd1Y);
			_sd2.Snapshot(now, out sd2X, out sd2Y);
			_sd1Sd2.Snapshot(now, out ratioX, out ratioY);
			rrsD = SnapshotD(_recentRr);
		}

		float[] rrs = new float[rrsD.Length];
		for (int i = 0; i < rrsD.Length; i++)
		{
			rrs[i] = (float)rrsD[i];
		}

		float h = FillRowHeight(3);
		DrawPoincareScatter(rrs, h); // unchanged: scatter, not a time series

		PlotRow(h,
			("SD1 (short-term variability, ms)", sd1X, sd1Y),
			("SD2 (long-term variability, ms)", sd2X, sd2Y));
		PlotRow(h, ("SD1/SD2 ratio (parasympathetic index)", ratioX, ratioY));
	}
```

- [ ] **Step 15: Convert the Overview snapshot block**

In `DrawOverviewTab`, replace the snapshot block + RR conversion (currently ~lines 667–693):

```csharp
		double now = NowEpochSeconds();
		float[] rmssdX, rmssdY, baseRmssdX, baseRmssdY, pnn50X, pnn50Y, sdnnX, sdnnY,
			hrX, hrY, baseHrX, baseHrY, lfX, lfY, hfX, hfY, lfhfX, lfhfY,
			baseLfhfX, baseLfhfY, sd1X, sd1Y, sd2X, sd2Y, sd1sd2X, sd1sd2Y,
			batteryX, batteryY, contactX, contactY;
		double[] rrsD;
		lock (_historyLock)
		{
			_rmssd.Snapshot(now, out rmssdX, out rmssdY);
			_baselineRmssd.Snapshot(now, out baseRmssdX, out baseRmssdY);
			_pnn50.Snapshot(now, out pnn50X, out pnn50Y);
			_sdnn.Snapshot(now, out sdnnX, out sdnnY);
			_meanHr.Snapshot(now, out hrX, out hrY);
			_baselineHr.Snapshot(now, out baseHrX, out baseHrY);
			_lfPower.Snapshot(now, out lfX, out lfY);
			_hfPower.Snapshot(now, out hfX, out hfY);
			_lfHfRatio.Snapshot(now, out lfhfX, out lfhfY);
			_baselineLfHf.Snapshot(now, out baseLfhfX, out baseLfhfY);
			_sd1.Snapshot(now, out sd1X, out sd1Y);
			_sd2.Snapshot(now, out sd2X, out sd2Y);
			_sd1Sd2.Snapshot(now, out sd1sd2X, out sd1sd2Y);
			_battery.Snapshot(now, out batteryX, out batteryY);
			_contact.Snapshot(now, out contactX, out contactY);
			rrsD = SnapshotD(_recentRr);
		}

		(float[] rrX, float[] rrY) = RrSeries(rrsD);
```

- [ ] **Step 16: Update the Overview `charts` array and `OverviewChart` record**

Replace the `charts` collection (currently ~lines 698–713):

```csharp
		OverviewChart[] charts =
		[
			new("RMSSD vs baseline (ms)", rmssdX, rmssdY, baseRmssdX, baseRmssdY),
			new("Heart rate vs baseline (bpm)", hrX, hrY, baseHrX, baseHrY),
			new("LF/HF ratio (sympathovagal balance)", lfhfX, lfhfY, baseLfhfX, baseLfhfY),
			new("pNN50 (%)", pnn50X, pnn50Y, null, null),
			new("SDNN (ms)", sdnnX, sdnnY, null, null),
			new("LF power (ms²)", lfX, lfY, null, null),
			new("HF power (ms²)", hfX, hfY, null, null),
			new("SD1 (ms)", sd1X, sd1Y, null, null),
			new("SD2 (ms)", sd2X, sd2Y, null, null),
			new("SD1/SD2 ratio (parasympathetic index)", sd1sd2X, sd1sd2Y, null, null),
			new("RR intervals (ms)", rrX, rrY, null, null),
			new("Battery (%)", batteryX, batteryY, null, null),
			new("Poincaré (RR[i] vs RR[i+1])", rrX, rrY, null, null, IsScatter: true),
		];
```

Replace the `OverviewChart` record definition (currently ~line 746):

```csharp
	private sealed record OverviewChart(
		string Title, float[] DataXs, float[] DataYs,
		float[]? BaselineXs, float[]? BaselineYs, bool IsScatter = false);
```

- [ ] **Step 17: Convert `DrawOverviewChart` (drop `static`) and its grid call**

Replace `DrawOverviewChart` (currently ~lines 748–776):

```csharp
	private void DrawOverviewChart(OverviewChart chart, Vector2 size)
	{
		if (chart.IsScatter)
		{
			DrawScatterPlot(chart.Title, chart.DataYs, size);
			return;
		}

		ImPlotFlags flags = chart.BaselineYs is null
			? ImPlotFlags.NoMouseText | ImPlotFlags.NoLegend
			: ImPlotFlags.NoMouseText;

		if (ImPlot.BeginPlot(chart.Title, size, flags))
		{
			SetupTimeAxis();

			if (chart.BaselineYs is { Length: >= 2 } by && chart.BaselineXs is { } bx)
			{
				ImPlot.PlotLine("baseline", ref bx[0], ref by[0], by.Length);
			}
			if (chart.DataYs.Length >= 2)
			{
				ImPlot.PlotLine(chart.Title, ref chart.DataXs[0], ref chart.DataYs[0], chart.DataYs.Length);
			}

			ImPlot.EndPlot();
		}
	}
```

`DrawOverviewChart` is now an instance method, so the grid lambda can no longer be `static`. In `DrawOverviewTab` (currently ~lines 722–724) change:

```csharp
		ImGuiWidgets.RowMajorGrid("overview-charts", charts,
			_ => new Vector2(cellW, cellH),
			static (chart, cellSize, itemSize) => DrawOverviewChart(chart, itemSize));
```

to (drop `static`):

```csharp
		ImGuiWidgets.RowMajorGrid("overview-charts", charts,
			_ => new Vector2(cellW, cellH),
			(chart, cellSize, itemSize) => DrawOverviewChart(chart, itemSize));
```

- [ ] **Step 18: Convert the contact step-strip**

Replace the contact-strip plot (currently ~lines 731–743) to share the time axis but keep the locked 0..1 Y and use the two-array `PlotStairs`:

```csharp
		if (ImPlot.BeginPlot("Sensor contact", new Vector2(contactW, contactH),
				ImPlotFlags.NoLegend | ImPlotFlags.NoMouseText))
		{
			SetupTimeAxis(ImPlotAxisFlags.Lock | ImPlotAxisFlags.NoTickLabels);
			ImPlot.SetupAxisLimits(ImAxis.Y1, 0.0, 1.0, ImPlotCond.Always);
			if (contactY.Length >= 2)
			{
				ImPlot.PlotStairs("Sensor contact (1=OK 0=no contact)", ref contactX[0], ref contactY[0], contactY.Length);
			}

			ImPlot.EndPlot();
		}
```

(The local `float[] contact` / `float[] rr` arrays from the old snapshot block are gone — `contactX/contactY` and `rrX/rrY` from Steps 15–16 replace them.)

- [ ] **Step 19: Build the desktop app (Windows)**

Run: `dotnet build MeltdownMonitor.App/MeltdownMonitor.App.csproj`
Expected: Build succeeded, 0 warnings, 0 errors (warnings-as-errors is on).
If the build flags an unused symbol, it's almost certainly leftover `SnapshotF` (Step 8) or a stale local — remove it.

- [ ] **Step 20: Sanity-check Core + Mobile tests still pass**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj`
Expected: PASS (no Core/Mobile regressions; Tasks 1–2 tests included).

- [ ] **Step 21: Commit**

```bash
git add MeltdownMonitor.App/TimedSeries.cs MeltdownMonitor.App/StatusWindow.cs
git commit -m "feat: plot desktop Status charts against a scrolling time axis"
```

---

## Task 4: Mobile — record per-sample timestamps in `NowViewModel`

Track the timestamp of each charted RMSSD/baseline sample so the sparkline can space points by time. Drives the same value+timestamp lock-step invariant the desktop uses.

**Files:**
- Modify: `MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs`
- Test: `MeltdownMonitor.Tests/NowViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `MeltdownMonitor.Tests/NowViewModelTests.cs` (new `[TestMethod]`s plus a timestamped sample factory near the existing `Sample` helper at the bottom of the class):

```csharp
	[TestMethod]
	public void OnSampleUpdated_RecordsOneTimestampPerValue()
	{
		var vm = new NowViewModel();
		var t0 = DateTimeOffset.UtcNow;

		vm.OnSampleUpdated(SampleAt(t0, rmssd: 40));
		vm.OnSampleUpdated(SampleAt(t0.AddSeconds(5), rmssd: 42));

		Assert.AreEqual(2, vm.RmssdTimestamps.Count);
		Assert.AreEqual(vm.RmssdHistory.Count, vm.RmssdTimestamps.Count,
			"every charted value carries exactly one timestamp");
		Assert.AreEqual(5.0, vm.RmssdTimestamps[1] - vm.RmssdTimestamps[0], 1e-6,
			"timestamps are epoch seconds spaced by the real sample gap");
	}

	[TestMethod]
	public void TrimHistory_KeepsValuesAndTimestampsTheSameLength()
	{
		var vm = new NowViewModel();
		var t0 = DateTimeOffset.UtcNow;

		for (int i = 0; i < 400; i++) // past the 360-point cap
		{
			vm.OnSampleUpdated(SampleAt(t0.AddSeconds(i), rmssd: 30 + (i % 5)));
		}

		Assert.AreEqual(vm.RmssdHistory.Count, vm.RmssdTimestamps.Count);
		Assert.IsTrue(vm.RmssdTimestamps.Count <= 360, $"capped, was {vm.RmssdTimestamps.Count}");
	}

	private static HrvSample SampleAt(DateTimeOffset timestamp, double rmssd) =>
		new(
			timestamp,
			rmssd,
			Pnn50: 20,
			MeanHr: 70,
			BaselineRmssd: 50,
			BaselineHr: 65,
			DetectorState.Watching);
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~NowViewModelTests"`
Expected: FAIL — `RmssdTimestamps` does not exist (compile error).

- [ ] **Step 3: Add the timestamps collection, expose it, append + trim**

In `NowViewModel.cs`:

Add the backing field next to `_rmssdHistory` / `_baselineHistory` (currently ~lines 24–25):

```csharp
	private readonly ObservableCollection<double> _rmssdTimestamps = [];
```

Expose it next to `RmssdHistory` / `BaselineHistory` (currently ~lines 65–66):

```csharp
	/// <summary>Unix epoch seconds for each <see cref="RmssdHistory"/> / <see cref="BaselineHistory"/>
	/// point — both series share this sample cadence. Lets the sparkline space points by real time.</summary>
	public IReadOnlyList<double> RmssdTimestamps => _rmssdTimestamps;
```

In `OnSampleUpdated`, append the timestamp alongside the values and raise it (currently ~lines 381–386):

```csharp
		_rmssdHistory.Add(sample.Rmssd);
		_baselineHistory.Add(sample.BaselineRmssd);
		_rmssdTimestamps.Add(sample.Timestamp.ToUnixTimeMilliseconds() / 1000.0);
		TrimHistory();

		Raise(nameof(RmssdHistory));
		Raise(nameof(BaselineHistory));
		Raise(nameof(RmssdTimestamps));
```

In `TrimHistory`, trim the timestamps in lock-step (currently ~lines 462–473):

```csharp
	private void TrimHistory()
	{
		while (_rmssdHistory.Count > SparklineMaxPoints)
		{
			_rmssdHistory.RemoveAt(0);
		}

		while (_baselineHistory.Count > SparklineMaxPoints)
		{
			_baselineHistory.RemoveAt(0);
		}

		while (_rmssdTimestamps.Count > SparklineMaxPoints)
		{
			_rmssdTimestamps.RemoveAt(0);
		}
	}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~NowViewModelTests"`
Expected: PASS (existing NowViewModel tests + 2 new).

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs MeltdownMonitor.Tests/NowViewModelTests.cs
git commit -m "feat: record per-sample timestamps for the mobile sparkline"
```

---

## Task 5: Mobile — time-relative `Sparkline` rendering + binding

Position sparkline points by their timestamp within a fixed window instead of evenly by index. Degrade gracefully to index spacing when timestamps are absent/misaligned.

**Files:**
- Modify: `MeltdownMonitor.Mobile/Controls/Sparkline.cs`
- Modify: `MeltdownMonitor.Mobile/Views/NowView.axaml`

- [ ] **Step 1: Add the `Timestamps` and `WindowSeconds` styled properties**

In `Sparkline.cs`, add after the `LineThicknessProperty` registration (currently ~lines 30–31):

```csharp
	public static readonly StyledProperty<IReadOnlyList<double>?> TimestampsProperty =
		AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Timestamps));

	public static readonly StyledProperty<double> WindowSecondsProperty =
		AvaloniaProperty.Register<Sparkline, double>(nameof(WindowSeconds), 60.0);
```

Add both to the `AffectsRender` list in the static constructor (currently ~lines 33–41):

```csharp
	static Sparkline()
	{
		AffectsRender<Sparkline>(
			ValuesProperty,
			BaselineValuesProperty,
			TimestampsProperty,
			WindowSecondsProperty,
			LineBrushProperty,
			BaselineBrushProperty,
			LineThicknessProperty);
	}
```

Add the CLR accessors next to the other properties (e.g. after `BaselineValues`, ~line 53):

```csharp
	/// <summary>Unix epoch seconds, one per <see cref="Values"/> / <see cref="BaselineValues"/>
	/// point. When present (and length-matched) the series is spaced by real time within
	/// <see cref="WindowSeconds"/>; otherwise points fall back to even index spacing.</summary>
	public IReadOnlyList<double>? Timestamps
	{
		get => GetValue(TimestampsProperty);
		set => SetValue(TimestampsProperty, value);
	}

	/// <summary>Width of the time window (seconds) shown across the control. Default 60.</summary>
	public double WindowSeconds
	{
		get => GetValue(WindowSecondsProperty);
		set => SetValue(WindowSecondsProperty, value);
	}
```

- [ ] **Step 2: Pass timestamps + window through `Render` to `DrawSeries`**

Replace `Render` (currently ~lines 73–118) so it captures the window/now once and forwards them:

```csharp
	public override void Render(DrawingContext context)
	{
		var bounds = new Rect(Bounds.Size);
		if (bounds.Width <= 0 || bounds.Height <= 0)
		{
			return;
		}

		var values = Values;
		var baseline = BaselineValues;
		var timestamps = Timestamps;
		double window = Math.Max(1.0, WindowSeconds);
		double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

		double max = 0;
		if (values is not null)
		{
			foreach (double v in values)
			{
				if (v > max) max = v;
			}
		}

		if (baseline is not null)
		{
			foreach (double v in baseline)
			{
				if (v > max) max = v;
			}
		}

		if (max <= 0)
		{
			max = 100;
		}

		// Headroom so peaks don't kiss the top edge.
		max *= 1.15;

		if (baseline is not null && baseline.Count >= 2)
		{
			DrawSeries(context, baseline, timestamps, now, window, bounds, max, BaselineBrush, LineThickness, dashed: true);
		}

		if (values is not null && values.Count >= 2)
		{
			DrawSeries(context, values, timestamps, now, window, bounds, max, LineBrush, LineThickness, dashed: false);
		}
	}
```

- [ ] **Step 3: Position x by time in `DrawSeries`**

Replace `DrawSeries` (currently ~lines 120–142):

```csharp
	private static void DrawSeries(
		DrawingContext context,
		IReadOnlyList<double> series,
		IReadOnlyList<double>? timestamps,
		double now,
		double window,
		Rect bounds,
		double max,
		IBrush brush,
		double thickness,
		bool dashed)
	{
		var pen = dashed
			? new Pen(brush, thickness) { DashStyle = new DashStyle(new[] { 4.0, 4.0 }, 0) }
			: new Pen(brush, thickness);

		// Time-relative x when timestamps line up with the series; otherwise fall back to
		// even index spacing so the control still renders if a caller omits timestamps.
		bool timed = timestamps is not null && timestamps.Count == series.Count;

		double XAt(int i)
		{
			if (timed)
			{
				double age = now - timestamps![i];                 // seconds before now
				double frac = 1.0 - Math.Clamp(age / window, 0.0, 1.0);
				return frac * bounds.Width;                          // newest at the right edge
			}

			return bounds.Width * i / Math.Max(1, series.Count - 1);
		}

		var prev = new Point(XAt(0), ToY(series[0], max, bounds.Height));
		for (int i = 1; i < series.Count; i++)
		{
			var next = new Point(XAt(i), ToY(series[i], max, bounds.Height));
			context.DrawLine(pen, prev, next);
			prev = next;
		}
	}
```

(`ToY` is unchanged.)

- [ ] **Step 4: Bind the new properties in `NowView.axaml`**

In `NowView.axaml`, extend the `<ctl:Sparkline>` element (currently ~lines 22–27) with the timestamps binding and a 60-second window:

```xml
                    <ctl:Sparkline Grid.Row="1"
                                   Height="48"
                                   Margin="0,8,0,0"
                                   Values="{Binding RmssdHistory}"
                                   BaselineValues="{Binding BaselineHistory}"
                                   Timestamps="{Binding RmssdTimestamps}"
                                   WindowSeconds="60"
                                   LineThickness="2" />
```

- [ ] **Step 5: Build the mobile project**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj`
Expected: PASS (all tests, including Tasks 1, 2, 4).

- [ ] **Step 7: Commit**

```bash
git add MeltdownMonitor.Mobile/Controls/Sparkline.cs MeltdownMonitor.Mobile/Views/NowView.axaml
git commit -m "feat: space the mobile sparkline by real time"
```

---

## Task 6: Live verification (manual — required, cannot be automated)

Per the repo's batching gotcha, the real-time scroll/gap behaviour can only be confirmed on the live app with a real Polar sensor — synthetic/replay sources can't reproduce it. This task has no code; it gates "done".

**Files:** none.

- [ ] **Step 1: Run the desktop app with a sensor connected**

Run: `dotnet run --project MeltdownMonitor.App`
Open the Status window (tray menu) and connect a Polar H10 / Verity Sense.

- [ ] **Step 2: Confirm the time-relative behaviour on each tab**

Check, across Overview / Heart Rate / Time-Domain HRV / Frequency-Domain / Poincaré:
- X tick labels read `now`, `-1 min`, etc. (ASCII hyphen, no missing-glyph boxes).
- The window is the configured "History (min)" width and **scrolls left in real time**, including when beats momentarily stop.
- A deliberate sensor dropout (cover/remove the sensor briefly) shows as a **visible horizontal gap**, not a compressed segment.
- The RR-intervals chart spaces beats by their RR duration (denser when HR is high).
- The Poincaré scatter and the sensor-contact strip still render correctly; the contact strip's Y stays pinned 0..1.
- Changing "History (min)" in Settings rescales the window and its tick density.

- [ ] **Step 3: (If available) confirm the mobile sparkline**

If an iOS device/simulator is available, confirm the `NowView` sparkline spaces points by time over its 60-second window. (iOS builds only on macOS with the `ios` workload; otherwise note this as deferred to the next macOS session.)

- [ ] **Step 4: Note the deliberate consequence for sparse series (battery)**

The Battery overview cell is now time-relative too, so within a short window it may show few or no points (battery readings arrive rarely). This is the intended "honest gaps" behaviour, not a bug. If it reads as broken in practice, a follow-up could give battery its own wider/auto-fit axis — out of scope here.

---

## Self-Review

**Spec coverage:**
- Time-relative x for desktop trend charts → Task 3 (Steps 9–18). ✔
- Per-beat RR cumulative derivation → Task 1 + `RrSeries`/Step 11,14,15. ✔
- Relative tick labels → Task 2 + `SetupTimeAxis`/Step 9. ✔
- Fixed scrolling window anchored to now → `SetupTimeAxis` `SetupAxisLimits(..., Always)` + per-frame `now`. ✔
- Extended-vs-base length mismatch dissolved → each series carries its own xs (`TimedSeries.Snapshot`). ✔
- Battery/contact own timestamps; backfill uses persisted timestamps → Steps 6,7,15. ✔
- Mobile sparkline time-relative → Tasks 4 + 5. ✔
- No Core pipeline/persistence change → only `Core/Hrv/{RrTimeAxis,RelativeTimeAxis}.cs` added; nothing in beat→HRV→detection→persistence touched. ✔
- Regulation Field + Poincaré scatter excluded → `DrawScatterPlot`/`DrawPoincareScatter` untouched (Step 14,17); Regulation Field not referenced. ✔
- Tests: `RrTimeAxisTests`, `RelativeTimeAxisTests`, `NowViewModelTests` additions; live-run gate. ✔

**Placeholder scan:** none — every step has concrete code or an exact command + expected output.

**Type consistency:**
- `TimedSeries.Snapshot(double, out float[], out float[])` used consistently (Steps 1, 11–18).
- Two-array plots use `ref float` xs + `ref float` ys (matches the verified `PlotLine/PlotStairs(string, ref float, ref float, int)` overloads).
- `SetupAxisTicks(ImAxis, ref double, int, string[])` — matches the verified overload; `RelativeTimeAxis.Ticks` returns `(double[], string[])`.
- `RrSeries`/`RrTimeAxis.CumulativeSeconds(IReadOnlyList<double>)` returns `double[]`, cast to `float[]` for plotting.
- `OverviewChart(Title, DataXs, DataYs, BaselineXs, BaselineYs, IsScatter)` used consistently in Steps 16–17.
- `RmssdTimestamps` named identically in VM (Task 4), test (Task 4), and XAML binding (Task 5).
