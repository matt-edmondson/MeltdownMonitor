# Regulation field: additive blend mode

**Status:** done — additive glow is wired into the regulation field on the Desktop head,
using the `ktsu.ImGui.App` 2.9.0 renderer change.
**Date:** 2026-06-02 (investigated, then unblocked upstream and wired).
**Scope:** Desktop (ImGui) head only. Mobile (Avalonia) was not investigated in depth.

## What was asked

Render the regulation field (or its glow layers — LF/HF halo, comet trail, marker
halo) with an **additive blend mode** so overlapping translucent shapes brighten
toward white instead of compositing with normal alpha-over, giving a neon/glow look.

## Why true GPU additive used to be unreachable on Desktop

The Desktop head draws everything through Hexa.NET `ImDrawListPtr` primitives
(`AddCircleFilled`, `AddLine`, …) in `MeltdownMonitor.App/Regulation/RegulationFieldView.cs`.
The ImGui draw list is rendered by the OpenGL backend inside the **`ktsu.ImGui.App`**
NuGet package (`ktsu.ImGui.App.ImGuiController.ImGuiController.RenderDrawData`,
backed by Silk.NET.OpenGL). The canonical ImGui technique for a per-region blend
change is `ImDrawList.AddCallback(...)`: insert a callback that flips `glBlendFunc`
to additive (`GL_SRC_ALPHA, GL_ONE`) before the glow primitives and reset it after
(via the `ImDrawCallback_ResetRenderState` sentinel). That used to be blocked:

1. **Draw-command callbacks threw.** The renderer did
   `if (cmd.UserCallback != null) throw new NotImplementedException();`, so any
   `AddCallback` crashed the app rather than blending.
2. **Blend state was global per frame.** `SetupRenderState` set the blend func once
   for the whole ImGui pass, so flipping it would have affected all UI.
3. **No GL escape hatch was exposed.** `ImGuiApp` keeps its `GL` instance `internal`
   and offered no public hook, so the field couldn't be re-drawn in a separate
   raw-OpenGL additive pass either.

## What changed upstream (`ktsu.ImGui.App`)

The OpenGL renderer now **honors draw-command callbacks**, the way the stock
`imgui_impl_opengl3` backend does:

- In `RenderDrawData`, a `null` `UserCallback` still draws normally; the
  `ImDrawCallback_ResetRenderState` sentinel re-runs `SetupRenderState`; any other
  callback is invoked with its draw list and command instead of throwing.

On top of that, `ImGuiApp` exposes a **GL-free public API** so consumers never need
the (still `internal`) `GL` instance:

```csharp
public enum ImGuiAppBlendMode { AlphaBlend, Additive }

// Records a draw-list callback that switches the renderer's blend func for the
// primitives that follow, until the next SetDrawBlendMode call.
public static void ImGuiApp.SetDrawBlendMode(ImDrawListPtr drawList, ImGuiAppBlendMode mode);
```

`Additive` maps to `glBlendFuncSeparate(SRC_ALPHA, ONE, ONE, ONE)`; `AlphaBlend`
restores the default alpha-over func from `SetupRenderState`.

## How it's wired in the regulation field

`MeltdownMonitor.App.csproj` references `ktsu.ImGui.App` (and `ktsu.ImGui.Widgets`)
`2.9.0`, the release that includes the API. `RegulationFieldView` brackets each glow
layer between an `Additive` and an `AlphaBlend` call on the window draw list:

```csharp
ImGuiApp.SetDrawBlendMode(draw, ImGuiAppBlendMode.Additive);
//  ... glow primitives ...
ImGuiApp.SetDrawBlendMode(draw, ImGuiAppBlendMode.AlphaBlend); // restore for the rest of the frame
```

The layers drawn additively are:

- **LF/HF halo** — the three concentric falloff discs accumulate into a real radial glow.
- **Density heatmap** — each magma cell adds its light to the dark canvas instead of tiling flat.
- **Lemniscate lobes** — the live two-tone trace is stroked as a **single tri-strip ribbon**
  (`LemniscateGeometry.StrokeClosed` builds miter-joined left/right boundaries from the
  Catmull-Rom centreline; `RegulationFieldView.FillRibbon` submits it as one indexed triangle
  batch via the `ImDrawListPtr` `PrimReserve`/`PrimWriteVtx`/`PrimWriteIdx` API). This replaced
  the earlier per-segment `AddLine` + round-join `AddCircleFilled` stroking, whose overlapping
  segments and joins **overdrew** every shared pixel and bloomed toward white under additive.
  The ribbon rasterises each pixel once, so additive now composites the trace cleanly onto the
  dark field and the layers beneath — only the figure-8's own self-crossing at the centre still
  overlaps, by design. Per-vertex colour still gives the warm/cool gradient and the lobes still
  brighten where they meet at the crossover; the lobe alpha is scaled by a user-configurable
  **Lobe opacity** knob (`AppSettings.LobeOpacity`, default 0.6) exposed alongside Lobe thickness.
  Trade-off: raw `Prim*` triangles carry **no anti-aliasing fringe**, so the ribbon's outer edge
  is crisper (slightly harder) than the old AA'd `AddLine` stroke — only confirmable on the live
  app + a real Polar sensor.
- **Comet trail** — overlapping sub-segments and the head-meets-marker join bloom. The
  per-segment alpha is scaled by a user-configurable **Trail opacity** knob
  (`AppSettings.TrailOpacity`, default 0.7) to compensate.
- **Axis histograms** — the arousal (X) and vagal-tone (Y) bars add their light to the canvas
  and the heatmap beneath. The bar alpha is scaled by a user-configurable **Histogram opacity**
  knob (`AppSettings.HistogramOpacity`, default 0.6); the thin axis baseline lines stay alpha-over
  as reference chrome.
- **Marker halos** — the pulsing state halo and the collapse halo glow; the solid marker
  core and inner dot stay alpha-over so they read as crisp, opaque points.

The blend func is global GL state for the remainder of the draw pass, so every glow region
is closed with an `AlphaBlend` before the next layer draws. The faint ghost baseline, window
of tolerance, vagal axis, histogram baseline lines, recovery target, arrow, and labels stay
alpha-over. The glow look can only be confirmed from the live app + a real Polar sensor, not
from tests.

## In-repo alternative (no longer required, kept for reference)

A **software-additive approximation** stays entirely within MeltdownMonitor: for each
glow color, pre-compute its additive/screen composite against the known background
color and emit the result as a normal alpha-over primitive (the same `WithAlpha`
path used today). Overlaps against the background brighten correctly; glow-on-glow
overlaps are approximate but read fine for halos, trails, and the marker on the dark
Catppuccin base. With true GPU additive now available upstream, this fallback is no
longer needed.
