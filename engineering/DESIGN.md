---
name: JIM
description: >
  Design system for JIM (Junctional Identity Manager), a self-hosted
  identity lifecycle management platform by Tetron. Navy-o6 default theme.
colors:
  primary: "#6135d7"
  on-primary: "#ffffff"
  primary-lighten: "#614b9e"
  primary-darken: "#4e3b84"
  secondary: "#1e3a5f"
  on-secondary: "#ffffff"
  secondary-lighten: "#2a4e7a"
  secondary-darken: "#142a48"
  tertiary: "#85782b"
  success: "#2d7a3e"
  warning: "#b85a00"
  info: "#0076d3"
  error: "#c62828"
  background: "#f3f3f3"
  surface: "#ffffff"
  on-surface: "#121b29"
  on-surface-secondary: "#4a5568"
  on-surface-muted: "#7a8a9a"
  outline: "#b7bfca"
  brand-purple: "#5A45A2"
  brand-navy: "#1A2B45"
typography:
  display:
    fontFamily: Space Grotesk
    fontSize: 2.75rem
    fontWeight: 700
    letterSpacing: -0.02em
  h1:
    fontFamily: Space Grotesk
    fontSize: 2.25rem
    fontWeight: 700
    letterSpacing: -0.02em
  h2:
    fontFamily: IBM Plex Sans
    fontSize: 1.75rem
    fontWeight: 600
  h3:
    fontFamily: IBM Plex Sans
    fontSize: 1.375rem
    fontWeight: 600
  h4:
    fontFamily: IBM Plex Sans
    fontSize: 1.125rem
    fontWeight: 600
  h5:
    fontFamily: IBM Plex Sans
    fontSize: 1rem
    fontWeight: 600
  h6:
    fontFamily: IBM Plex Sans
    fontSize: 0.875rem
    fontWeight: 500
  body-lg:
    fontFamily: IBM Plex Sans
    fontSize: 1.125rem
    fontWeight: 400
    lineHeight: 1.6
  body-md:
    fontFamily: IBM Plex Sans
    fontSize: 1rem
    fontWeight: 400
    lineHeight: 1.5
  body-sm:
    fontFamily: IBM Plex Sans
    fontSize: 0.875rem
    fontWeight: 400
    lineHeight: 1.5
  label:
    fontFamily: IBM Plex Sans
    fontSize: 0.75rem
    fontWeight: 400
  code:
    fontFamily: IBM Plex Mono
    fontSize: 0.875rem
    fontWeight: 400
    lineHeight: 1.6
  code-sm:
    fontFamily: IBM Plex Mono
    fontSize: 0.75rem
    fontWeight: 400
rounded:
  sm: 4px
  md: 8px
  lg: 12px
  xl: 16px
  full: 9999px
spacing:
  xs: 4px
  sm: 8px
  md: 16px
  lg: 24px
  xl: 32px
  xxl: 48px
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.on-primary}"
    rounded: "{rounded.sm}"
    padding: 12px
    typography: "{typography.body-md}"
  button-primary-hover:
    backgroundColor: "{colors.primary-darken}"
    textColor: "{colors.on-primary}"
  button-secondary:
    backgroundColor: "{colors.secondary}"
    textColor: "{colors.on-secondary}"
    rounded: "{rounded.sm}"
    padding: 12px
    typography: "{typography.body-md}"
  button-secondary-hover:
    backgroundColor: "{colors.secondary-darken}"
    textColor: "{colors.on-secondary}"
  button-outlined:
    backgroundColor: "#FFFFFF00"
    textColor: "{colors.primary}"
    rounded: "{rounded.sm}"
    padding: 12px
  card:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.on-surface}"
    rounded: "{rounded.sm}"
    padding: 16px
  chip-mv:
    backgroundColor: "#6135D729"
    textColor: "{colors.primary-darken}"
    rounded: "{rounded.full}"
    padding: 8px
  chip-cs:
    backgroundColor: "#1E3A5F29"
    textColor: "{colors.secondary-darken}"
    rounded: "{rounded.full}"
    padding: 8px
  nav-sidebar:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.on-surface-secondary}"
    width: 240px
  nav-sidebar-active:
    textColor: "{colors.primary}"
  table-header:
    backgroundColor: "{colors.background}"
    textColor: "{colors.on-surface}"
    typography: "{typography.body-sm}"
  alert-success:
    backgroundColor: "{colors.success}"
    textColor: "{colors.on-primary}"
    rounded: "{rounded.sm}"
  alert-warning:
    backgroundColor: "{colors.warning}"
    textColor: "{colors.on-primary}"
    rounded: "{rounded.sm}"
  alert-error:
    backgroundColor: "{colors.error}"
    textColor: "{colors.on-primary}"
    rounded: "{rounded.sm}"
  alert-info:
    backgroundColor: "{colors.info}"
    textColor: "{colors.on-primary}"
    rounded: "{rounded.sm}"
  chip-mv-prefix:
    backgroundColor: "#FFFFFF00"
    textColor: "{colors.primary-lighten}"
    rounded: "{rounded.sm}"
  chip-cs-prefix:
    backgroundColor: "#FFFFFF00"
    textColor: "{colors.secondary-lighten}"
    rounded: "{rounded.sm}"
  input-field:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.on-surface}"
    rounded: "{rounded.sm}"
    padding: 12px
  input-field-placeholder:
    textColor: "{colors.on-surface-muted}"
  divider:
    backgroundColor: "{colors.outline}"
    height: 1px
---

## Overview

JIM is a self-hosted, container-native identity lifecycle management platform. The UI is built with Blazor Server and MudBlazor. The visual identity communicates precision, trust, and technical sophistication for an audience of CISOs, IAM architects, and identity practitioners.

The default portal theme is **navy-o6**, a paired light/dark palette built around a deep purple primary. The design system is deliberately flat: cards use subtle background shifts rather than shadows, surfaces stay close in value, and the purple accent is reserved for interactive elements. Air-gapped deployments require all assets (fonts, icons) to be self-hosted; no external CDN dependencies in the portal.

The global default border radius is set to **4px** (`rounded.sm`) via `MudTheme.LayoutProperties.DefaultBorderRadius` in `MainLayout.razor`. The `rounded.md`, `rounded.lg`, and `rounded.xl` tokens above exist for one-off cases where a softer corner is needed; the bulk of buttons, cards, inputs, and alerts use the 4px default.

## Colors

The navy-o6 palette uses semantic tokens mapped through MudBlazor's `--mud-palette-*` CSS custom properties. The YAML above documents the **light mode** values. Dark mode values are listed below.

### Dark mode (navy-o6-dark)

| Token | Dark value |
|---|---|
| primary | `#764ce8` |
| on-primary | `#ffffff` |
| secondary | `#95a6bb` |
| on-secondary | `#0a1018` |
| tertiary | `#c4ba7f` |
| success | `#66d96e` |
| warning | `#ffb74d` |
| info | `#64b5f6` |
| error | `#ff8a80` |
| background | `#051526` |
| surface | `#0d1e30` |
| on-surface | `#e0e4ea` |
| on-surface-secondary | `#8a9eb4` |
| outline | `#243a52` |

### Semantic colour roles in causality visualisations

- **Primary**: Metaverse-side concepts. MV badge, MV chip-type labels (`.jim-mv-chip-prefix` maps to `primary-lighten`).
- **Secondary**: Connected-system-side concepts. CS avatar chips, attribute-flow outcome chips, operation chips, CS chip-type labels (`.jim-cs-chip-prefix` maps to `secondary-lighten`).
- **Tertiary**: Not used in causality views; reserved for accents elsewhere.

On hover, `Color.Primary` avatars darken to `--mud-palette-primary-darken` for contrast against the primary chip background. Chip-prefix classes flip to `primary-text` inside `.jim-chip-link:hover`.

### Transparent variants

Use `color-mix(in srgb, var(--mud-palette-*) X%, transparent)` for transparent colour variants. Do not use the `rgba(var(--*-rgb), α)` pattern. The `-rgb` triplet custom properties exist in theme files only for MudBlazor's internal use (hover, ripple, focus on built-in components); never reference them from JIM's own CSS.

### Brand colours vs theme colours

The brand purple (`#5A45A2`) and brand navy (`#1A2B45`) are the root identity colours used on the docs site, in marketing, and in the brand guide PDF. The portal primary tokens (`#6135d7` / `#764ce8`) are tuned for interactive UI on their respective backgrounds. Agents generating portal UI should use the `--mud-palette-*` tokens. Agents generating marketing or docs content should use the brand values.

### Alternate themes

Five additional theme pairs exist alongside navy-o6 (`black`, `blended-nav`, `future-minimal`, `navy-o5`, `purple`), giving six total light/dark theme pairs. They follow the same semantic token structure with different hue choices. Theme files live at `src/JIM.Web/wwwroot/css/themes/`. Navy-o6 is the default (set in `Program.cs`) and the only theme documented in detail here.

## Typography

JIM uses a three-tier type system. Each typeface has a defined role; do not mix them outside these roles.

### IBM Plex Sans (primary)

Body text, UI controls, navigation, list items, table content, and heading levels H2 to H6. The default voice everywhere except code and the display-accent layer. Designed by IBM, SIL Open Font License 1.1. Sharper character disambiguation (1/l/I, 0/O) than many grotesques, which matters where GUIDs, DNs, and command snippets appear.

Self-hosted in the portal at `wwwroot/fonts/ibm-plex-sans/`. Loaded via Google Fonts on the docs site.

### IBM Plex Mono (technical)

Code blocks, inline code, GUIDs, DNs, command snippets, file paths, log excerpts. Never in marketing headings or running prose.

Self-hosted in the portal at `wwwroot/fonts/ibm-plex-mono/`. Loaded via Google Fonts on the docs site.

### Space Grotesk (display accent)

Used sparingly for hero titles, the portal sidebar wordmark, and publication-style surfaces. Its tech-leaning character (mono-derived a, g, numerals) anchors the brand mark without committing to a full display serif.

**Where to use:** docs site hero title and header bar, portal sidebar "JIM" wordmark (SemiBold 600, letter-spacing -0.02em, top offset 2px for optical alignment), marketing landing-page hero copy, conference slide title slides, whitepaper covers.

**Where not to use:** body text, sub-headings (H2 to H6), tables, UI controls, or marketing prose. Overuse tips the brand from "considered modern" toward "developer-tool studio".

Self-hosted in the portal at `wwwroot/fonts/space-grotesk/`. Loaded via CSS `@import` from Google Fonts on the docs site.

### Heading weight corrections

MudBlazor 9 ships H5 at weight 400 and H6 at weight 500, creating a visual inversion. JIM overrides H5 to 600 and pins H6 at 500 in `MainLayout.razor`. Material for MkDocs ships H1 at weight 300 with a lightened colour; JIM pins all heading colours to `--md-default-fg-color` and steps weights H1 400, H2/H3/H4 500 in `custom.css`.

### Fallback stacks

- **Body:** IBM Plex Sans, -apple-system, Segoe UI, sans-serif
- **Display:** Space Grotesk, IBM Plex Sans, -apple-system, sans-serif
- **Mono:** IBM Plex Mono, Consolas, Courier New, monospace

Space Grotesk cascades to Plex Sans on failure, preserving design system coherence.

## Layout

The portal uses a fixed sidebar (240px, MudBlazor's `DrawerVariant.Mini` default) with a main content area. The sidebar contains the logo plus wordmark, navigation tree, and footer links. Content surfaces use an 8px grid aligned with MudBlazor's default spacing scale.

Cards and elevated surfaces use subtle background shifts between `background` and `surface` tokens. No drop shadows on cards or data tables; the design is deliberately flat. Grid-card hover lightens the border rather than adding a shadow.

### Docs site

The MkDocs Material docs site uses a standard two-column layout (nav plus content). Table and admonition body text is set at 0.72rem (up from Material's 0.64rem default). Default Material hover shadow on cards is removed for a flatter look consistent with the portal.

## Components

### Buttons

Primary buttons use `primary` background with `on-primary` text, 4px corners (the global `DefaultBorderRadius`). Outlined buttons use transparent background with `primary` text and border. Hover state darkens to `primary-darken`.

### Chips

Metaverse text-chips render as a 16% tint of `primary` over the surface, with `primary-darken` text. Connected-system text-chips render as a 16% tint of `secondary`, with `secondary-darken` text. Both use `rounded.full` for pill shape. The chip-type prefix labels (`.jim-mv-chip-prefix`, `.jim-cs-chip-prefix`) sit inside chips as tinted text drawn from the `-lighten` variants of each colour, providing a softer accent against the tinted chip body.

### Cards

Cards use `surface` background with 4px corners (the global `DefaultBorderRadius`) and `outline` border. No box-shadow. Hover state lightens the border colour.

### Navigation

Sidebar uses `surface` background. Active nav item indicated by `primary` colour on text and left border accent. Inactive items use `on-surface-secondary`.

### Data tables

Header row uses `background` token. Row content uses `body-sm` typography. Alternating row shading with surface/background tokens. No row hover shadow.

## Diagrams

The diagram design system covers the docs site (`docs.junctional.io`) and marketing surfaces (`junctional.io`), so diagrams read as one family across both properties. Concept diagrams are **hand-authored SVGs inlined into the page** (via `pymdownx.snippets` on the docs site): geometry lives in the SVG, and every colour lives in the site stylesheet as a `--jimdg-*` custom property, so a single SVG serves both light and dark mode through the normal theme toggle. The reference implementation is `docs/assets/diagrams/hub-and-spoke.svg` plus the "Concept diagrams" section of `docs/assets/stylesheets/custom.css`.

Diagrams use the **brand colours**, not the portal `--mud-palette-*` tokens, consistent with the brand-vs-theme rule above. The semantic roles mirror the causality-visualisation roles: purple for JIM/metaverse-side concepts, navy for Connected-System-side concepts.

### Element tokens

| Element | Light | Dark |
|---|---|---|
| Diagram background | transparent (page shows through) | transparent |
| JIM boundary (hub container) | fill `#f8f6fd`, stroke `#5A45A2`, label `#483790` | fill `#1d1a3d`, stroke `#7B68B8`, label `#c9bfe8` |
| Metaverse core | fill `#5A45A2`, text `#ffffff`, stroke `#483790` | fill `#6a4fc2`, text `#ffffff`, stroke `#7B68B8` |
| JIM inner layer (e.g. Connectors) | fill `#ede8f8`, stroke `#7B68B8`, text `#483790` | fill `#2a2352`, stroke `#7B68B8`, text `#d5ccef` |
| Pipeline stage / neutral node | fill `#ffffff`, stroke `#b7bfca`, text `#121b29` | fill `#0d1e30`, stroke `#243a52`, text `#e0e4ea` |
| Connected System (external) | fill `#ffffff`, stroke `#1e3a5f`, text `#1A2B45` | fill `#0d1e30`, stroke `#2a4e7a`, text `#e0e4ea` |
| Planned / roadmap element | fill `#f7f7f7`, dashed stroke `#b7bfca`, text `#7a8a9a` | fill `#0a1a2c`, dashed stroke `#243a52`, text `#8a9eb4` |
| Edge (data flow) | stroke `#4a5568` | stroke `#8a9eb4` |
| Edge (JIM-internal flow) | stroke `#5A45A2`, label `#483790` | stroke `#7B68B8`, label `#c9bfe8` |
| Edge (planned flow) | dashed, `#7a8a9a` | dashed, `#5c718a` |

### Rules

- **One SVG, both themes.** No colour literals inside diagram SVGs: shapes and text carry `.jimdg-*` classes and the stylesheet's `[data-md-color-scheme]` token blocks do the theming. Adding a diagram must not add new colour values outside the token blocks.
- **Typography:** IBM Plex Sans, inherited from the page's webfonts (set via the `.jim-diagram` class). No Space Grotesk in diagrams; it is reserved for display surfaces.
- **Geometry:** 8px corner radius on nodes and containers (matching docs cards and portal panels); flat fills, no shadows or gradients. Arrowheads are explicit small polygons, not SVG markers (markers restyle unreliably across browsers).
- **Accessibility:** every diagram SVG carries `role="img"` with a `<title>` and a `<desc>` that narrates the concept.
- **Planned (roadmap) elements** are dashed, muted, and say "(planned)" in their label; no separate legend.
- **Sync-model diagrams never draw system-to-system edges**; every flow passes through JIM, visually reinforcing the hub-and-spoke metaverse pattern.
- **Motion: data packets, not marching dashes.** Edge animation is small dots (SMIL `animateMotion`) travelling in the direction of each arrow, in the `--jimdg-packet` token colour (`#5A45A2` light / `#9d86e8` dark); animated dashes are not used because dashes encode "planned". Packets are slow and staggered, fade in and out at the ends of their path, and appear **only on live edges** - planned edges never animate (nothing flows there yet). All packets are hidden under `prefers-reduced-motion: reduce`.
- **British English** in all labels; JIM domain entity names are Title Cased per the writing rules.

## Do's and Don'ts

### Do

- Use `--mud-palette-*` custom properties for all colour references in portal CSS.
- Use `color-mix()` for transparent variants of palette colours.
- Keep Space Grotesk to display surfaces only: hero titles, the sidebar wordmark, slide title slides.
- Self-host all fonts in the portal for air-gap compatibility.
- Maintain the H5 600 / H6 500 weight overrides when updating MudBlazor.
- Use semantic colour roles consistently: primary for metaverse-side, secondary for connected-system-side.

### Don't

- Reference `--mud-palette-*-rgb` triplets from JIM's own CSS; those exist only for MudBlazor internals.
- Promote Space Grotesk to body text, sub-headings, tables, or UI controls.
- Add drop shadows to cards or table rows.
- Use brand colours (`#5A45A2`, `#1A2B45`) in portal component code; use the `--mud-palette-*` tokens instead.
- Hard-code hex values in component CSS; always reference theme tokens.
- Load external fonts via CDN in the portal; the docs site may use CDN since it is public-web.
