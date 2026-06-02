# Regulation field: additive blend mode

**Status:** unblocked upstream — the renderer change has been made in `ktsu.ImGui.App`.
Wiring it into the regulation field is pending a published package bump.
**Date:** 2026-06-02 (investigated), updated after the upstream fix landed.
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

## How to wire it into the regulation field

Once MeltdownMonitor takes a `ktsu.ImGui.App` version that includes the API above
(currently referenced at `2.6.0` in `MeltdownMonitor.App.csproj` — needs the next
published release), wrap the glow layers in `RegulationFieldView`:

```csharp
ImGuiApp.SetDrawBlendMode(draw, ImGuiAppBlendMode.Additive);
//  ... DrawLfHfHalo / DrawTrail / DrawMarker halo ...
ImGuiApp.SetDrawBlendMode(draw, ImGuiAppBlendMode.AlphaBlend); // restore for the rest of the frame
```

The blend func is global GL state for the remainder of the draw pass, so the glow
region must always be closed with an `AlphaBlend` (or `ImDrawCallback_ResetRenderState`)
before the rest of the UI draws. Build it from the live app + a real Polar sensor —
the glow look can't be confirmed from tests alone.

## In-repo alternative (no longer required, kept for reference)

A **software-additive approximation** stays entirely within MeltdownMonitor: for each
glow color, pre-compute its additive/screen composite against the known background
color and emit the result as a normal alpha-over primitive (the same `WithAlpha`
path used today). Overlaps against the background brighten correctly; glow-on-glow
overlaps are approximate but read fine for halos, trails, and the marker on the dark
Catppuccin base. With true GPU additive now available upstream, this fallback is no
longer needed.
