# iOS / Mobile Feature Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the Avalonia `MeltdownMonitor.Mobile` UI (the only iOS UI) to feature parity with the ImGui `MeltdownMonitor.App` desktop head: a full HRV metrics chart suite, Regulation Field rendering parity, and matching settings.

**Architecture:** Three workstreams over a shared data spine. (A) A new scrolling **Metrics** tab with hand-rolled `DrawingContext` chart controls fed by a new `MetricsViewModel`. (B) Regulation Field rendering parity by porting the desktop's missing layers and routing glow through a SkiaSharp `ICustomDrawOperation` (`SKBlendMode.Plus`). (C) Settings + pipeline parity (new `MobileSettings` knobs, a new `Pipeline.BeatReceived` event). Pure coordinate/geometry math is extracted so it is unit-testable; the render bodies and Skia glow are verified later on device.

**Tech Stack:** .NET 10, Avalonia 12.0.4, SkiaSharp 3.119.4 (managed `SkiaSharp` + `Avalonia.Skia`), MSTest. Core helpers reused verbatim: `LemniscateGeometry`, `RegulationFieldCalculator`, `RegulationFieldHistogram`/`RegulationFieldDensity`, `HypoarousalVisual`.

**Build/test loop (Windows):**
```
dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj
dotnet build MeltdownMonitor.Core/MeltdownMonitor.Core.csproj
dotnet test  MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj
```
The `net10.0-ios` heads (`Ble.Apple`, `iOS`) compile only on macOS/CI — the csproj edit in Task 13 is made here but not built here.

**Definition of done:** all of the above build clean and tests pass. Glow bloom, RR playhead motion, and visual layout are tuned later on macOS + a real Polar sensor (per CLAUDE.md, those are not verifiable from tests).

---

## File map

**Create**
- `MeltdownMonitor.Core/Regulation/RegulationFieldGeometry.cs` — pure `VagalToneOffsetY` shared by both heads.
- `MeltdownMonitor.Mobile/Controls/ChartScale.cs` — pure data→pixel mapping helpers.
- `MeltdownMonitor.Mobile/Controls/MetricChart.cs` — multi-series line/stairs chart.
- `MeltdownMonitor.Mobile/Controls/ScatterChart.cs` — Poincaré scatter.
- `MeltdownMonitor.Mobile/Controls/AdditiveSkiaLayer.cs` — `ICustomDrawOperation` additive-blend helper.
- `MeltdownMonitor.Mobile/ViewModels/MetricsViewModel.cs` — chart data source.
- `MeltdownMonitor.Mobile/Views/MetricsView.axaml` (+ `.axaml.cs`) — the Metrics tab.
- `MeltdownMonitor.Tests/ChartScaleTests.cs`, `MetricsViewModelTests.cs`, `RegulationFieldGeometryTests.cs`, `RrTextureTests.cs`, `MobileSettingsSerializerTests.cs` (extend if present).

**Modify**
- `MeltdownMonitor.Mobile/Pipeline.cs` — add `BeatReceived` event (after `Pipeline.cs:301`).
- `MeltdownMonitor.Mobile/MobileSettings.cs` — add opacity/heatmap/threshold/refresh knobs.
- `MeltdownMonitor.Mobile/Controls/RegulationField.cs` — add layers + Skia glow + vagal-tone Y.
- `MeltdownMonitor.Mobile/ViewModels/{RootViewModel,SettingsViewModel}.cs` — Metrics tab + new settings.
- `MeltdownMonitor.Mobile/Views/{RootView,SettingsView,NowView}.axaml` — tab + sliders + bindings.
- `MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj` — SkiaSharp + Avalonia.Skia refs.
- `MeltdownMonitor.iOS/IosCompositionRoot.cs` — construct/subscribe `MetricsViewModel`, pass new providers.

---

## Phase 0 — Foundations

### Task 1: `Pipeline.BeatReceived` event

**Files:**
- Modify: `MeltdownMonitor.Mobile/Pipeline.cs` (event near line 86; raise after line 301)
- Test: `MeltdownMonitor.Tests/MobilePipelineBeatReceivedTests.cs` (create)

- [ ] **Step 1: Write the failing test**

The Tests project references Mobile. A `Pipeline` needs an `IBeatSource`, `MobileSettings`, and a `MeltdownRepository`. Mirror the construction already used by other pipeline tests (search `new Pipeline(` under `MeltdownMonitor.Tests`); reuse the same in-memory repository + synthetic beat source helper. If none exists, use the synthetic source pattern from `NowViewModelTests` fixtures.

```csharp
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Mobile;

namespace MeltdownMonitor.Tests;

[TestClass]
public sealed class MobilePipelineBeatReceivedTests
{
    [TestMethod]
    public async Task BeatReceived_fires_once_per_non_paused_beat()
    {
        // Arrange: build a Pipeline over a finite synthetic beat source that yields 3 beats.
        // (Reuse the existing test helpers for repository + source; see other Pipeline tests.)
        var fixture = MobilePipelineFixture.WithBeats(rrMs: [820, 805, 815]);
        int received = 0;
        fixture.Pipeline.BeatReceived += _ => received++;

        // Act
        await fixture.RunToCompletionAsync();

        // Assert
        Assert.AreEqual(3, received);
    }
}
```

If `MobilePipelineFixture` does not already exist, create it in the test project wrapping the same construction the other Mobile pipeline tests use; keep it minimal (a `Pipeline`, an in-memory/temp-file `MeltdownRepository`, and an `IBeatSource` that yields a fixed RR list then completes).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~MobilePipelineBeatReceivedTests"`
Expected: FAIL — `Pipeline` has no `BeatReceived` member (compile error).

- [ ] **Step 3: Add the event and raise it**

In `MeltdownMonitor.Mobile/Pipeline.cs`, add alongside the other public events (near line 86):

```csharp
/// <summary>Fires for each beat the source delivers while not paused, after it is
/// persisted. Mirrors the desktop pipeline's BeatReceived so the Metrics charts and
/// the Regulation Field's RR-textured trace can consume the raw RR stream. Handlers
/// filter <see cref="Beat.IsArtifact"/> themselves, as the desktop consumers do.</summary>
public event Action<Beat>? BeatReceived;
```

Then in `RunAsync`, immediately after `_repository.InsertBeat(beat);` (line 301):

```csharp
_repository.InsertBeat(beat);
BeatReceived?.Invoke(beat);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~MobilePipelineBeatReceivedTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Mobile/Pipeline.cs MeltdownMonitor.Tests/MobilePipelineBeatReceivedTests.cs
git commit -m "feat(mobile): add Pipeline.BeatReceived event for charts and RR trace"
```

---

### Task 2: New `MobileSettings` knobs + serializer round-trip

**Files:**
- Modify: `MeltdownMonitor.Mobile/MobileSettings.cs`
- Test: `MeltdownMonitor.Tests/MobileSettingsSerializerTests.cs` (create or extend)

- [ ] **Step 1: Write the failing test**

```csharp
using MeltdownMonitor.Mobile;
using MeltdownMonitor.Mobile.Services;

namespace MeltdownMonitor.Tests;

[TestClass]
public sealed class MobileSettingsSerializerTests
{
    [TestMethod]
    public void Roundtrip_preserves_new_regulation_and_refresh_knobs()
    {
        var s = new MobileSettings
        {
            LobeOpacity = 0.55,
            TrailOpacity = 0.65,
            HeatmapOpacity = 0.40,
            HeatmapPeakOpacity = 0.72,
            HeatmapRegionOpacity = 0.58,
            HeatmapRegionThreshold = 0.45,
            HistogramOpacity = 0.62,
            RegulationHeatmapLength = 900,
            HrvEmitIntervalSeconds = 4.0,
            SparklineWindowMinutes = 30,
        };

        var back = MobileSettingsSerializer.Deserialize(MobileSettingsSerializer.Serialize(s));

        Assert.AreEqual(0.55, back.LobeOpacity, 1e-9);
        Assert.AreEqual(0.65, back.TrailOpacity, 1e-9);
        Assert.AreEqual(0.40, back.HeatmapOpacity, 1e-9);
        Assert.AreEqual(0.72, back.HeatmapPeakOpacity, 1e-9);
        Assert.AreEqual(0.58, back.HeatmapRegionOpacity, 1e-9);
        Assert.AreEqual(0.45, back.HeatmapRegionThreshold, 1e-9);
        Assert.AreEqual(0.62, back.HistogramOpacity, 1e-9);
        Assert.AreEqual(900, back.RegulationHeatmapLength);
        Assert.AreEqual(4.0, back.HrvEmitIntervalSeconds, 1e-9);
        Assert.AreEqual(30, back.SparklineWindowMinutes);
    }

    [TestMethod]
    public void Defaults_match_desktop_tuned_values()
    {
        var s = new MobileSettings();
        Assert.AreEqual(0.60, s.LobeOpacity, 1e-9);
        Assert.AreEqual(0.70, s.TrailOpacity, 1e-9);
        Assert.AreEqual(0.35, s.HeatmapOpacity, 1e-9);
        Assert.AreEqual(0.70, s.HeatmapPeakOpacity, 1e-9);
        Assert.AreEqual(0.55, s.HeatmapRegionOpacity, 1e-9);
        Assert.AreEqual(0.50, s.HeatmapRegionThreshold, 1e-9);
        Assert.AreEqual(0.60, s.HistogramOpacity, 1e-9);
        Assert.AreEqual(720, s.RegulationHeatmapLength);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~MobileSettingsSerializerTests"`
Expected: FAIL — properties don't exist (compile error).

- [ ] **Step 3: Add the properties**

Append to `MobileSettings` (defaults copied from the desktop `AppSettings` tuned values referenced in `App/StatusWindow.cs` help text):

```csharp
/// <summary>Opacity of the Regulation Field live-trace lobes (additive). Default 60%.</summary>
public double LobeOpacity { get; set; } = 0.60;

/// <summary>Opacity of the Regulation Field comet trail (additive). Default 70%.</summary>
public double TrailOpacity { get; set; } = 0.70;

/// <summary>Overall opacity of the dwell heatmap. Default 35%.</summary>
public double HeatmapOpacity { get; set; } = 0.35;

/// <summary>Opacity of the peak-dwell crosshair. Default 70%.</summary>
public double HeatmapPeakOpacity { get; set; } = 0.70;

/// <summary>Opacity of the dashed high-density region box. Default 55%.</summary>
public double HeatmapRegionOpacity { get; set; } = 0.55;

/// <summary>Bucket-share of the peak a cell must reach to fall inside the region box. Default 50%.</summary>
public double HeatmapRegionThreshold { get; set; } = 0.50;

/// <summary>Opacity of the axis histograms (additive). Default 60%.</summary>
public double HistogramOpacity { get; set; } = 0.60;

/// <summary>Readings the dwell heatmap accumulates over (≈1 h at 720). Default 720.</summary>
public int RegulationHeatmapLength { get; set; } = 720;

/// <summary>How often an HRV sample is emitted (seconds). Mirrors the desktop refresh knob. Default 5.</summary>
public double HrvEmitIntervalSeconds { get; set; } = 5.0;

/// <summary>How much history the Metrics charts show (minutes). Default 60.</summary>
public int SparklineWindowMinutes { get; set; } = 60;
```

Serialization needs no change — `MobileSettingsSerializer` serializes the whole POCO.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~MobileSettingsSerializerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Mobile/MobileSettings.cs MeltdownMonitor.Tests/MobileSettingsSerializerTests.cs
git commit -m "feat(mobile): add regulation/heatmap/refresh settings knobs for parity"
```

---

## Phase A — Metrics tab

### Task 3: `ChartScale` pure mapping helpers

**Files:**
- Create: `MeltdownMonitor.Mobile/Controls/ChartScale.cs`
- Test: `MeltdownMonitor.Tests/ChartScaleTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using MeltdownMonitor.Mobile.Controls;

namespace MeltdownMonitor.Tests;

[TestClass]
public sealed class ChartScaleTests
{
    [TestMethod]
    public void FitRange_pads_min_and_max()
    {
        var (min, max) = ChartScale.FitRange([[10.0, 20.0, 30.0]], padFraction: 0.1);
        Assert.AreEqual(8.0, min, 1e-9);   // 10 - 0.1*(30-10)
        Assert.AreEqual(32.0, max, 1e-9);  // 30 + 0.1*(30-10)
    }

    [TestMethod]
    public void FitRange_flat_series_expands_to_a_visible_band()
    {
        var (min, max) = ChartScale.FitRange([[50.0, 50.0]], padFraction: 0.1);
        Assert.IsTrue(max > min, "a flat series must still produce a non-zero range");
    }

    [TestMethod]
    public void FitRange_ignores_null_and_empty_series()
    {
        var (min, max) = ChartScale.FitRange([null, [], [5.0, 15.0]], padFraction: 0.0);
        Assert.AreEqual(5.0, min, 1e-9);
        Assert.AreEqual(15.0, max, 1e-9);
    }

    [TestMethod]
    public void Y_maps_max_to_top_and_min_to_bottom()
    {
        Assert.AreEqual(0.0, ChartScale.Y(30, 10, 30, height: 100), 1e-9);   // max at top
        Assert.AreEqual(100.0, ChartScale.Y(10, 10, 30, height: 100), 1e-9); // min at bottom
    }

    [TestMethod]
    public void TimeX_places_now_at_right_edge_and_window_start_at_left()
    {
        Assert.AreEqual(200.0, ChartScale.TimeX(1000, now: 1000, windowSec: 60, width: 200), 1e-9);
        Assert.AreEqual(0.0, ChartScale.TimeX(940, now: 1000, windowSec: 60, width: 200), 1e-9);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~ChartScaleTests"`
Expected: FAIL — `ChartScale` not defined.

- [ ] **Step 3: Implement `ChartScale`**

```csharp
namespace MeltdownMonitor.Mobile.Controls;

/// <summary>
/// Pure data→pixel mapping for the hand-rolled metric charts. Kept free of any
/// Avalonia type so it is unit-testable. Mirrors the desktop ImPlot behaviour:
/// padded auto-fit on Y, newest sample at the right edge on a time X axis.
/// </summary>
internal static class ChartScale
{
    /// <summary>Padded [min, max] across one or more series (nulls/empties skipped).
    /// A flat or empty overall range expands to a small visible band so a line still renders.</summary>
    public static (double Min, double Max) FitRange(IReadOnlyList<IReadOnlyList<double>?> series, double padFraction)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var s in series)
        {
            if (s is null) continue;
            foreach (double v in s)
            {
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        if (double.IsInfinity(min) || double.IsInfinity(max))
        {
            return (0.0, 1.0);
        }

        double span = max - min;
        if (span <= 0)
        {
            double band = Math.Abs(max) > 1e-9 ? Math.Abs(max) * 0.1 : 1.0;
            return (min - band, max + band);
        }

        double pad = span * padFraction;
        return (min - pad, max + pad);
    }

    /// <summary>Pixel Y for a value: max maps to 0 (top), min maps to <paramref name="height"/> (bottom).</summary>
    public static double Y(double value, double min, double max, double height)
    {
        double range = max - min;
        if (range <= 0) return height * 0.5;
        double frac = (value - min) / range;
        return height - (Math.Clamp(frac, 0.0, 1.0) * height);
    }

    /// <summary>Pixel X for a timestamp (epoch seconds): now at the right edge,
    /// (now - windowSec) at the left edge. Clamped to the control width.</summary>
    public static double TimeX(double timestampSec, double now, double windowSec, double width)
    {
        double w = Math.Max(1.0, windowSec);
        double age = now - timestampSec;
        double frac = 1.0 - Math.Clamp(age / w, 0.0, 1.0);
        return frac * width;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~ChartScaleTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Mobile/Controls/ChartScale.cs MeltdownMonitor.Tests/ChartScaleTests.cs
git commit -m "feat(mobile): pure ChartScale data-to-pixel mapping for metric charts"
```

---

### Task 4: `MetricChart` control

**Files:**
- Create: `MeltdownMonitor.Mobile/Controls/MetricChart.cs`

This control has no unit test (it renders to a surface); its math lives in `ChartScale` (Task 3). Keep the render body thin — it only maps + strokes.

- [ ] **Step 1: Implement `MetricChart`**

```csharp
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MeltdownMonitor.Mobile.Controls;

/// <summary>
/// Hand-rolled multi-series time chart for the Metrics tab. Renders a primary series
/// and an optional baseline overlay (dashed) on a shared padded auto-fit Y axis and a
/// time X axis (newest at the right). A <see cref="Stairs"/> mode renders the primary
/// series as a step plot (used for the binary sensor-contact strip). Title is drawn
/// top-left. Mirrors the desktop ImPlot charts; the mapping is <see cref="ChartScale"/>.
/// </summary>
public sealed class MetricChart : Control
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<MetricChart, string?>(nameof(Title));
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<MetricChart, IReadOnlyList<double>?>(nameof(Values));
    public static readonly StyledProperty<IReadOnlyList<double>?> BaselineValuesProperty =
        AvaloniaProperty.Register<MetricChart, IReadOnlyList<double>?>(nameof(BaselineValues));
    public static readonly StyledProperty<IReadOnlyList<double>?> TimestampsProperty =
        AvaloniaProperty.Register<MetricChart, IReadOnlyList<double>?>(nameof(Timestamps));
    public static readonly StyledProperty<double> WindowSecondsProperty =
        AvaloniaProperty.Register<MetricChart, double>(nameof(WindowSeconds), 3600.0);
    public static readonly StyledProperty<IBrush> LineBrushProperty =
        AvaloniaProperty.Register<MetricChart, IBrush>(nameof(LineBrush), new SolidColorBrush(Color.FromRgb(0x8A, 0xAD, 0xF4)));
    public static readonly StyledProperty<IBrush> BaselineBrushProperty =
        AvaloniaProperty.Register<MetricChart, IBrush>(nameof(BaselineBrush), new SolidColorBrush(Color.FromRgb(0x80, 0x87, 0xA2)));
    public static readonly StyledProperty<bool> StairsProperty =
        AvaloniaProperty.Register<MetricChart, bool>(nameof(Stairs));
    public static readonly StyledProperty<double?> YMinProperty =
        AvaloniaProperty.Register<MetricChart, double?>(nameof(YMin));
    public static readonly StyledProperty<double?> YMaxProperty =
        AvaloniaProperty.Register<MetricChart, double?>(nameof(YMax));

    static MetricChart() =>
        AffectsRender<MetricChart>(TitleProperty, ValuesProperty, BaselineValuesProperty,
            TimestampsProperty, WindowSecondsProperty, LineBrushProperty, BaselineBrushProperty,
            StairsProperty, YMinProperty, YMaxProperty);

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IReadOnlyList<double>? BaselineValues { get => GetValue(BaselineValuesProperty); set => SetValue(BaselineValuesProperty, value); }
    public IReadOnlyList<double>? Timestamps { get => GetValue(TimestampsProperty); set => SetValue(TimestampsProperty, value); }
    public double WindowSeconds { get => GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }
    public IBrush LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public IBrush BaselineBrush { get => GetValue(BaselineBrushProperty); set => SetValue(BaselineBrushProperty, value); }
    public bool Stairs { get => GetValue(StairsProperty); set => SetValue(StairsProperty, value); }
    /// <summary>Forces a fixed Y floor (e.g. 0 for the contact strip); null = auto-fit.</summary>
    public double? YMin { get => GetValue(YMinProperty); set => SetValue(YMinProperty, value); }
    /// <summary>Forces a fixed Y ceiling (e.g. 1 for the contact strip); null = auto-fit.</summary>
    public double? YMax { get => GetValue(YMaxProperty); set => SetValue(YMaxProperty, value); }

    private static readonly IBrush TitleBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98));
    private const double TitleH = 16.0;

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 2 || h <= 2) return;

        if (!string.IsNullOrEmpty(Title))
        {
            var ft = new FormattedText(Title!, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 11, TitleBrush);
            context.DrawText(ft, new Point(2, 0));
        }

        double plotTop = string.IsNullOrEmpty(Title) ? 0 : TitleH;
        double plotH = h - plotTop;
        if (plotH <= 2) return;

        var values = Values;
        var baseline = BaselineValues;
        var (autoMin, autoMax) = ChartScale.FitRange([values, baseline], padFraction: 0.12);
        double min = YMin ?? autoMin;
        double max = YMax ?? autoMax;
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var ts = Timestamps;

        if (baseline is { Count: >= 2 })
        {
            Stroke(context, baseline, ts, now, w, plotTop, plotH, min, max,
                new Pen(BaselineBrush, 1.5) { DashStyle = new DashStyle([4.0, 4.0], 0) }, stairs: false);
        }
        if (values is { Count: >= 2 })
        {
            Stroke(context, values, ts, now, w, plotTop, plotH, min, max, new Pen(LineBrush, 2.0), Stairs);
        }
    }

    private void Stroke(DrawingContext context, IReadOnlyList<double> series, IReadOnlyList<double>? ts,
        double now, double w, double top, double plotH, double min, double max, IPen pen, bool stairs)
    {
        bool timed = ts is not null && ts.Count == series.Count;
        double X(int i) => timed
            ? ChartScale.TimeX(ts![i], now, WindowSeconds, w)
            : w * i / Math.Max(1, series.Count - 1);
        double Y(int i) => top + ChartScale.Y(series[i], min, max, plotH);

        var prev = new Point(X(0), Y(0));
        for (int i = 1; i < series.Count; i++)
        {
            var next = new Point(X(i), Y(i));
            if (stairs)
            {
                var corner = new Point(next.X, prev.Y);
                context.DrawLine(pen, prev, corner);
                context.DrawLine(pen, corner, next);
            }
            else
            {
                context.DrawLine(pen, prev, next);
            }
            prev = next;
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add MeltdownMonitor.Mobile/Controls/MetricChart.cs
git commit -m "feat(mobile): MetricChart line/stairs control for the Metrics tab"
```

---

### Task 5: `ScatterChart` control + Poincaré pairing helper

**Files:**
- Create: `MeltdownMonitor.Mobile/Controls/ScatterChart.cs`
- Test: `MeltdownMonitor.Tests/ChartScaleTests.cs` (add cases for the pairing helper)

- [ ] **Step 1: Write the failing test (add to `ChartScaleTests`)**

```csharp
[TestMethod]
public void ConsecutivePairs_builds_rr_i_vs_rr_iplus1()
{
    var (xs, ys) = ScatterSeries.ConsecutivePairs([800.0, 810.0, 790.0]);
    CollectionAssert.AreEqual(new[] { 800.0, 810.0 }, xs);
    CollectionAssert.AreEqual(new[] { 810.0, 790.0 }, ys);
}

[TestMethod]
public void ConsecutivePairs_short_input_is_empty()
{
    var (xs, ys) = ScatterSeries.ConsecutivePairs([800.0]);
    Assert.AreEqual(0, xs.Length);
    Assert.AreEqual(0, ys.Length);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~ChartScaleTests"`
Expected: FAIL — `ScatterSeries` not defined.

- [ ] **Step 3: Implement `ScatterChart` + `ScatterSeries`**

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MeltdownMonitor.Mobile.Controls;

/// <summary>Pure helpers for the Poincaré scatter (testable without a surface).</summary>
internal static class ScatterSeries
{
    /// <summary>Consecutive RR pairs (RR[i], RR[i+1]). Empty when fewer than two samples.</summary>
    public static (double[] Xs, double[] Ys) ConsecutivePairs(IReadOnlyList<double> rr)
    {
        if (rr.Count < 2) return ([], []);
        int n = rr.Count - 1;
        var xs = new double[n];
        var ys = new double[n];
        for (int i = 0; i < n; i++) { xs[i] = rr[i]; ys[i] = rr[i + 1]; }
        return (xs, ys);
    }
}

/// <summary>
/// Poincaré plot: RR[i] (x) vs RR[i+1] (y), square equal axes with a faint identity
/// line so the cloud reads at 45°. Mirrors the desktop ImPlot scatter.
/// </summary>
public sealed class ScatterChart : Control
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ScatterChart, string?>(nameof(Title));
    public static readonly StyledProperty<IReadOnlyList<double>?> RrIntervalsProperty =
        AvaloniaProperty.Register<ScatterChart, IReadOnlyList<double>?>(nameof(RrIntervals));

    static ScatterChart() => AffectsRender<ScatterChart>(TitleProperty, RrIntervalsProperty);

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public IReadOnlyList<double>? RrIntervals { get => GetValue(RrIntervalsProperty); set => SetValue(RrIntervalsProperty, value); }

    private static readonly IBrush TitleBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98));
    private static readonly IBrush PointBrush = new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0xFF), 0.85);
    private static readonly IPen IdentityPen = new Pen(new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C), 0.40), 1);

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 2 || h <= 2) return;

        if (!string.IsNullOrEmpty(Title))
        {
            var ft = new FormattedText(Title!, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 11, TitleBrush);
            context.DrawText(ft, new Point(2, 0));
        }
        double top = string.IsNullOrEmpty(Title) ? 0 : 16;
        double side = Math.Min(w, h - top);
        if (side <= 2) return;
        double offX = (w - side) / 2;

        var rr = RrIntervals;
        var (xs, ys) = rr is null ? ([], []) : ScatterSeries.ConsecutivePairs(rr);
        if (xs.Length < 1) return;

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (double v in rr!) { if (v < min) min = v; if (v > max) max = v; }
        double range = max - min;
        if (range <= 0) { min -= 50; max += 50; range = max - min; }

        Point Map(double x, double y) => new(
            offX + ((x - min) / range * side),
            top + (side - ((y - min) / range * side)));

        context.DrawLine(IdentityPen, Map(min, min), Map(max, max));
        for (int i = 0; i < xs.Length; i++)
        {
            context.DrawEllipse(PointBrush, null, Map(xs[i], ys[i]), 2.0, 2.0);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~ChartScaleTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Mobile/Controls/ScatterChart.cs MeltdownMonitor.Tests/ChartScaleTests.cs
git commit -m "feat(mobile): ScatterChart Poincaré control + pairing helper"
```

---

### Task 6: `MetricsViewModel` — series buffers + live ingestion

**Files:**
- Create: `MeltdownMonitor.Mobile/ViewModels/MetricsViewModel.cs`
- Test: `MeltdownMonitor.Tests/MetricsViewModelTests.cs`

The VM mirrors the desktop `StatusWindow`'s `TimedSeries` set. Each series is a parallel `(timestamps, values)` pair trimmed to a capacity derived from the window-minutes provider and emit cadence (same formula as the desktop `DesiredCapacity`). Use plain `List<double>` exposed as `IReadOnlyList<double>` (the chart controls only read). Marshal to the UI thread exactly like `NowViewModel` (look at its `RunOnUi` helper and reuse the same pattern).

- [ ] **Step 1: Write the failing test**

```csharp
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Mobile.ViewModels;

namespace MeltdownMonitor.Tests;

[TestClass]
public sealed class MetricsViewModelTests
{
    private static HrvSample SampleAt(DateTimeOffset t, double rmssd, double hr) =>
        new()
        {
            Timestamp = t,
            Rmssd = rmssd,
            Pnn50 = 10,
            MeanHr = hr,
            BaselineRmssd = 50,
            BaselineHr = 70,
            BaselineLfHfRatio = 1.5,
            SensorContact = SensorContactStatus.Detected,
            State = DetectorState.Idle,
        };

    [TestMethod]
    public void OnSampleUpdated_appends_rmssd_and_baseline_with_timestamps()
    {
        var vm = new MetricsViewModel();
        var t0 = DateTimeOffset.UnixEpoch.AddSeconds(1000);
        vm.OnSampleUpdated(SampleAt(t0, rmssd: 40, hr: 80));
        vm.OnSampleUpdated(SampleAt(t0.AddSeconds(5), rmssd: 42, hr: 82));

        CollectionAssert.AreEqual(new[] { 40.0, 42.0 }, vm.Rmssd.ToList());
        CollectionAssert.AreEqual(new[] { 50.0, 50.0 }, vm.BaselineRmssd.ToList());
        CollectionAssert.AreEqual(new[] { 80.0, 82.0 }, vm.MeanHr.ToList());
        Assert.AreEqual(2, vm.RmssdTimestamps.Count);
    }

    [TestMethod]
    public void OnBeatReceived_collects_non_artifact_rr_only()
    {
        var vm = new MetricsViewModel();
        vm.OnBeatReceived(new Beat(DateTimeOffset.UnixEpoch, 820, IsArtifact: false));
        vm.OnBeatReceived(new Beat(DateTimeOffset.UnixEpoch, 9999, IsArtifact: true));
        vm.OnBeatReceived(new Beat(DateTimeOffset.UnixEpoch, 810, IsArtifact: false));
        CollectionAssert.AreEqual(new[] { 820.0, 810.0 }, vm.RecentRr.ToList());
    }

    [TestMethod]
    public void Capacity_trims_oldest_when_window_exceeded()
    {
        // 1-minute window at 5 s cadence ⇒ ~12 samples retained.
        var vm = new MetricsViewModel(windowMinutesProvider: () => 1, emitIntervalProvider: () => 5.0);
        var t0 = DateTimeOffset.UnixEpoch.AddSeconds(1000);
        for (int i = 0; i < 50; i++) vm.OnSampleUpdated(SampleAt(t0.AddSeconds(i * 5), rmssd: i, hr: 70));
        Assert.IsTrue(vm.Rmssd.Count <= 13, $"expected ~12 retained, got {vm.Rmssd.Count}");
        Assert.AreEqual(49.0, vm.Rmssd[^1], 1e-9); // newest kept
    }
}
```

Confirm the `Beat` constructor shape (`new Beat(timestamp, rrMs, IsArtifact)`) against `MeltdownMonitor.Core/Beats/Beat.cs`; adjust the test's construction to match the real record signature if it differs.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~MetricsViewModelTests"`
Expected: FAIL — `MetricsViewModel` not defined.

- [ ] **Step 3: Implement `MetricsViewModel`**

```csharp
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.Mobile.ViewModels;

/// <summary>
/// Backing data for the Metrics tab: live ring-buffered histories of every HRV metric
/// the desktop StatusWindow charts, plus the raw RR stream for the Poincaré/RR plots.
/// Fed from Pipeline.SampleUpdated / BeatReceived / BatteryUpdated and backfilled from
/// the repository on load. Capacity tracks the window-minutes setting and emit cadence,
/// mirroring the desktop DesiredCapacity formula.
/// </summary>
public sealed class MetricsViewModel : ViewModelBase
{
    private const int RrCapacity = 512;

    private readonly Func<int> _windowMinutes;
    private readonly Func<double> _emitInterval;

    private readonly List<double> _rmssd = [], _baselineRmssd = [], _pnn50 = [], _sdnn = [];
    private readonly List<double> _meanHr = [], _baselineHr = [];
    private readonly List<double> _lf = [], _hf = [], _lfhf = [], _baselineLfHf = [];
    private readonly List<double> _sd1 = [], _sd2 = [], _sd1sd2 = [];
    private readonly List<double> _battery = [], _contact = [];
    private readonly List<double> _ts = [], _batteryTs = [], _contactTs = [];
    private readonly List<double> _recentRr = [];

    public MetricsViewModel(Func<int>? windowMinutesProvider = null, Func<double>? emitIntervalProvider = null)
    {
        _windowMinutes = windowMinutesProvider ?? (() => 60);
        _emitInterval = emitIntervalProvider ?? (() => 5.0);
    }

    public IReadOnlyList<double> Rmssd => _rmssd;
    public IReadOnlyList<double> BaselineRmssd => _baselineRmssd;
    public IReadOnlyList<double> Pnn50 => _pnn50;
    public IReadOnlyList<double> Sdnn => _sdnn;
    public IReadOnlyList<double> MeanHr => _meanHr;
    public IReadOnlyList<double> BaselineHr => _baselineHr;
    public IReadOnlyList<double> LfPower => _lf;
    public IReadOnlyList<double> HfPower => _hf;
    public IReadOnlyList<double> LfHfRatio => _lfhf;
    public IReadOnlyList<double> BaselineLfHf => _baselineLfHf;
    public IReadOnlyList<double> Sd1 => _sd1;
    public IReadOnlyList<double> Sd2 => _sd2;
    public IReadOnlyList<double> Sd1Sd2 => _sd1sd2;
    public IReadOnlyList<double> Battery => _battery;
    public IReadOnlyList<double> Contact => _contact;
    public IReadOnlyList<double> RmssdTimestamps => _ts;
    public IReadOnlyList<double> BatteryTimestamps => _batteryTs;
    public IReadOnlyList<double> ContactTimestamps => _contactTs;
    public IReadOnlyList<double> RecentRr => _recentRr;

    /// <summary>Window width (seconds) the charts span — drives MetricChart.WindowSeconds.</summary>
    public double WindowSeconds => Math.Max(60, _windowMinutes()) * 60.0;

    private int Capacity =>
        Math.Max(60, (int)(_windowMinutes() * 60.0 / Math.Max(0.5, _emitInterval())));

    public void OnSampleUpdated(HrvSample s) => RunOnUi(() =>
    {
        double ts = s.Timestamp.ToUnixTimeMilliseconds() / 1000.0;
        Append(_rmssd, s.Rmssd); Append(_baselineRmssd, s.BaselineRmssd);
        Append(_pnn50, s.Pnn50); Append(_meanHr, s.MeanHr); Append(_baselineHr, s.BaselineHr);
        Append(_baselineLfHf, s.BaselineLfHfRatio);
        Append(_contact, s.SensorContact == SensorContactStatus.NotDetected ? 0.0 : 1.0);
        Append(_contactTs, ts);
        Append(_ts, ts);

        if (s.Extended is { } ext)
        {
            Append(_sdnn, ext.Sdnn); Append(_lf, ext.LfPowerMs2); Append(_hf, ext.HfPowerMs2);
            Append(_lfhf, ext.LfHfRatio); Append(_sd1, ext.SD1); Append(_sd2, ext.SD2);
            Append(_sd1sd2, ext.SD1SD2Ratio);
        }

        RaiseAllSeriesChanged();
    });

    public void OnBeatReceived(Beat beat) => RunOnUi(() =>
    {
        if (beat.IsArtifact) return;
        _recentRr.Add(beat.RrMs);
        while (_recentRr.Count > RrCapacity) _recentRr.RemoveAt(0);
        Raise(nameof(RecentRr));
    });

    public void OnBatteryUpdated(BatteryReading reading) => RunOnUi(() =>
    {
        _battery.Add(reading.Percent);
        _batteryTs.Add(reading.Timestamp.ToUnixTimeMilliseconds() / 1000.0);
        Trim(_battery); Trim(_batteryTs);
        Raise(nameof(Battery)); Raise(nameof(BatteryTimestamps));
    });

    private void Append(List<double> list, double value) { list.Add(value); Trim(list); }

    private void Trim(List<double> list)
    {
        int cap = Capacity;
        while (list.Count > cap) list.RemoveAt(0);
    }

    private void RaiseAllSeriesChanged()
    {
        foreach (var name in new[]
        {
            nameof(Rmssd), nameof(BaselineRmssd), nameof(Pnn50), nameof(Sdnn), nameof(MeanHr),
            nameof(BaselineHr), nameof(LfPower), nameof(HfPower), nameof(LfHfRatio),
            nameof(BaselineLfHf), nameof(Sd1), nameof(Sd2), nameof(Sd1Sd2), nameof(Contact),
            nameof(RmssdTimestamps), nameof(ContactTimestamps), nameof(WindowSeconds),
        })
        {
            Raise(name);
        }
    }
}
```

Match `RunOnUi` and `Raise` to the actual `ViewModelBase`/`NowViewModel` helpers (read `ViewModels/ViewModelBase.cs` and `NowViewModel`'s `RunOnUi`). If `RunOnUi` lives on `NowViewModel` rather than the base, lift it to `ViewModelBase` (or copy the same Dispatcher-marshalling helper) so `MetricsViewModel` can share it. Confirm `BatteryReading` member names (`Percent`, `Timestamp`) and `HrvExtended` member names (`Sdnn`, `LfPowerMs2`, `HfPowerMs2`, `LfHfRatio`, `SD1`, `SD2`, `SD1SD2Ratio`) against Core (they match the desktop `StatusWindow.OnSampleUpdated` usage).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~MetricsViewModelTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Mobile/ViewModels/MetricsViewModel.cs MeltdownMonitor.Tests/MetricsViewModelTests.cs
git commit -m "feat(mobile): MetricsViewModel live HRV series for the Metrics tab"
```

---

### Task 7: `MetricsViewModel` repository backfill

**Files:**
- Modify: `MeltdownMonitor.Mobile/ViewModels/MetricsViewModel.cs`
- Test: `MeltdownMonitor.Tests/MetricsViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[TestMethod]
public void Backfill_seeds_series_from_persisted_samples_oldest_first()
{
    var vm = new MetricsViewModel();
    var t0 = DateTimeOffset.UnixEpoch.AddSeconds(1000);
    var history = new List<HrvSample>
    {
        SampleAt(t0, rmssd: 30, hr: 70),
        SampleAt(t0.AddSeconds(5), rmssd: 33, hr: 72),
    };

    vm.Backfill(history, batteries: []);

    CollectionAssert.AreEqual(new[] { 30.0, 33.0 }, vm.Rmssd.ToList());
    Assert.AreEqual(2, vm.RmssdTimestamps.Count);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~MetricsViewModelTests.Backfill"`
Expected: FAIL — `Backfill` not defined.

- [ ] **Step 3: Add `Backfill` and a repository loader**

Add to `MetricsViewModel`:

```csharp
/// <summary>Seeds the series from persisted history (oldest first) so the charts aren't
/// blank on open. Mirrors the desktop StatusWindow.BackfillFromRepository.</summary>
public void Backfill(IReadOnlyList<HrvSample> samples, IReadOnlyList<BatteryReading> batteries) => RunOnUi(() =>
{
    int cap = Capacity;
    foreach (var s in samples.TakeLast(cap))
    {
        double ts = s.Timestamp.ToUnixTimeMilliseconds() / 1000.0;
        _rmssd.Add(s.Rmssd); _baselineRmssd.Add(s.BaselineRmssd); _pnn50.Add(s.Pnn50);
        _meanHr.Add(s.MeanHr); _baselineHr.Add(s.BaselineHr); _baselineLfHf.Add(s.BaselineLfHfRatio);
        _contact.Add(s.SensorContact == SensorContactStatus.NotDetected ? 0.0 : 1.0);
        _contactTs.Add(ts); _ts.Add(ts);
        if (s.Extended is { } ext)
        {
            _sdnn.Add(ext.Sdnn); _lf.Add(ext.LfPowerMs2); _hf.Add(ext.HfPowerMs2);
            _lfhf.Add(ext.LfHfRatio); _sd1.Add(ext.SD1); _sd2.Add(ext.SD2); _sd1sd2.Add(ext.SD1SD2Ratio);
        }
    }

    foreach (var b in batteries.TakeLast(cap))
    {
        _battery.Add(b.Percent);
        _batteryTs.Add(b.Timestamp.ToUnixTimeMilliseconds() / 1000.0);
    }

    RaiseAllSeriesChanged();
    Raise(nameof(Battery)); Raise(nameof(BatteryTimestamps));
});

/// <summary>Convenience overload that reads history from the repository on a background
/// thread, then applies it on the UI thread. Errors are swallowed (a missing/locked DB
/// must never block the tab), matching the desktop backfill.</summary>
public async Task LoadFromRepositoryAsync(string databasePath)
{
    var to = DateTimeOffset.UtcNow;
    var from = to.AddMinutes(-Math.Max(1, _windowMinutes()));
    try
    {
        var samples = await Task.Run(() => MeltdownRepository.ReadHistory(databasePath, from, to)).ConfigureAwait(false);
        IReadOnlyList<BatteryReading> batteries;
        try { batteries = await Task.Run(() => MeltdownRepository.ReadBatteryHistory(databasePath, from, to)).ConfigureAwait(false); }
        catch { batteries = []; }
        Backfill(samples, batteries);
    }
    catch
    {
        // best-effort: leave the series to fill from live samples
    }
}
```

Confirm the static `MeltdownRepository.ReadHistory(path, from, to)` / `ReadBatteryHistory(path, from, to)` signatures against `App/StatusWindow.cs:299,309` (same calls).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~MetricsViewModelTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Mobile/ViewModels/MetricsViewModel.cs MeltdownMonitor.Tests/MetricsViewModelTests.cs
git commit -m "feat(mobile): MetricsViewModel repository backfill"
```

---

### Task 8: `MetricsView.axaml` grouped sections

**Files:**
- Create: `MeltdownMonitor.Mobile/Views/MetricsView.axaml` + `MetricsView.axaml.cs`

- [ ] **Step 1: Create the view**

`MetricsView.axaml` — a scrolling list of section headers + charts. Each `MetricChart`/`ScatterChart` binds to a `MetricsViewModel` series. Use a fixed per-chart height (e.g. 140) and `WindowSeconds` bound to the VM.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:MeltdownMonitor.Mobile.ViewModels"
             xmlns:ctl="clr-namespace:MeltdownMonitor.Mobile.Controls"
             x:Class="MeltdownMonitor.Mobile.Views.MetricsView"
             x:DataType="vm:MetricsViewModel">
    <ScrollViewer>
        <StackPanel Margin="16" Spacing="14">

            <TextBlock Text="Time-Domain" FontWeight="SemiBold" />
            <ctl:MetricChart Height="140" Title="RMSSD vs baseline (ms)"
                             Values="{Binding Rmssd}" BaselineValues="{Binding BaselineRmssd}"
                             Timestamps="{Binding RmssdTimestamps}" WindowSeconds="{Binding WindowSeconds}" />
            <ctl:MetricChart Height="120" Title="pNN50 (%)"
                             Values="{Binding Pnn50}" Timestamps="{Binding RmssdTimestamps}"
                             WindowSeconds="{Binding WindowSeconds}" />
            <ctl:MetricChart Height="120" Title="SDNN (ms)"
                             Values="{Binding Sdnn}" Timestamps="{Binding RmssdTimestamps}"
                             WindowSeconds="{Binding WindowSeconds}" />

            <TextBlock Text="Heart Rate" FontWeight="SemiBold" Margin="0,8,0,0" />
            <ctl:MetricChart Height="140" Title="Heart rate vs baseline (bpm)"
                             Values="{Binding MeanHr}" BaselineValues="{Binding BaselineHr}"
                             Timestamps="{Binding RmssdTimestamps}" WindowSeconds="{Binding WindowSeconds}" />
            <ctl:MetricChart Height="120" Title="RR intervals (ms, recent beats)"
                             Values="{Binding RecentRr}" />

            <TextBlock Text="Frequency-Domain" FontWeight="SemiBold" Margin="0,8,0,0" />
            <ctl:MetricChart Height="140" Title="LF/HF ratio vs baseline"
                             Values="{Binding LfHfRatio}" BaselineValues="{Binding BaselineLfHf}"
                             Timestamps="{Binding RmssdTimestamps}" WindowSeconds="{Binding WindowSeconds}" />
            <ctl:MetricChart Height="120" Title="LF power (ms²)"
                             Values="{Binding LfPower}" Timestamps="{Binding RmssdTimestamps}"
                             WindowSeconds="{Binding WindowSeconds}" />
            <ctl:MetricChart Height="120" Title="HF power (ms²)"
                             Values="{Binding HfPower}" Timestamps="{Binding RmssdTimestamps}"
                             WindowSeconds="{Binding WindowSeconds}" />

            <TextBlock Text="Poincaré" FontWeight="SemiBold" Margin="0,8,0,0" />
            <ctl:ScatterChart Height="220" Title="Poincaré (RR[i] vs RR[i+1])"
                              RrIntervals="{Binding RecentRr}" />
            <ctl:MetricChart Height="120" Title="SD1 (ms)"
                             Values="{Binding Sd1}" Timestamps="{Binding RmssdTimestamps}"
                             WindowSeconds="{Binding WindowSeconds}" />
            <ctl:MetricChart Height="120" Title="SD2 (ms)"
                             Values="{Binding Sd2}" Timestamps="{Binding RmssdTimestamps}"
                             WindowSeconds="{Binding WindowSeconds}" />
            <ctl:MetricChart Height="120" Title="SD1/SD2 ratio"
                             Values="{Binding Sd1Sd2}" Timestamps="{Binding RmssdTimestamps}"
                             WindowSeconds="{Binding WindowSeconds}" />

            <TextBlock Text="Sensor" FontWeight="SemiBold" Margin="0,8,0,0" />
            <ctl:MetricChart Height="100" Title="Battery (%)"
                             Values="{Binding Battery}" Timestamps="{Binding BatteryTimestamps}"
                             WindowSeconds="{Binding WindowSeconds}" YMin="0" YMax="100" />
            <ctl:MetricChart Height="60" Title="Sensor contact (1=OK, 0=lost)" Stairs="True"
                             Values="{Binding Contact}" Timestamps="{Binding ContactTimestamps}"
                             WindowSeconds="{Binding WindowSeconds}" YMin="0" YMax="1" />
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

`MetricsView.axaml.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MeltdownMonitor.Mobile.Views;

public partial class MetricsView : UserControl
{
    public MetricsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
```

(Match the exact code-behind idiom used by the other `*.axaml.cs` views — e.g. whether they declare `InitializeComponent` or rely on the generated partial.)

- [ ] **Step 2: Build to verify it compiles (XAML compiled bindings on)**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: build succeeds (compiled-binding errors here mean a property name mismatch with `MetricsViewModel`).

- [ ] **Step 3: Commit**

```bash
git add MeltdownMonitor.Mobile/Views/MetricsView.axaml MeltdownMonitor.Mobile/Views/MetricsView.axaml.cs
git commit -m "feat(mobile): MetricsView grouped HRV chart sections"
```

---

### Task 9: Wire the Metrics tab into the shell + composition

**Files:**
- Modify: `MeltdownMonitor.Mobile/ViewModels/RootViewModel.cs`
- Modify: `MeltdownMonitor.Mobile/Views/RootView.axaml`
- Modify: `MeltdownMonitor.iOS/IosCompositionRoot.cs`

- [ ] **Step 1: Add `Metrics` to `RootViewModel`**

Add a `MetricsViewModel metrics` constructor parameter (after `settings_tab`), assign `Metrics = metrics;`, expose `public MetricsViewModel Metrics { get; }`, and update `CreateDefault()` to pass `new MetricsViewModel()`:

```csharp
public RootViewModel(
    MobileSettings settings,
    NowViewModel now,
    HistoryViewModel history,
    SettingsViewModel settings_tab,
    MetricsViewModel metrics,
    IMobileSettingsStore? store = null)
{
    _settings = settings;
    _store = store;
    Now = now;
    History = history;
    Settings = settings_tab;
    Metrics = metrics;
    Disclaimer = new DisclaimerViewModel(AcceptDisclaimer);
}

public MetricsViewModel Metrics { get; }
```

In `CreateDefault()`:

```csharp
return new RootViewModel(
    settings,
    new NowViewModel(),
    new HistoryViewModel(),
    new SettingsViewModel(settings),
    new MetricsViewModel());
```

- [ ] **Step 2: Add the tab to `RootView.axaml`**

Insert between the Now and History `TabItem`s:

```xml
<TabItem Header="Metrics">
    <views:MetricsView DataContext="{Binding Metrics}" />
</TabItem>
```

- [ ] **Step 3: Construct + subscribe in `IosCompositionRoot`**

Mirror how `NowViewModel` is built and subscribed (`IosCompositionRoot.cs:117`). Construct the metrics VM with the settings-backed providers, subscribe it to the pipeline, kick off the backfill, and pass it to `RootViewModel`:

```csharp
var metrics = new MetricsViewModel(
    windowMinutesProvider: () => settings.SparklineWindowMinutes,
    emitIntervalProvider: () => settings.HrvEmitIntervalSeconds);
pipeline.SampleUpdated += metrics.OnSampleUpdated;
pipeline.BeatReceived += metrics.OnBeatReceived;
pipeline.BatteryUpdated += metrics.OnBatteryUpdated;
_ = metrics.LoadFromRepositoryAsync(settings.DatabasePath);   // match the actual DB-path accessor on this composition root

return new RootViewModel(settings, _now, _history, settingsTab, metrics, _store);
```

Use whatever the composition root already uses for the database path (the same value handed to the `MeltdownRepository`). If it isn't a `settings.DatabasePath`, pass that variable instead.

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: build succeeds. (The iOS head compiles on macOS/CI; verify the edit is consistent by re-reading the file.)

- [ ] **Step 5: Commit**

```bash
git add MeltdownMonitor.Mobile/ViewModels/RootViewModel.cs MeltdownMonitor.Mobile/Views/RootView.axaml MeltdownMonitor.iOS/IosCompositionRoot.cs
git commit -m "feat(mobile): add Metrics tab to the shell and wire it in the iOS composition root"
```

---

## Phase B — Regulation Field rendering parity

> Phase B ports layers from `App/Regulation/RegulationFieldView.cs` (the authoritative reference). Translate ImGui `ImDrawListPtr` calls to either Avalonia `DrawingContext` (alpha-over chrome) or, for additive glow regions, SkiaSharp inside the custom draw op (Task 10). The Catppuccin Macchiato colours already exist as `static readonly Color` fields in the Mobile `RegulationField`; add any missing ones (`Mauve`, `Red`, `Yellow`, `Mantle`, `Subtext0`) by copying their hex from `App/Regulation/MacchiatoPalette.cs`. None of Phase B is unit-testable except the pure helpers in Tasks 11–12; the rest is verified on device.

### Task 10: SkiaSharp refs + additive-blend draw operation

**Files:**
- Modify: `MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
- Create: `MeltdownMonitor.Mobile/Controls/AdditiveSkiaLayer.cs`

- [ ] **Step 1: Add managed SkiaSharp references**

In `Mobile.csproj`, add to the existing Avalonia `ItemGroup`:

```xml
<PackageReference Include="Avalonia.Skia" Version="12.0.4" />
<PackageReference Include="SkiaSharp" Version="3.119.4" />
```

(The iOS head already ships the native assets; these are the managed assemblies the custom draw op compiles against. Keep versions matched to Avalonia.Skia 12.0.4's managed SkiaSharp.)

- [ ] **Step 2: Implement the additive layer helper**

```csharp
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace MeltdownMonitor.Mobile.Controls;

/// <summary>
/// An <see cref="ICustomDrawOperation"/> that leases the Skia canvas and runs a caller
/// delegate with additive (<see cref="SKBlendMode.Plus"/>) compositing — the only way to
/// reproduce the desktop Regulation Field's glow bloom, which Avalonia's DrawingContext
/// cannot express. The delegate receives the canvas (already clipped to the op bounds)
/// and an SKPaint pre-set to additive; the caller sets colour/stroke per primitive.
/// </summary>
internal sealed class AdditiveSkiaLayer : ICustomDrawOperation
{
    private readonly Action<SKCanvas, SKPaint> _draw;

    public AdditiveSkiaLayer(Rect bounds, Action<SKCanvas, SKPaint> draw)
    {
        Bounds = bounds;
        _draw = draw;
    }

    public Rect Bounds { get; }

    public bool HitTest(Point p) => false;
    public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
    public void Dispose() { }

    public void Render(ImmediateDrawingContext context)
    {
        var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (lease is null) return;          // non-Skia backend (design-time): glow simply absent
        using var l = lease.Lease();
        var canvas = l.SkCanvas;
        using var paint = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus };
        _draw(canvas, paint);
    }
}
```

Verify against Avalonia 12.0.4: `ISkiaSharpApiLeaseFeature` / `ISkiaSharpApiLease.SkCanvas` and `ICustomDrawOperation.Render(ImmediateDrawingContext)`. If the lease member differs (e.g. `GetSkCanvas()`), adjust. A control pushes this with `context.Custom(new AdditiveSkiaLayer(bounds, (c, p) => { ... }))` inside its `Render`.

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: build succeeds (NuGet restores Avalonia.Skia + SkiaSharp).

- [ ] **Step 4: Commit**

```bash
git add MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj MeltdownMonitor.Mobile/Controls/AdditiveSkiaLayer.cs
git commit -m "feat(mobile): SkiaSharp refs + additive-blend custom draw operation"
```

---

### Task 11: Vagal-tone Y travel (marker, trail, Y-histogram)

**Files:**
- Create: `MeltdownMonitor.Core/Regulation/RegulationFieldGeometry.cs`
- Test: `MeltdownMonitor.Tests/RegulationFieldGeometryTests.cs`
- Modify: `MeltdownMonitor.Mobile/Controls/RegulationField.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

[TestClass]
public sealed class RegulationFieldGeometryTests
{
    [TestMethod]
    public void VagalToneOffsetY_baseline_sits_on_the_crossover()
        => Assert.AreEqual(0f, RegulationFieldGeometry.VagalToneOffsetY(0.5, markerYClamp: 100f), 1e-4f);

    [TestMethod]
    public void VagalToneOffsetY_fragile_lifts_to_top()
        => Assert.AreEqual(-100f, RegulationFieldGeometry.VagalToneOffsetY(0.0, 100f), 1e-4f);

    [TestMethod]
    public void VagalToneOffsetY_steady_drops_to_bottom()
        => Assert.AreEqual(100f, RegulationFieldGeometry.VagalToneOffsetY(1.0, 100f), 1e-4f);

    [TestMethod]
    public void VagalToneOffsetY_clamps_out_of_range_tone()
        => Assert.AreEqual(100f, RegulationFieldGeometry.VagalToneOffsetY(2.0, 100f), 1e-4f);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RegulationFieldGeometryTests"`
Expected: FAIL — `RegulationFieldGeometry` not defined.

- [ ] **Step 3: Implement the Core helper**

```csharp
namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// Pure pixel-space helpers shared by both heads' Regulation Field renderers, kept in
/// Core so they are unit-testable. The marker's vertical position encodes vagal tone:
/// FRAGILE (0) lifts to the top extent, STEADY (1) drops to the bottom, 0.5 rests on
/// the crossover. Ported verbatim from the desktop RegulationFieldView.VagalToneOffsetY.
/// </summary>
public static class RegulationFieldGeometry
{
    /// <summary>Vertical offset from the crossover for a tone in [0, 1]. <paramref name="markerYClamp"/>
    /// is the half-travel (pixels) from the crossover to each extent.</summary>
    public static float VagalToneOffsetY(double vagalTone, float markerYClamp)
        => Math.Clamp(((float)vagalTone - 0.5f) * 2f * markerYClamp, -markerYClamp, markerYClamp);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RegulationFieldGeometryTests"`
Expected: PASS.

- [ ] **Step 5: Apply the Y offset in the Mobile field**

In `RegulationField.cs`, introduce `markerYClamp = lobeHeight * MarkerYSpan` (add `const float MarkerYSpan = 0.92f;` matching the desktop) and apply `RegulationFieldGeometry.VagalToneOffsetY` to the Y of: the marker (`DrawMarker` — currently `MarkerPoint(MarkerPos)` only), each trail point (`DrawTrail`), and the Y-histogram bar rows (`DrawAxisHistograms`, Y axis) so rows line up with the marker's travel. Use `Reading.VagalTone` for the live marker and `trail[i].Reading.VagalTone` for trail points. Reference `App/Regulation/RegulationFieldView.cs` `MarkerScreenPos`, `DrawTrail` (lines ~466-480), and the Y-axis block (lines ~1053-1083) for the exact mapping.

- [ ] **Step 6: Build to verify it compiles**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: build succeeds.

- [ ] **Step 7: Commit**

```bash
git add MeltdownMonitor.Core/Regulation/RegulationFieldGeometry.cs MeltdownMonitor.Tests/RegulationFieldGeometryTests.cs MeltdownMonitor.Mobile/Controls/RegulationField.cs
git commit -m "feat(field): vagal-tone Y travel for marker, trail, and Y-histogram (mobile parity)"
```

---

### Task 12: Recent-RR buffer + texture playhead

**Files:**
- Create: `MeltdownMonitor.Core/Regulation/RrTexture.cs` (pure `BuildRrDeviations` + `RrTexturePlayhead`)
- Test: `MeltdownMonitor.Tests/RrTextureTests.cs`
- Modify: `MeltdownMonitor.Mobile/Controls/RegulationField.cs` (add a recent-RR buffer fed by a new `Rr` property / method)

- [ ] **Step 1: Write the failing test**

```csharp
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.Tests;

[TestClass]
public sealed class RrTextureTests
{
    [TestMethod]
    public void BuildRrDeviations_returns_empty_below_min_beats()
        => Assert.AreEqual(0, RrTexture.BuildRrDeviations([800, 810, 805]).Length);

    [TestMethod]
    public void BuildRrDeviations_normalises_diffs_into_minus1_to_1()
    {
        double[] rr = [800, 800, 800, 800, 800, 800, 800, 860]; // 8 beats; last diff +60ms
        float[] dev = RrTexture.BuildRrDeviations(rr);
        Assert.AreEqual(rr.Length, dev.Length);
        Assert.AreEqual(0f, dev[1], 1e-4f);                 // no change
        Assert.IsTrue(dev[^1] is > 0f and <= 1f);           // +60ms ⇒ clamped toward +1
    }

    [TestMethod]
    public void Playhead_advances_monotonically_with_frame_time()
    {
        var p = new RrTexturePlayhead();
        p.Advance(dt: 0.1f, beatsPerSecond: 1.0f, newestBeatIndex: 5);
        double a = p.Position;
        p.Advance(dt: 0.1f, beatsPerSecond: 1.0f, newestBeatIndex: 6);
        Assert.IsTrue(p.Position > a);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RrTextureTests"`
Expected: FAIL — `RrTexture`/`RrTexturePlayhead` not defined.

- [ ] **Step 3: Port the pure RR texture mechanism into Core**

Copy `BuildRrDeviations` (constants `MinRrForJitter = 8`, `RrDevScaleMs = 30f`) and the `RrTexturePlayhead` struct verbatim from `App/Regulation/RegulationFieldView.cs` (`BuildRrDeviations` at ~434-448; the `_playhead.Advance(...)` semantics — find the `RrTexturePlayhead` definition in the App project and move the pure logic here). Public surface:

```csharp
namespace MeltdownMonitor.Core.Regulation;

public static class RrTexture
{
    public const int MinRrForJitter = 8;
    public const float RrDevScaleMs = 30f;

    /// <summary>Normalised beat-to-beat differences in [-1, 1]; empty below MinRrForJitter beats.</summary>
    public static float[] BuildRrDeviations(IReadOnlyList<double> rr) { /* ported verbatim */ }
}

/// <summary>Free-running smooth scroll over the absolute beat timeline (ported from the
/// desktop). Advances by frame time at the real beat rate, gently corrected toward the
/// newest beat, so the RR texture flows even though BLE beats arrive in batches.</summary>
public struct RrTexturePlayhead
{
    public double Position { get; private set; }
    public void Advance(float dt, float beatsPerSecond, long newestBeatIndex) { /* ported verbatim */ }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj --filter "FullyQualifiedName~RrTextureTests"`
Expected: PASS.

- [ ] **Step 5: Feed a recent-RR buffer in the Mobile field**

Add to `RegulationField` a `double[] _rr` ring (capacity 160, matching desktop `RrBufferLength`) + `int _rrCount` + `long _beatsAppended`, and a public method the composition root / NowView calls on each beat:

```csharp
/// <summary>Push one non-artifact RR interval (ms) into the live-trace texture buffer.</summary>
public void PushBeat(double rrMs) { /* mirror RegulationFieldView.OnBeatReceived ring logic */ }
```

Drive the playhead in `OnFrame` (`_playhead.Advance(dt, Math.Max(40f, hr)/60f, _beatsAppended - 1)`). Wire `PushBeat` from the composition root by subscribing `pipeline.BeatReceived` and forwarding to the `RegulationField` instance on the Now view (or expose RR on `NowViewModel` and bind a new `Rr` property — choose whichever matches how `Trail` is already plumbed; `Trail` is a bound property on the control, so add an `Rr` `IReadOnlyList<double>` styled property fed by `NowViewModel` and have `NowViewModel.OnBeatReceived` maintain the buffer).

> Decision: prefer the **bound `Rr` property** path (consistent with `Trail`): add `OnBeatReceived` to `NowViewModel`, expose `RecentRr`, bind `Rr="{Binding RecentRr}"` in `NowView.axaml`, and subscribe `pipeline.BeatReceived += _now.OnBeatReceived` in the composition root. This keeps the control free of pipeline coupling.

- [ ] **Step 6: Build to verify it compiles**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: build succeeds.

- [ ] **Step 7: Commit**

```bash
git add MeltdownMonitor.Core/Regulation/RrTexture.cs MeltdownMonitor.Tests/RrTextureTests.cs MeltdownMonitor.Mobile/Controls/RegulationField.cs MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs MeltdownMonitor.Mobile/Views/NowView.axaml
git commit -m "feat(field): port RR texture deviations + playhead to Core; feed mobile RR buffer"
```

---

### Task 13: LF/HF balance halo (additive)

**Files:**
- Modify: `MeltdownMonitor.Mobile/Controls/RegulationField.cs`

- [ ] **Step 1: Add the halo via the additive layer**

Port `DrawLfHfHalo` (`RegulationFieldView.cs:241-269`): gated on `UseLfHfCorroboration` (read from a new `UseLfHfCorroboration` bool styled property bound from `NowViewModel`, sourced from `Pipeline.LatestThresholds.UseLfHfCorroboration`) and on `|Reading.LfHfBalance| ≥ 0.02`. Draw three concentric Peach/Sky discs through `AdditiveSkiaLayer` (`SKBlendMode.Plus`), biased by `LfHfBalance * halfWidth * 0.6` on X. Push it first (under everything) in `Render`:

```csharp
context.Custom(new AdditiveSkiaLayer(new Rect(Bounds.Size), (canvas, paint) =>
{
    // map Reading.LfHfBalance to colour (Peach/Sky) + centre; draw 3 discs with falloff alpha
}));
```

Convert Avalonia `Color` to `SKColor` with `new SKColor(c.R, c.G, c.B, (byte)(alpha*255))`. Use a Mobile-local `MacchiatoPalette` analogue or the existing `static readonly Color` fields.

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add MeltdownMonitor.Mobile/Controls/RegulationField.cs MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs MeltdownMonitor.Mobile/Views/NowView.axaml
git commit -m "feat(field): LF/HF balance halo (additive) on mobile"
```

---

### Task 14: Dwell density heatmap + peak crosshair + region box

**Files:**
- Modify: `MeltdownMonitor.Mobile/Controls/RegulationField.cs`

This reuses Core `RegulationFieldHistogram.FieldDensity` → `RegulationFieldDensity` (`PeakCount`, `Count(x,y)`, `PeakX/Y`, `HighDensityBounds(threshold)`) — no new math.

- [ ] **Step 1: Add the heatmap**

Add styled properties bound from settings: `HeatmapOpacity`, `HeatmapPeakOpacity`, `HeatmapRegionOpacity`, `HeatmapRegionThreshold`, `RegulationHeatmapLength`. Port `DrawDensityHeatmap` (`RegulationFieldView.cs:758-852`) + `HeatColor`/`HeatStops` (936-965): the trail input is the (already-bound) `Trail` sliced to `RegulationHeatmapLength`; cell extents use `halfWidth` × `markerYClamp` exactly as the desktop. Draw the magma cells through `AdditiveSkiaLayer` (additive); draw the dashed region box and peak crosshair via `DrawingContext` (alpha-over crisp chrome) reusing the existing `DrawDashedVertical`-style helpers (add dashed-rect + crosshair helpers mirroring `DrawHeatmapDensityRegion` / `DrawHeatmapPeakCrosshair`). Insert in `Render` after the window-of-tolerance / shutdown zone and before the trace.

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add MeltdownMonitor.Mobile/Controls/RegulationField.cs MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs MeltdownMonitor.Mobile/Views/NowView.axaml
git commit -m "feat(field): dwell heatmap + peak crosshair + region box on mobile"
```

---

### Task 15: RR-textured ribbon trace

**Files:**
- Modify: `MeltdownMonitor.Mobile/Controls/RegulationField.cs`

- [ ] **Step 1: Replace synthetic jitter with the real RR texture**

Port the live-trace section of `DrawLemniscate` (`RegulationFieldView.cs:292-391`): build the lemniscate polyline, compute `RrTexture.BuildRrDeviations(_rr)`, map the lobe once onto a window of the RR signal using the smooth `_playhead.Position` (Task 12), jitter each vertex along its normal, build a Catmull-Rom centreline, and stroke it. On mobile, render the ribbon through `AdditiveSkiaLayer` using an `SKPath` filled per the warm/cool depth gradient (or, if a tri-strip ribbon proves heavy, stroke the Catmull-Rom centreline as a variable-width `SKPath` with `SKBlendMode.Plus`). Scale alpha by `LobeOpacity`. This replaces the current `_animator.JitterOffset(...)` path in `DrawTrace`.

> Keep the ghost baseline lemniscate drawn alpha-over (it is chrome, not glow).

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add MeltdownMonitor.Mobile/Controls/RegulationField.cs
git commit -m "feat(field): RR-signal-textured ribbon trace on mobile"
```

---

### Task 16: Comet trail spline + additive marker halos + histogram opacity

**Files:**
- Modify: `MeltdownMonitor.Mobile/Controls/RegulationField.cs`

- [ ] **Step 1: Upgrade trail, marker halos, and histogram bars**

- Comet trail: replace the discrete-dot loop in `DrawTrail` with a Catmull-Rom spline (port `RegulationFieldView.cs:482-519`), drawn through `AdditiveSkiaLayer`, alpha scaled by `TrailOpacity`, width thickening toward the head.
- Marker halos: draw the two surrounding halos (state pulse + collapse) through `AdditiveSkiaLayer`; keep the solid core + pupil alpha-over (port `DrawMarker` 682-706).
- Axis histograms: route the bar fills through `AdditiveSkiaLayer` and scale alpha by `HistogramOpacity` (port the additive brackets in `DrawAxisHistograms` 982-1085); keep axis baselines alpha-over.
- Add the `drawScale = max(1, halfWidth / 240f)` factor and apply it to indicator pixel sizes/strokes as the desktop does.

- [ ] **Step 2: Build + full test run**

Run:
```
dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj
dotnet test  MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj
```
Expected: build succeeds; all tests pass.

- [ ] **Step 3: Commit**

```bash
git add MeltdownMonitor.Mobile/Controls/RegulationField.cs
git commit -m "feat(field): spline comet trail, additive marker halos, histogram opacity"
```

---

## Phase C — Settings UI parity

### Task 17: `SettingsViewModel` new properties

**Files:**
- Modify: `MeltdownMonitor.Mobile/ViewModels/SettingsViewModel.cs`

- [ ] **Step 1: Add bound properties**

Following the existing `SettingsViewModel` pattern (each property reads/writes `_settings.X`, raises change, and persists via the store like the current knobs), add:
`LobeOpacityPercent`, `TrailOpacityPercent`, `HeatmapOpacityPercent`, `HeatmapPeakOpacityPercent`, `HeatmapRegionOpacityPercent`, `HeatmapRegionThresholdPercent`, `HistogramOpacityPercent` (each a `double` in 0–100 mapping to the 0–1 settings field), `RegulationHeatmapLength` (int), `HrvEmitIntervalSeconds` (double), `SparklineWindowMinutes` (int), plus the detection knobs `RmssdAlertingDropPercent`, `WarningHoldSeconds`, `AlertingEscalationSeconds`, `CooldownMinutes` (these last four read/write `_settings.Thresholds` with `record with` updates, mirroring how the existing `RmssdWarningDropPercent` / `HrWarningRisePercent` already do it — read the current file to copy the exact idiom).

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add MeltdownMonitor.Mobile/ViewModels/SettingsViewModel.cs
git commit -m "feat(mobile): settings view model knobs for regulation/heatmap/detection parity"
```

---

### Task 18: `SettingsView` sliders

**Files:**
- Modify: `MeltdownMonitor.Mobile/Views/SettingsView.axaml`

- [ ] **Step 1: Add slider rows**

In the existing "Regulation field" section, add sliders (same label/value/`Slider`/help triplet the file already uses) for: Lobe opacity (0–100), Trail opacity (0–100), Histogram opacity (0–100), Heatmap opacity (0–100), Heatmap peak (0–100), Dwell region (0–100), Region threshold (0–100), Heatmap window (`RegulationHeatmapLength`, 60–17280). Add a new "Refresh" section for HRV emit interval (0.5–30 s) and History window (`SparklineWindowMinutes`, 1–360 min). In "Sensitivity", add RMSSD alert drop (5–95%), Warning hold (5–300 s), Escalation (s), Cooldown (min). Bind each to the Task 17 property.

- [ ] **Step 2: Build to verify it compiles (compiled bindings)**

Run: `dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add MeltdownMonitor.Mobile/Views/SettingsView.axaml
git commit -m "feat(mobile): settings sliders for regulation/heatmap/refresh/detection knobs"
```

---

### Task 19: Bind new field settings into the Now view + providers

**Files:**
- Modify: `MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs`
- Modify: `MeltdownMonitor.Mobile/Views/NowView.axaml`
- Modify: `MeltdownMonitor.iOS/IosCompositionRoot.cs`

- [ ] **Step 1: Expose + bind the new field knobs**

Add provider parameters to `NowViewModel` (mirroring the existing `trailLengthProvider` etc.) for `LobeOpacity`, `TrailOpacity`, `HeatmapOpacity`, `HeatmapPeakOpacity`, `HeatmapRegionOpacity`, `HeatmapRegionThreshold`, `HistogramOpacity`, `RegulationHeatmapLength`, and `UseLfHfCorroboration` (from `pipeline.LatestThresholds`). Expose them as VM properties, bind them onto the `RegulationField` styled properties added in Tasks 13–16 in `NowView.axaml`, and pass the providers from `IosCompositionRoot` (where `NowViewModel` is constructed, line ~117).

- [ ] **Step 2: Build + full test run**

Run:
```
dotnet build MeltdownMonitor.Mobile/MeltdownMonitor.Mobile.csproj
dotnet test  MeltdownMonitor.Tests/MeltdownMonitor.Tests.csproj
```
Expected: build succeeds; all tests pass. (Re-read `IosCompositionRoot.cs` to confirm the iOS-only edits are consistent; it compiles on macOS/CI.)

- [ ] **Step 3: Commit**

```bash
git add MeltdownMonitor.Mobile/ViewModels/NowViewModel.cs MeltdownMonitor.Mobile/Views/NowView.axaml MeltdownMonitor.iOS/IosCompositionRoot.cs
git commit -m "feat(mobile): plumb new regulation-field knobs into the Now view and composition root"
```

---

## Phase D — Docs

### Task 20: Update project docs

**Files:**
- Modify: `CLAUDE.md` (Gotchas / Layout), `README.md` if it enumerates per-head features.

- [ ] **Step 1: Note the new Mobile capabilities and Skia dependency**

Add to `CLAUDE.md`: the Mobile head now renders the full metric chart suite (Metrics tab) and the Regulation Field glow via a SkiaSharp `ICustomDrawOperation` (`Avalonia.Skia` + `SkiaSharp` are now direct Mobile deps; glow/playhead still device-only-verifiable). Note `Pipeline.BeatReceived` now exists on the Mobile pipeline too.

- [ ] **Step 2: Commit**

```bash
git add CLAUDE.md README.md
git commit -m "docs: record mobile metrics tab + field-glow Skia dependency"
```

---

## Self-review

**Spec coverage:**
- Metrics tab (full set, scrolling, hand-rolled) → Tasks 3–9. ✓
- Regulation Field parity: LF/HF halo (13), dwell heatmap+crosshair+region (14), RR-textured trace (12+15), vagal-tone Y travel (11), additive glow (10 + used in 13–16), comet spline / marker halos / drawScale (16). ✓
- Settings parity → Tasks 2, 17, 18, 19. ✓
- Pipeline `BeatReceived` → Task 1. ✓
- Testing (chart mapping, MetricsVM, pipeline event, settings round-trip, vagal offset, RR texture) → Tasks 1,2,3,5,6,7,11,12. ✓
- Definition of done / device-deferred visuals → stated in header and Phase B preamble. ✓

**Placeholder scan:** Render-body ports (Tasks 13–16, 19's bindings) intentionally reference the authoritative desktop source by file+line range rather than reproducing ~1000 lines of rendering verbatim — the engineer ports against in-repo code. All *new mechanisms* (additive draw op, ChartScale, MetricChart, ScatterChart, MetricsViewModel, RrTexture, RegulationFieldGeometry, settings) have complete code. No TBD/TODO left.

**Type consistency:** `ChartScale.{FitRange,Y,TimeX}`, `ScatterSeries.ConsecutivePairs`, `RegulationFieldGeometry.VagalToneOffsetY`, `RrTexture.BuildRrDeviations` + `RrTexturePlayhead.{Position,Advance}`, `MetricsViewModel.{OnSampleUpdated,OnBeatReceived,OnBatteryUpdated,Backfill,LoadFromRepositoryAsync,WindowSeconds}` and its series property names match between definitions (Tasks 3,5,6,7,11,12) and consumers (Tasks 8,9,15). `Pipeline.BeatReceived` consistent across Tasks 1, 9, 12. `RootViewModel` new `metrics` parameter consistent across Task 9's three edits.

**Open verification points flagged for the implementer:** exact `Beat` record signature (Task 6), `RunOnUi`/`ViewModelBase` helper location (Task 6), `MeltdownRepository.ReadHistory/ReadBatteryHistory` signatures (Task 7), Avalonia 12 `ISkiaSharpApiLease` member names (Task 10), the composition root's database-path accessor (Task 9), and `SettingsViewModel`'s existing threshold-knob idiom (Task 17).
