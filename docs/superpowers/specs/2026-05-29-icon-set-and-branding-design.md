# MeltdownMonitor — Icon Set & Branding Design

Status: **Draft / Pre-implementation**
Date: 2026-05-29
Scope: A complete icon and branding system for MeltdownMonitor (Windows desktop +
iOS), built on the **Catppuccin Macchiato** palette, designed to read
**neurodivergent-affirming ("autistic")** and **biohacking** at the same time,
while honouring the app's core sensory-safety constraint: *calm, non-jarring,
never alarming*.

---

## 1. Design thesis

MeltdownMonitor watches the **autonomic nervous system** for the shift between
the sympathetic ("fight/flight/freeze", high-arousal) and parasympathetic
("rest/digest", low-arousal) branches, measured against *your own* HRV baseline,
and gives a quiet heads-up *before* you consciously notice dysregulation.

The brand expresses that thesis through a single organising form — **a
lemniscate (∞) drawn as a living HRV trace** — that does triple duty:

1. **Reads autistic.** The infinity loop is the neurodiversity / autistic-pride
   symbol. It is deliberately **not** the puzzle piece, which the autistic
   community broadly rejects.
2. **Reads biohacking.** Rendered as a precise, telemetry-style heart-rate-
   variability trace (not a soft decorative curve), on a dark dashboard palette.
   Quantified-self, sensor-data energy.
3. **Means something.** The lemniscate's two lobes map onto the two branches of
   the autonomic nervous system. The figure-8 is also the **product's signature
   data instrument** at rest (see §7) — so the logo is literally a frozen frame
   of the app's core view.

**Non-negotiable constraint:** the audience is people with PTSD / C-PTSD /
autism and nervous-system dysregulation. Every asset must be low-arousal. No
alarm-red, no high-contrast flashing, no jarring motion. Even the most intense
state is rendered in a soft pastel.

---

## 2. The mark — "the Regulation Lemniscate"

A figure-8 drawn as a **single continuous HRV trace**. Two lobes that meet at a
crossover node:

- **Cool lobe** — parasympathetic / rest. Sky → Sapphire (`#91d7e3` → `#7dc4e4`).
- **Warm lobe** — sympathetic / arousal. Peach → Maroon (`#f5a97f` → `#ee99a0`).
- **Crossover node** — a small bright pulse point in **Lavender** (`#b7bdf8`):
  the *balance point*, the centre of the window of tolerance.

The line is **not** a smooth mathematical lemniscate. It carries subtle
**variability jitter** — micro-peaks like a real RR-interval trace — so it reads
as *living data*, not a generic infinity logo. In a regulated state both lobes
are full and symmetric. The state system (§5) expresses dysregulation by letting
one lobe swell while the other thins.

### Geometry rules

- Two-lobe lemniscate (Bernoulli-style figure-8), horizontal long axis.
- The two lobes' fatness/length are derived from a default **SD1/SD2 ratio**
  (the Poincaré aspect ratio), so the geometry is honest to the data model even
  in the static logo. Default static ratio ≈ a relaxed, fat figure-8.
- Stroke carries jitter only at full detail; the simplification ladder (§6)
  removes it at small sizes.
- Crossover node sits at the geometric centre; its glow radius scales with size.

---

## 3. Palette — Catppuccin Macchiato

All assets are built from the Catppuccin Macchiato palette. Darks form the
base (calm dark-mode dashboard register, gentle on sensory load by default);
the accent spectrum carries meaning.

| Role | Name | Hex |
|---|---|---|
| App / canvas base | Base | `#24273a` |
| Recessed surface | Mantle | `#1e2030` |
| Deepest / OG bg | Crust | `#181926` |
| Raised surface | Surface0 | `#363a4f` |
| Raised surface | Surface1 | `#494d64` |
| Raised surface | Surface2 | `#5b6078` |
| Idle / muted | Overlay0 | `#6e738d` |
| **Idle state** | Overlay1 | `#8087a2` |
| Muted detail | Overlay2 | `#939ab7` |
| Secondary text | Subtext0 | `#a5adcb` |
| Wordmark "Monitor" | Subtext1 | `#b8c0e0` |
| Primary text / wordmark "Meltdown" | Text | `#cad3f5` |
| **Crossover / balance node** | Lavender | `#b7bdf8` |
| Cool lobe (mid) | Blue | `#8aadf4` |
| **Cooldown state** | Sapphire | `#7dc4e4` |
| **Cool lobe (parasympathetic)** | Sky | `#91d7e3` |
| Cool accent | Teal | `#8bd5ca` |
| **Watching state** | Green | `#a6da95` |
| Reserved | Yellow | `#eed49f` |
| **Warning state / warm lobe (sympathetic)** | Peach | `#f5a97f` |
| **Warm lobe (high-arousal)** | Maroon | `#ee99a0` |
| **Alerting state** | Red | `#ed8796` |
| Reserved accent | Pink | `#f5bde6` |
| Reserved accent | Mauve | `#c6a0f6` |
| Reserved accent | Flamingo | `#f0c6c6` |
| Reserved accent | Rosewater | `#f4dbd6` |

**Light-surface note:** for the website's light-mode lockups, the mark keeps its
accent hues but the canvas becomes a near-white derived from Rosewater/Text at
low saturation; one-color marks use Crust on light.

---

## 4. Typography

Hybrid system (humanist for voice, monospace for data):

- **Wordmark & headings — humanist sans** (Inter or Outfit). Calm, accessible,
  low-anxiety reading. The wordmark "**MeltdownMonitor**" sets "Meltdown" in
  **Text** weight and "Monitor" in **Subtext1**, a subtle two-tone that echoes
  the two lobes.
- **Data, labels, numerics — monospace** (JetBrains Mono, in keeping with the
  Catppuccin dev-tool lineage). Every HR / RMSSD / SDNN readout, axis label, and
  UI numeric uses mono. This is where the biohacking voice lives — and it is
  most of the actual UI surface.

System rule: *if it's a number the app measured, it's monospace; if it's a word
the app says to you, it's humanist.*

---

## 5. State color system

Replaces the current hard-coded `Color.Gray/Green/Orange/Red/DodgerBlue` in
`MeltdownMonitor.App/TrayIcon.cs` with Macchiato equivalents. Drives the tray
icon, the iOS alert tint, and the status window accents.

| Detector state | Current | Macchiato | Hex | Mark behaviour |
|---|---|---|---|---|
| Idle | Gray | Overlay1 | `#8087a2` | Dim; both lobes faint |
| Watching | Green | Green | `#a6da95` | Balanced; gently breathing |
| Warning | Orange | Peach | `#f5a97f` | Warm lobe begins to swell |
| Alerting | Red | Red | `#ed8796` | Warm lobe dominant — but soft pastel, never an alarm |
| Cooldown | DodgerBlue | Sapphire | `#7dc4e4` | Cool lobe full; settling back toward symmetry |

**Sensory-safety win:** even "Alerting" is the soft pastel `#ed8796`, so the most
intense state still reads as a gentle nudge rather than an alarm.

---

## 6. Production approach & simplification ladder

**SVG masters → derived assets.** Every icon is authored once as a layered
vector master. All raster outputs (`.ico`, `.icns` / `.appiconset`, PNG,
favicon, OG image) are derived from those masters at fixed sizes. This is the
only approach that survives the 16px-tray → 1024px-app-store range without
redrawing per size.

**Simplification ladder** (applied by size, top → bottom as size shrinks):

1. **Full detail** (≥128px): two-tone lobes, gradient, variability jitter on the
   stroke, Lavender crossover glow, optional ghost-baseline underlay.
2. **Simplified** (32–64px): drop jitter; keep gradient lobes + crossover node.
3. **Minimal glyph** (≤24px, incl. 16px tray/favicon): flat two-tone figure-8
   on a pixel-tuned grid; no gradient, no glow; state carried by **fill color +
   which lobe is brighter**.

Each deliverable below names which rung(s) it uses.

---

## 7. Signature visualization — "the Regulation Field"

The flagship deliverable and the app's main unique insight. **The lemniscate
becomes a live instrument; the logo is its idealised resting frame.** Most HRV
apps show a line chart of RMSSD; this instead shows **autonomic balance as a
position within your window of tolerance, with predictive drift** — which is the
app's whole reason to exist.

### Conceptual frame: window of tolerance

Both lobes are *poles of dysregulation*; the centre is regulation:

- **Cool lobe (far)** — parasympathetic extreme → **shutdown** / freeze
  (low-arousal dysregulation).
- **Warm lobe (far)** — sympathetic extreme → **meltdown** (high-arousal
  dysregulation).
- **Crossover centre** — the **window of tolerance**, the regulated zone.

This is language the trauma / autistic community already uses, so the instrument
is meaningful on sight to its actual users.

### State → encoding contract

Every piece of `MeltdownMonitor.Core` state maps into one view:

| Core state | Encoded as |
|---|---|
| Balance (RMSSD↓ + HR↑ vs baseline) | **Position of the live marker** along the figure-8 — the headline reading |
| HRV amount (RMSSD / SDNN) | **Stroke character** — fat, jittery, "alive" line = healthy variability; thin, smooth, metronomic = collapse |
| Poincaré SD1 / SD2 | **Lobe geometry** — ellipse axes set each lobe's fatness/length; the Poincaré plot *is* the lemniscate |
| EWMA baseline | **Ghost lemniscate** underlay (regulated-you); current vs baseline is a direct overlay. Shows a **lock glyph** when the baseline is frozen during Warning/Alerting |
| Trajectory (last few minutes) | **Comet trail** behind the marker — direction + speed = the early-warning signal |
| Detector state | **Color** of marker / active lobe (the §5 palette) |
| Mean HR | **Breathing cadence** of the glow + a monospace numeric readout |
| Annotations (Calm / Activated / Overwhelmed / Recovering) | **Dots on the trail** where the user logged a feeling |
| LF/HF (when corroboration enabled) | Subtle **halo asymmetry** |

### Payoff

One glance answers *"am I in my window, which way am I drifting, and how fast."*
The **app icon, tray glyph, and annotation icons are all reduced/frozen states
of this same instrument**, so the entire identity is one coherent system.

### Degradation & integration

- **Full Regulation Field** (status window / iOS primary surface): all encodings
  above, animated at the breathing cadence, ≥256px.
- **Reduced** (medium contexts): marker + lobes + state color, no trail/ghost.
- **Frozen** (logo, app icon, splash): idealised symmetric resting frame.
- **Status-window reconciliation:** the existing ImPlot RMSSD-vs-baseline
  sparklines (`MeltdownMonitor.App/StatusWindow.cs`) remain as the detailed
  time-series read; the Regulation Field becomes the *hero* element above them —
  the at-a-glance gauge, with the sparklines as the drill-down. The spec defines
  the Field's visual grammar; implementation in ImGui/ImPlot (Windows) and
  Skia/Avalonia (iOS) is left to the implementation plan.

---

## 8. Deliverables catalog

### 8.1 App icon — Windows `.ico` + iOS `.appiconset`
- Full-detail lemniscate (frozen resting frame) on a Macchiato-Base rounded
  square, soft Lavender glow at the crossover.
- Windows `.ico`: 16 / 24 / 32 / 48 / 64 / 128 / 256 px.
- iOS: 1024px master → standard appiconset derivations.
- Ladder: full ≥128, simplified 32–64, minimal ≤24.

### 8.2 Tray icon — Windows, state-driven
- 16px primary + 20 / 24 / 32 px DPI variants. **Minimal glyph** rung.
- 5 state colorways (§5). Replaces the solid-color-square `BuildIcon` in
  `TrayIcon.cs` with rendered glyphs.
- 16px fallback: warm-lobe-swell isn't legible that small, so state is carried
  by **fill color + which lobe is brighter**.

### 8.3 Website logo mark + lockups
- **Primary mark** (full lemniscate), **horizontal lockup** (mark + wordmark),
  **stacked lockup**.
- Light-surface and dark-surface versions; clear-space rule (≥ one lobe-height
  on all sides) and min-size rule (mark ≥ 24px, lockup ≥ 120px wide).
- Wordmark two-tone per §4.

### 8.4 Favicon
- 32 / 16px derived from the tray minimal glyph; `.ico` + SVG + 180px
  apple-touch-icon.

### 8.5 Social / Open Graph image
- 1200×630. Mark + tagline on a Macchiato gradient (Mantle → Crust) for link
  previews.

### 8.6 Monochrome / single-color variant
- One-color lemniscate (Text on dark, Crust on light) for watermarks, stamps,
  embossing, and any context where color is unavailable.

### 8.7 iOS-specific branding
(The tray has no iOS equivalent; alerts are the background-state UI per
`docs/ios-design.md`.)
- **Notification / alert glyph** — state-tinted lemniscate for push-style alerts.
- **Launch / splash mark** — centred mark, gentle fade-in (no flash; sensory-safe).
- **Apple Watch complication** — circular minimal glyph. **Designed, not shipped
  in v1**, matching the iOS design doc's WatchOS stance.

### 8.8 Annotation state icons
The four log labels in `AnnotationDialog` (Calm / Activated / Overwhelmed /
Recovering), each a lemniscate variant expressing that state's lobe balance —
direct reductions of the Regulation Field:
- **Calm** — symmetric, cool-leaning.
- **Activated** — warm lobe swelling.
- **Overwhelmed** — warm-dominant, cool lobe collapsed.
- **Recovering** — cool lobe refilling toward symmetry.

### 8.9 Brand spec essentials (the reference sheet)
- Full palette table with roles (§3).
- Typography rules (§4).
- Simplification ladder (§6).
- **Do / Don't list**, including: never the puzzle piece; never alarm-red; never
  high-contrast flashing or jarring motion; always pastel for intense states;
  jitter only at full detail; maintain clear-space and min-size.

---

## 9. Out of scope (v1)

- Animated/Lottie production of the Regulation Field (spec defines grammar;
  motion implementation is in the implementation plan).
- Android assets (framework kept Android-friendly per the iOS doc, but no assets).
- Marketing collateral beyond the OG image (no business cards, merch, etc.).
- Shipping the Apple Watch complication (designed only).

---

## 10. Open questions / for the implementation plan

- Exact gradient stops and jitter amplitude tuning for the full-detail mark
  (needs visual iteration once a master SVG exists).
- Whether the Regulation Field hero replaces or sits above the current ImPlot
  layout in the Windows status window.
- Final humanist typeface pick (Inter vs Outfit) — both satisfy the brief.
```
