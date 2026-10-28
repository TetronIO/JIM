# A Diagram on Every Docs Page

- **Status:** Done
- **Created:** 2026-10-27
- **Author:** Jay Van der Zant (drafted with Claude)
- **Issue:** [#2564](https://github.com/TetronIO/JIM/issues/2564)

## Problem Statement

The documentation site at docs.junctional.io is text-heavy. Its few diagrams (the concept SVGs under `docs/assets/diagrams/` and a handful of Mermaid flowcharts) are some of the most effective parts of the site, but only 12 of 155 pages have one:

| Page type | With a diagram | Pages |
|-----------|----------------|-------|
| `reference` | 0 | 59 |
| `object` | 3 | 51 |
| `concept` | 6 | 17 |
| `how-to` | 1 | 16 |
| `troubleshooting` | 0 | 6 |
| `landing`, `tutorial` | 2 | 6 |

An administrator reading about a Synchronisation Rule, a Connected System or an API key has to build the picture in their head from prose and tables. Where a page explains a flow, a lifecycle or a structure, a diagram explains it faster and is remembered better. The standard already says so for concept pages ("a diagram where the shape is the point", VOI-5), but nothing asks for one anywhere else, so diagrams are rare.

## Goals

- Every customer page carries at least one diagram, and each diagram is specific to its page: it says something only that page could say. No decorative or shared "banner" diagrams.
- Important subjects within a page are explained with a diagram as well as text, by a written rule that says when a section needs one and when it does not.
- Diagrams are cheap to make and keep accurate, through a small set of shared diagram archetypes rather than one-off drawings.
- Every diagram reads well in light and dark mode, at the reading width, at full width and on a phone.
- The Configuration section (36 object pages and its other pages) is done first, then the rest of the site in section batches.

## Non-Goals

- A single generated map repeated on every page with a different highlight. It was mocked up and rejected: a picture that is 90% identical on every page becomes wallpaper.
- Screenshots of the portal. VOI-5 still applies.
- Redrawing the existing concept SVGs or Mermaid diagrams. They already count.
- Diagrams in `engineering/` contributor docs, which this standard does not cover.
- Interactive or animated diagrams beyond what the existing concept SVGs already do.

## User Stories

1. As an administrator configuring JIM, I want each page to show me the shape of what it describes, so that I understand how the pieces fit before I read the detail.
2. As a scripter reading a PowerShell reference page, I want to see what each cmdlet does to the object it manages, so that I pick the right cmdlet without reading every Syntax block.
3. As a documentation author, I want a rule that tells me when a section needs a diagram, and archetypes to build it from, so that adding one is a known amount of work rather than a design project.
4. As a reviewer, I want a check that tells me which pages still lack a diagram, so that the gap closes rather than grows.

## Requirements

### Functional Requirements

1. **Diagram archetypes.** A small set of archetypes in the house diagram style (`engineering/DESIGN.md` > Diagrams), each with its own layout and its phone layout:
   - **Flow:** something that processes, with steps and branches (a Synchronisation Rule, an Export run).
   - **Lifecycle:** something that moves between states, with the transitions labelled (a Connected System and its cmdlets, an Activity, a Pending Export).
   - **Anatomy:** something made of parts, taken apart with callouts (an API key, a Run Profile).
   - **Decision:** a branching diagnosis, for troubleshooting pages.
   - **Comparison:** two or more options side by side, for "which one do I use" pages.
   - **Relationship map:** how a set of things connect, each node linking to where it is described (the glossary's map of JIM's core terms; concept pages).

   The existing concept SVGs and Mermaid diagrams remain valid alongside these.
2. **Authoring: a per-page spec rendered at build time.** A diagram is written as a short spec (its archetype, nodes, labels and edges) beside the page, and a MkDocs build hook (alongside `docs/hooks/changelog.py`) renders it to an inline SVG. The renderer owns layout, the phone layout, theming, label cuts and the `<title>`/`<desc>`, so a label change is a one-word edit. A hand-authored SVG remains allowed for a diagram no archetype can express.
3. **Unique content.** Each diagram's labels and structure come from its own page. Where the page has a numbered list or a set of headings the diagram depicts, the diagram uses the same names and numbers, so it works as an index to the text below it.
4. **Placement.** A page's first diagram sits directly after its opening paragraph (STR-7), unless the page's structure puts it better at a section that introduces the subject.
5. **Section-level rule.** A section needs a diagram when its text describes any of:
   - a flow of three or more steps or components;
   - a lifecycle of states;
   - relationships among three or more things;
   - a decision that branches;
   - precedence or ordering.

   It does not need one for a list of settings, a single step, or anything a table already says better.
6. **Rules in the documentation standard,** as a new `VIS` family in `engineering/DOCUMENTATION_STANDARD.md`:
   - **VIS-1 (Ratchet):** the page has at least one diagram. It is checked by `Test-DocsConformance.ps1`, counting diagram specs, concept SVG includes, inline `svg.jim-diagram` elements and Mermaid fences. Existing pages are seeded into the baseline (`-SeedBaseline -Rule VIS-1`), so nothing fails today; a new page without a diagram fails from its first commit, and each batch lowers the baseline. The changelog (`docs/reference/changelog.md`) is excused with an `allow-page` marker: its content is release entries generated from `CHANGELOG.md`. The glossary is not excused; it gets the relationship map.
   - **VIS-2 (Review):** a section that meets the trigger in requirement 5 has a diagram.
   - **VIS-3 (Review):** a diagram carries information specific to its page; no decorative or generic diagrams.
   - **VIS-4 (Review):** what a diagram shows matches the page's text and the product, like ACC-4 for prose.
7. **Accessibility.** Every diagram meets A11Y-1 and A11Y-2: a `<title>`, and a `<desc>` that carries the same information as the picture.
8. **Theming and size.** Diagrams take every colour from the `--jimdg-*` tokens and render text at close to body-caption size in the reading column. They are capped so they do not scale up past their design size at full width.
9. **Phone layout.** Each archetype has a narrow layout below about 600px wide, so diagram text stays readable on a phone rather than shrinking to a few pixels.
10. **Lightbox.** New diagrams are click-to-enlarge through `diagram-zoom.js` like the existing ones, with no per-diagram markup.
11. **Batches.** Diagrams are added section by section, starting with Configuration. Each batch is reviewed as a contact sheet of every diagram in the batch, in light and dark, before it lands.

### Non-Functional Requirements

- No new third-party dependency (Mermaid is already loaded by Material; the archetypes are SVG).
- A diagram never makes the page body scroll sideways at any width.
- Edge labels follow the existing label-cut rules (`docs/CLAUDE.md` > Concept Diagrams) and pass `Update-DiagramLabelCuts.ps1 -Check`.

## Examples and Scenarios

Mock-ups of three archetypes on real pages, rendered from the built site in both themes: [JIM Docs Page Map](https://claude.ai/artifact/LATYeyGX2u1YSgfxAYyBu1).

### Scenario 1: Flow, on Synchronisation Rules

**Given**: the page lists what a rule ties together: 1 Direction, 2 Scoping criteria, 3 Object Matching Rules, 4 Projection or Provisioning, 5 Attribute mappings
**When**: a reader opens the page
**Then**: under the opening paragraph, a diagram shows a Connected System Object entering the rule, passing Scoping criteria (2) and Object Matching Rules (3), going straight to Attribute mappings (5) on a match or through Projection (4) on no match, and arriving at a Metaverse Object, with "Direction: inbound" (1) on the rule

### Scenario 2: Lifecycle, on the PowerShell Connected Systems page

**Given**: the page documents New, Set, Get, Clear, Remove and the deletion preview cmdlets
**When**: a scripter opens the page
**Then**: a diagram shows the Connected System's states (Configured, Holding objects, Deleted) with each cmdlet as the arrow it causes, and the deletion preview marked as the check before Remove

### Scenario 3: A page with no diagram

**Given**: a pull request changes a page that has no diagram
**When**: `changelog-lint` runs
**Then**: if the page is in the baseline, the check passes (its debt is recorded); if it is a new page, VIS-1 fails until the page has a diagram

### Scenario 4: A section that needs a diagram

**Given**: a new section describes a four-step export retry sequence in prose
**When**: the documentation review runs on the pull request
**Then**: it raises a VIS-2 thread asking for a flow diagram, and the merge waits until the thread is resolved

## Constraints

- British English, Title Case product nouns and no em dashes, in diagram labels and `<desc>` text as everywhere else.
- No colour literals in diagrams; tokens only.
- Air-gap deployable: no external diagram service.

## Affected Areas

| Area | Impact |
|------|--------|
| Docs site | New diagram archetypes and their CSS in `custom.css`; new diagrams on every page |
| Conformance tooling | VIS-1 in `Test-DocsConformance.ps1` and `docs/.conformance/config.psd1`; VIS-2 to VIS-4 in the rubric review |
| Diagram tooling | Archetype rendering, and possibly a contact-sheet script for batch review |

## Documentation Impact

| Doc | Change |
|-----|--------|
| `engineering/DOCUMENTATION_STANDARD.md` | New `VIS` rule family and rule index rows; VOI-5 links to it |
| `engineering/DESIGN.md` | Diagrams section: the archetypes, their layouts and the phone layout |
| `docs/CLAUDE.md` | How to add a diagram from an archetype |
| `docs/**` | A diagram on every page, Configuration first |

## Dependencies

- None. Builds on the existing concept diagram tokens, `diagram-zoom.js` and `Update-DiagramLabelCuts.ps1`.

## Decisions

Settled with the product owner on 2026-10-27:

1. **Authoring:** a per-page spec rendered to SVG by a build hook (requirement 2), not hand-written SVG per page.
2. **Enforcement:** VIS-1 is Ratchet from the start, not Warn: existing pages are baselined, new pages must have a diagram.
3. **Exemptions:** the changelog only. The glossary gets a relationship map of JIM's core terms, each node linking to its entry.

## Acceptance Criteria

- [ ] The archetypes exist with light, dark and phone layouts, and are documented in `engineering/DESIGN.md`
- [ ] VIS-1 to VIS-4 are in the documentation standard; VIS-1 runs in `Test-DocsConformance.ps1` at Ratchet, seeded over existing pages
- [ ] Diagram specs render to SVG at build time, with the phone layout, through the build hook
- [ ] The glossary has its relationship map
- [ ] Every page in the Configuration section has at least one diagram specific to it, reviewed as a contact sheet
- [ ] Every new diagram passes A11Y-1/A11Y-2 and `Update-DiagramLabelCuts.ps1 -Check`, and causes no horizontal scroll at any width
- [ ] The remaining sections have a batch plan, ordered by section

## Additional Context

- Rejected alternative: a generated "where this sits" map, the same on every page with the subject highlighted (first mock-up at the link above).
- Existing diagram mechanics: `docs/CLAUDE.md` > Concept Diagrams, Mermaid Diagrams, Diagram lightbox.
