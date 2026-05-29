# MeltdownMonitor — Branding Assets

SVG masters and derived raster icons for MeltdownMonitor, implementing
[`docs/superpowers/specs/2026-05-29-icon-set-and-branding-design.md`](../../docs/superpowers/specs/2026-05-29-icon-set-and-branding-design.md).

The organising form is the **Regulation Lemniscate** — a figure-8 drawn as a
living HRV trace. Cool lobe = parasympathetic (rest), warm lobe = sympathetic
(arousal), Lavender crossover = the balance point / window of tolerance. Built
entirely from the **Catppuccin Macchiato** palette, tuned to read
neurodivergent-affirming *and* biohacking while staying calm and non-jarring.

## SVG masters (authoritative — edit these)

| File | Purpose | Rung |
|---|---|---|
| `mark-regulation-lemniscate.svg` | Core mark, transparent background | full |
| `app-icon.svg` | App icon on Macchiato rounded-square field | full |
| `mark-monochrome.svg` | One-color mark (watermarks, stamps) | full |
| `favicon.svg` | Minimal-glyph favicon with Base field | minimal |
| `tray-idle/watching/warning/alerting/cooldown.svg` | State-driven tray glyphs | minimal |
| `annotation-calm/activated/overwhelmed/recovering.svg` | Annotation-state icons | simplified |
| `wordmark-horizontal.svg` | Mark + wordmark, horizontal lockup | — |
| `wordmark-stacked.svg` | Mark + wordmark, stacked lockup | — |
| `regulation-field.svg` | Signature visualization, representative still frame | full |
| `og-image.svg` | Open Graph / social preview (1200×630) | — |

## Derived rasters (`icons/` — regenerate, don't hand-edit)

`app.ico` + `app-icon-{16…256}.png`, `favicon.ico` + `favicon-{16,32}.png`,
`apple-touch-icon.png` (180), `tray-{state}-{16,32}.png`, `mark-{256,512}.png`.

## Regenerating rasters

Requires ImageMagick (`magick`). Run from this directory:

```powershell
# app icon → multi-size PNG + .ico
foreach ($s in 16,24,32,48,64,128,256) { magick -background none app-icon.svg -resize "${s}x${s}" "icons/app-icon-$s.png" }
magick -background none app-icon.svg -define icon:auto-resize=16,24,32,48,64,128,256 icons/app.ico

# favicon + apple-touch
magick -background none favicon.svg -define icon:auto-resize=16,32,48 icons/favicon.ico
magick -background none favicon.svg -resize 180x180 icons/apple-touch-icon.png

# tray state glyphs
foreach ($state in 'idle','watching','warning','alerting','cooldown') {
  foreach ($s in 16,32) { magick -background none "tray-$state.svg" -resize "${s}x${s}" "icons/tray-$state-$s.png" }
}
```

**Text assets** (`wordmark-*`, `og-image`, `regulation-field`) reference **Inter**
(wordmark) and **JetBrains Mono** (numerics). If those fonts aren't installed,
`magick` substitutes a fallback — install the fonts, or render with a browser /
Inkscape, before exporting production rasters of the text assets.

## State colour mapping

| State | Macchiato | Hex |
|---|---|---|
| Idle | Overlay1 | `#8087a2` |
| Watching | Green | `#a6da95` |
| Warning | Peach | `#f5a97f` |
| Alerting | Red (soft pastel) | `#ed8796` |
| Cooldown | Sapphire | `#7dc4e4` |

Cool lobe `#7dc4e4`→`#91d7e3` · warm lobe `#f5a97f`→`#ee99a0` · crossover
`#b7bdf8` · canvas Base `#24273a` / Mantle `#1e2030` / Crust `#181926`.

## Notes for implementation

- The tray glyphs replace the solid-colour-square `BuildIcon` in
  `MeltdownMonitor.App/TrayIcon.cs`; map `DetectorState` → the matching
  `tray-*` raster (or render the SVG directly).
- On light taskbars the pastels are intentionally lower-contrast (sensory-safe
  mandate). If more separation is needed, add a 1px Crust/Mantle halo to the
  tray glyph — do **not** raise saturation into alarm territory.
- The `regulation-field.svg` is a *still frame* of a live, animated instrument;
  the SVG documents the visual grammar, not the motion.
