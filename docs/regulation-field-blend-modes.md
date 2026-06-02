# Regulation field: additive blend mode (investigation)

**Status:** not implemented — blocked on an upstream renderer change.
**Date:** 2026-06-02.
**Scope of finding:** Desktop (ImGui) head only. Mobile (Avalonia) was not investigated in depth.

## What was asked

Render the regulation field (or its glow layers — LF/HF halo, comet trail, marker
halo) with an **additive blend mode** so overlapping translucent shapes brighten
toward white instead of compositing with normal alpha-over, giving a neon/glow look.

## Why true GPU additive is currently not achievable on Desktop

The Desktop head draws everything through Hexa.NET `ImDrawListPtr` primitives
(`AddCircleFilled`, `AddLine`, …) in `MeltdownMonitor.App/Regulation/RegulationFieldView.cs`.
The ImGui draw list is rendered by the OpenGL backend inside the **`ktsu.ImGui.App`**
NuGet package (`ktsu.ImGui.App.ImGuiController.ImGuiController.RenderDrawData`,
backed by Silk.NET.OpenGL). Three facts block the standard approach:

1. **Draw-command callbacks throw.** The canonical ImGui technique for a per-region
   blend change is `ImDrawList.AddCallback(...)`: you insert a callback that flips
   `glBlendFunc` to additive (`GL_SRC_ALPHA, GL_ONE`) before the glow primitives and
   reset it after (via the `ImDrawCallback_ResetRenderState` sentinel). But the
   `ktsu.ImGui.App` renderer does:

   ```csharp
   ImDrawCmd cmd = cmdList.CmdBuffer[j];
   if (cmd.UserCallback != null)
   {
       throw new NotImplementedException();   // any callback crashes the renderer
   }
   ```

   So `AddCallback` would crash the app rather than blend.

2. **Blend state is global per frame.** `SetupRenderState` sets the blend func once
   for the whole ImGui pass:

   ```csharp
   _gl.BlendEquation(GL_FUNC_ADD);
   _gl.BlendFuncSeparate(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
   ```

   Switching this to additive would affect *all* UI (text, panels, every other
   window), not just the field.

3. **No GL escape hatch is exposed.** `ImGuiApp` keeps its `GL` instance `internal`
   and offers no public post-render hook, so the field can't be re-drawn in a
   separate raw-OpenGL additive pass after the ImGui frame either.

## What it would take to do it properly

Teach the `ktsu.ImGui.App` OpenGL renderer to honor draw-command callbacks, the way
the stock `imgui_impl_opengl3` backend does:

- In `RenderDrawData`, when `cmd.UserCallback != null`, invoke the callback instead
  of throwing — and special-case the `ImDrawCallback_ResetRenderState` sentinel to
  re-run `SetupRenderState`.

With that in place, `RegulationFieldView` can wrap its glow layers:

```text
draw.AddCallback(setAdditiveBlend, null);   // glBlendFunc(SRC_ALPHA, ONE)
//  ... DrawLfHfHalo / DrawTrail / DrawMarker halo ...
draw.AddCallback(ImDrawList ResetRenderState sentinel, null);  // back to alpha-over
```

That work lives in a **different repository** (`ktsu.ImGui.App`), out of scope for
the MeltdownMonitor session where this was investigated.

## Viable in-repo alternative (not yet done)

A **software-additive approximation** stays entirely within MeltdownMonitor: for each
glow color, pre-compute its additive/screen composite against the known background
color and emit the result as a normal alpha-over primitive (the same `WithAlpha`
path used today). Overlaps against the background brighten correctly; glow-on-glow
overlaps are approximate but read fine for halos, trails, and the marker on the dark
Catppuccin base. This was offered as a fallback but not implemented.
