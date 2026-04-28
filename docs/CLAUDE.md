# Docs Site Reference

> MkDocs site conventions, diagram embedding, infrastructure internals, and writing style.
> For changelog format, see `engineering/CLAUDE.md`.

## Documentation Style

**Use `✅` for positive indicators and `❌` for negative indicators in tables** (e.g. feature comparison tables). Do NOT use `:material-check-bold:`, `:material-close:`, or other Material icon syntax -- these render as small monochrome icons. The emoji equivalents render as clear green/red symbols in MkDocs and on GitHub.

**Term-and-description bullet lists: put the description on a new line via `<br />`, not on the same line separated by a dash.** When a list pairs a bold label with descriptive text, the description reads better on its own line. The first word of the description is capitalised because it now starts a new line.

Format:

```markdown
- **Term**<br /> Descriptive text explaining the term.
- **Another term**<br /> More descriptive text.
```

Not:

```markdown
- **Term** -- descriptive text explaining the term.
```

This applies to feature lists, capability lists, and any similar "label + explanation" bullet structure. It does not apply to short flat bullet lists where each item is a single phrase.

**Use emojis to add energy and aid visual scanning, but keep them purposeful and professional.** JIM documentation should feel alive and modern, not dry -- but never unprofessional.

Where to use them:
- Table indicators: `✅` / `❌` for yes/no capability comparisons
- Changelog entries: one leading emoji per entry to signal the type of change (✨ new, 🐛 fix, ⚡ performance, 🔄 changed, 🗑️ removed, 🔒 security, 📦 deployment, 🖥️ UI/UX)
- Section lead-ins on feature-heavy or conceptual pages: a single emoji before a heading or card title to anchor the concept visually
- Admonition-style callouts where an emoji reinforces the tone (e.g. a warning or tip paragraph)

Where NOT to use them:
- Mid-sentence or inline within body text
- In API/technical reference content (endpoint tables, parameter lists, code comments)
- More than once per heading or table cell
- Decoratively on every heading -- only where it genuinely adds meaning or helps the reader navigate
- As a substitute for checklists: use `- [ ]` / `- [x]` task list syntax instead, which renders as styled checkboxes via the `pymdownx.tasklist` extension

## ASCII Diagrams

When creating ASCII diagrams in documentation or code comments, use only reliably monospaced characters:

| Use         | Instead of    | Purpose                        |
|-------------|---------------|--------------------------------|
| `->` `-->`  | `->` `-->` `>` | Arrows (horizontal)           |
| `<-` `<--`  | special chars | Arrows (reverse)               |
| `v` / `^`   | `v` `^`       | Arrows (vertical)             |
| `+`         | box-drawing chars | Corners and junctions      |
| `-` / `|`   | `=` special chars | Lines (horizontal/vertical)|
| `-`         | bullets       | Bullet points in diagrams      |

## C4 Architecture Diagrams (SVG)

C4 diagrams are exported SVGs from the Structurizr DSL (`engineering/diagrams/structurizr/`). Each diagram has a light and dark variant stored under `docs/diagrams/images/light/` and `docs/diagrams/images/dark/`.

Embed them as paired `<img>` tags so the correct variant shows for the active theme:

```html
<img class="diagram-light" alt="Description" src="../../diagrams/images/light/jim-structurizr-1-Example.svg">
<img class="diagram-dark"  alt="Description" src="../../diagrams/images/dark/jim-structurizr-1-Example.svg">
```

- CSS in `docs/assets/stylesheets/custom.css` hides the inactive variant based on `data-md-color-scheme`
- GLightbox picks up both `<img>` tags automatically; the hidden one is ignored by the browser
- **Path gotcha:** MkDocs serves pages at trailing-slash URLs (e.g. `/JIM/developer/architecture/`). Relative paths resolve from that URL, not the `.md` file location. Count `../` levels from the served URL, not the file path.

## Mermaid Diagrams

Write Mermaid diagrams inline in markdown as normal fenced code blocks:

````markdown
```mermaid
flowchart TD
    A --> B
```
````

No additional markup or attributes are needed. They are clickable automatically.

### How mermaid-zoom.js works

`docs/assets/javascripts/mermaid-zoom.js` provides the click-to-zoom behaviour. Understanding the internals matters for debugging or upgrading MkDocs Material.

**The problem:** MkDocs Material renders Mermaid into a **closed Shadow DOM** (`attachShadow({mode:"closed"})`), making the rendered SVG completely inaccessible via `querySelector` or `shadowRoot`.

**The solution:**

1. The script captures each diagram's source text from `<pre class="mermaid">` elements **before** Material replaces them with shadow-host `<div class="mermaid">` elements
2. Sources and divs are matched by **insertion order** (top-to-bottom DOM order) - stable because Material processes diagrams sequentially
3. On click, the captured source is re-rendered via the Mermaid JS API (already initialised on the page) using a temporary off-screen DOM node
4. The resulting SVG is displayed in a custom modal overlay (not GLightbox - Mermaid SVGs are handled separately from image files)

**The modal** is theme-aware: dark navy (`#051526`) in slate mode, white in default mode. Closes on backdrop click or Esc.

**No external dependencies** - uses only the Mermaid and GLightbox instances MkDocs Material already loads.

**If diagrams stop being clickable after a MkDocs Material upgrade:** check whether Material changed how it processes Mermaid. Look for changes to the `div.mermaid` shadow host insertion or `attachShadow` call (search for `attachShadow` in the bundle). The relevant function in the current bundle is named `Zn()`.

## GLightbox (image files)

GLightbox is configured as a plugin in `mkdocs.yml` and wraps all `<img>` tags automatically. No per-image markup is needed.

The lightbox background is themed via `docs/assets/stylesheets/custom.css` (`.gslide-image img` rules) so transparent SVGs render against the correct surface colour in each theme.

## Dependencies

MkDocs dependencies are pinned in two places - keep them in sync:

| File | Purpose |
|------|---------|
| `.devcontainer/setup.sh` | Installed in the dev container |
| `.github/workflows/docs.yml` | Installed in CI for GitHub Pages deployment |

Current pinned versions: `mkdocs>=1.6,<2`, `mkdocs-material>=9.7,<10`, `mkdocs-glightbox>=0.4,<1`.
