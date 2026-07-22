# Causality Visualisation Redesign: Implementation Plan

- **Status:** Doing (Phase 1 in progress)
- **Issue:** [#1087](https://github.com/TetronIO/JIM/issues/1087)
- **PRD:** [`engineering/prd/doing/PRD_CAUSALITY_VISUALISATION_REDESIGN.md`](../../prd/doing/PRD_CAUSALITY_VISUALISATION_REDESIGN.md)
- **Design reference:** approved interactive mock-up (internal): https://claude.ai/code/artifact/c928e648-1fb1-4f39-961d-9c73c497dacb

## Overview

Replace the Causality Tree on the Run Profile Execution Item detail page (`/activity/item/{Id:guid}`) with the redesigned causality visualisation from the approved mock-up: a summary band (run chip, plain-English sentence with primary-colour entity highlights, colour-coded outcome pills), plain-language-first event naming with the technical term demoted alongside, three toggleable views (Flow, Timeline, Graph) persisted per user, a redesigned attribute change detail (operation badges, demoted type/plurality, monospace values, count-annotated filter chips plus search), and comprehensive entity linking including the Synchronisation Rule attribution (#1085) and deleted-Identity details (#1086) that now exist in the data.

No schema, Application-layer or Worker changes; this is a JIM.Web feature with a strong testable core.

## Business Value

Administrators currently have to walk a tree of MVO/CSO-vocabulary events to work out what a run did to an object. The redesign answers "what happened and what did it cause" in one sentence, teaches the technical vocabulary rather than assuming it, links every entity for fast pivoting, and lifts the visual quality of one of JIM's most distinctive capabilities to the standard the rest of the product is being held to.

## Technical Architecture

### Current state (what gets replaced)

- **Host section:** `src/JIM.Web/Pages/ActivityRunProfileExecutionItemDetail.razor` lines ~236-261 render `<OutcomeTree>` inside a `MudPaper` when `SyncOutcomes.Count > 0`. The page's separate "Projection Details", "Metaverse Impact" and legacy "Attribute Changes" sections (the last suppressed when SyncOutcomes exist) are untouched by this plan.
- **Components being replaced:** `src/JIM.Web/Shared/OutcomeTree.razor` (synthetic Connected System + record root nodes), `src/JIM.Web/Shared/OutcomeTreeNode.razor` (recursive node; inline link logic; parses the overloaded `DetailMessage` `"csId|csoTypeName"` channel inline), and their global styles in `src/JIM.Web/wwwroot/css/site.css` (`.outcome-tree` block, lines ~1896-1957).
- **Kept:** `src/JIM.Web/Shared/AttributeChangeTable.razor` remains for the page's legacy Attribute Changes section (used when no SyncOutcomes exist); the causality views get their own attribute detail component.
- **Display mapping today:** `src/JIM.Web/Helpers.cs` `GetOutcomeTypeDisplayName` / `GetOutcomeTypeMudBlazorColor` / `GetOutcomeTypeIcon`. Icon coverage for `AssertedNull` / `NoContributor` needs verifying and completing.
- **Data:** the page already loads everything needed via `jim.Activities.GetActivityRunProfileExecutionItemAsync(Id)`: flat `SyncOutcomes` (EF relationship fixup populates `Children` / `ParentSyncOutcomeId`), `ConnectedSystemObjectChange` / `MetaverseObjectChange` attribute changes, and per-outcome `ConnectedSystemObjectChange` snapshots for `PendingExportCreated`. Ordering is client-side by `Ordinal`. `SyncRuleId` / `SyncRuleName` exist on `ActivityRunProfileExecutionItemSyncOutcome` but are rendered nowhere yet.

### Proposed solution

**1. Testable core (plain C#, no Razor), new folder `src/JIM.Web/Causality/`:**

- `OutcomeDisplayMap` (static): for every `ActivityRunProfileExecutionItemSyncOutcomeType` value, the plain-language label ("Identity created"), technical label ("MVO Projected"), tone (`CausalityTone` enum: Primary/Success/Info/Warning/Error/Secondary) and icon. Existing `Helpers.GetOutcomeType*` methods delegate to it so other callers keep working; missing `AssertedNull`/`NoContributor` icons added here.
- `OutcomeDetailMessageParser` (static): extracts the `"csId|csoTypeName"` overload out of `OutcomeTreeNode.razor` into one tested place.
- `CausalityModelBuilder`: transforms an `ActivityRunProfileExecutionItem` into a `CausalityModel`:
  - `CausalityEvent` nodes mirroring the outcome tree (ordered by `Ordinal`), each with plain/technical labels, tone, icon, sentence segments, lane (`Source`/`Identity`/`Downstream`), owning Connected System (for downstream grouping), badge ("Destructive" for `MvoDeleted`, "Needs attention" for `ExportFailed`), entity links, and attribute rows.
  - Entity links built from existing routes: Identities via `Utilities.GetMetaverseObjectHref`, records via `GetConnectedSystemObjectHref`, Connected Systems via `GetConnectedSystemHref`, Synchronisation Rules via `/admin/sync-rules/{SyncRuleId}` (new; falls back to unlinked `SyncRuleName` when the id is null or the rule no longer resolves), deletion records via `/admin/deleted-objects`, Pending Exports via `/admin/connected-systems/{csId}/pending-exports`.
  - Attribute rows normalised from `ConnectedSystemObjectChangeAttribute` / `MetaverseObjectChangeAttribute` into `(Operation Set|Add|Remove, Name, TypeAndPlurality, Value, PreviousValue?)`, reusing the Add+Remove collapse ("Set with previous value") logic currently in `AttributeChangeTable`.
- `CausalitySummaryBuilder`: produces the summary band content:
  - A sentence as a list of segments (`Text` or `Entity(label, href, kind)`), never pre-rendered HTML, so values from connected systems are always encoded by Blazor at render time.
  - Sentence templates keyed on the dominant outcome shape (projection/join, out-of-scope and deletion, export attempt/failure, no-change), composed from the same event model; a generic fallback sentence covers unanticipated shapes.
  - Outcome pills derived from outcome type counts (e.g. "Identity created", "11 attributes flowed", "Provisioned · 1 system", "Export queued · 11 changes"), each with a tone.

**2. Components, new folder `src/JIM.Web/Shared/Causality/`:**

- `CausalityPanel.razor`: hosts everything; owns state (selected view, technical-names toggle, selected event, drawer contents, attribute filter/search); builds the `CausalityModel` once per parameter set.
- `CausalitySummaryBand.razor`: run chip, timestamp, sentence (entity segments rendered as `.hl` token chips in the primary colour), pill strip (MudChips).
- `CausalityFlowView.razor`: CSS grid, three columns (Source, Identity, Downstream) with column captions ("what came in" / "what JIM did" / "what it caused"); downstream events grouped per Connected System in group cards; SVG overlay with cubic elbow connectors.
- `CausalityTimelineView.razor`: vertical rail with tone-coloured dots, sentence-per-event with entity chips, indented nesting, inline attribute expansion.
- `CausalityGraphView.razor`: layered node-link SVG computed in C# (depth = x column; leaf counter = y; parents centred over children, exactly the mock-up's algorithm); tone accent bar per node; legend; click selects into the drawer.
- `CausalityEventCard.razor` and `CausalityEntityChip.razor`: shared card and glyph-chip (CS/R/ID/rule) primitives.
- `CausalityAttributeDetail.razor`: the redesigned attribute list (op badge, name with demoted type/plurality sub-line, monospace value with struck-through previous value), toolbar with search box, All/Set/Add/Remove filter chips with counts, and "n of m" indicator. Rendered in a bottom drawer for Flow/Graph and inline for Timeline.

**3. Styling:**

- New stylesheet `src/JIM.Web/wwwroot/css/causality.css` (linked from `_Layout.cshtml`), replacing the `.outcome-tree` block in `site.css`. All colours derive from theme tokens: `--mud-palette-primary`, `--mud-palette-*` and `color-mix(in srgb, var(--mud-palette-...) 12%, transparent)` for the soft pill/badge backgrounds, so all six themes (light and dark) work without per-theme additions. Fonts inherit JIM's existing typography (IBM Plex Sans; monospace stack for values).
- Focus-visible rings, `prefers-reduced-motion` support and the mock-up's responsive behaviour (columns stack below ~820px with connectors hidden; attribute value wraps to a full-width line).

**4. Flow connectors (the one JS touchpoint):**

- A small `wwwroot/js/causality.js` module measures card positions (`getBoundingClientRect`) after render and returns them to Blazor via interop; `CausalityFlowView` draws the SVG paths from those measurements and re-measures on resize (debounced) and on model change. If interop fails or the viewport is narrow, the view renders without connectors; nothing else depends on it.

**5. Preferences:**

- `IUserPreferenceService` gains `GetCausalityViewAsync`/`SetCausalityViewAsync` (`"flow" | "timeline" | "graph"`, default `flow`) and `GetCausalityTechNamesAsync`/`SetCausalityTechNamesAsync` (bool, default off), following the existing const-key + `jimPreferences.get/set` + swallow-interop-exception idiom. `CausalityPanel` loads both in `OnAfterRenderAsync(firstRender)` per the `TableDensityToggle` pattern.

### Data flow

```
GetActivityRunProfileExecutionItemAsync(Id)          (unchanged)
        │
        ▼
CausalityModelBuilder.Build(rpei)                    (pure, unit-tested)
        │  events: lanes, groups, labels, tones, links, attribute rows
        ▼
CausalitySummaryBuilder.Build(model)                 (pure, unit-tested)
        │  sentence segments + pills
        ▼
CausalityPanel ── view pref ──> Flow │ Timeline │ Graph
        │                                 │
        └────── drawer/inline ──> CausalityAttributeDetail
```

## Implementation Phases

Each phase is TDD (failing tests first), builds clean, and leaves the page working. Phases 2-4 are separable if review favours landing the views incrementally.

### Phase 1: Testable core

- `OutcomeDisplayMap` with complete coverage of all 20 outcome types (plain label, technical label, tone, icon); `Helpers.GetOutcomeType*` delegate to it; add the missing `AssertedNull`/`NoContributor` icons.
- `OutcomeDetailMessageParser` extraction.
- `CausalityModelBuilder` + `CausalitySummaryBuilder` with a test matrix over the three mock-up scenarios (new joiner, leaver, export failure) plus: no-change items, pre-#1085 data (null `SyncRuleId`/`SyncRuleName`), pre-#1086 deletions (no detail message), Standard vs Detailed tracking levels, and the generic-sentence fallback.
- New test project `test/JIM.Web.Tests/` (NUnit + bUnit 2.7.2, referencing JIM.Web) hosts all causality tests: plain-class tests in this phase, component tests from Phase 2. The logic lives in plain classes because four renderers (summary band + three views) consume it, not as a testing workaround; bUnit covers what remains in the Razor layer.
- Register the new project in `JIM.sln`, and update `test/CLAUDE.md` and the root `CLAUDE.md` (test project list, and retire the "no UI tests exist" carve-out for UI-only changes).

### Phase 2: Panel, summary band and Timeline view

- `CausalityPanel`, `CausalitySummaryBand`, `CausalityEntityChip`, `CausalityEventCard`, `CausalityTimelineView`, `CausalityAttributeDetail`, `causality.css`.
- User preference methods and the view/technical-names toggles.
- Replace the `<OutcomeTree>` section in `ActivityRunProfileExecutionItemDetail.razor` with `<CausalityPanel>`; delete `OutcomeTree.razor` / `OutcomeTreeNode.razor` and the `.outcome-tree` CSS block.
- bUnit component tests: summary band segment/pill rendering (entity chips link correctly, hostile values encoded), Timeline nesting and inline attribute expansion, MvoDeleted deletion-record link, view/technical-names toggles persisting via a stubbed preference service.
- Timeline ships first because it is structurally closest to the current tree: full information parity from day one.

### Phase 3: Flow view

- `CausalityFlowView` with the three-column grid, per-system downstream grouping, `causality.js` measurement interop, SVG connectors, responsive stacking, drawer wiring.
- bUnit component tests: lane/column assignment, per-system group cards, drawer opening on card selection, graceful rendering when the measurement interop fails (bUnit's JSInterop stubs simulate the failure).
- Flow becomes the default view (matching the mock-up's default).

### Phase 4: Graph view

- `CausalityGraphView` with the C# layered layout, node selection into the drawer, legend.
- bUnit component tests: node/edge counts for known tree shapes, selection behaviour, label truncation.
- Deliberately last: the PRD's open question resolves as "ship it, but sequence it so it can be dropped from the PR without rework if review prefers".

### Phase 5: Runtime validation, docs and changelog

- Runtime verification in the sandbox light stack (`pwsh ./scripts/Start-SandboxStack.ps1`, per `engineering/SANDBOX_RUNTIME_VERIFICATION.md`): drive the three scenario shapes end to end, verify light and dark for the default navy-o6 theme and spot-check the other theme families, keyboard navigation, and pre-#1085/#1086 rows rendering unenriched.
- Rewrite the outcome tree section of `docs/configuration/activities.md` around the summary band and the three views.
- ✨ changelog entry under `[Unreleased]`.
- Final `dotnet build JIM.sln` / `dotnet test JIM.sln` at zero warnings.

## Success Criteria

- Every acceptance criterion in the PRD ticks, including per-user view persistence and the #1085/#1086 fidelity rendering.
- All 20 outcome types have complete display mappings, proven by tests; no outcome shape renders an exception or an empty summary.
- `dotnet build JIM.sln` and `dotnet test JIM.sln` pass with zero errors and warnings; no new product/runtime dependencies (bUnit is test-only).
- Old components and their CSS removed; no dead code left behind.

## Benefits

- Comprehension: the summary band answers the "what happened" question without any interaction.
- Approachability with no fidelity loss: plain language first, technical vocabulary always present, swappable for practitioners.
- Navigability: every entity is one click from its detail page, including durable deletion records for entities that no longer exist.
- Maintainability: display logic moves from inline Razor into tested classes; the `DetailMessage` overload parsing gets a single owner; styling derives from theme tokens instead of hard-coded colours.

## Dependencies

- #1085 and #1086: shipped (PR #1098).
- Existing user preferences service and theme token system: no changes beyond additive preference methods.
- **bUnit 2.7.2** (MIT; actively maintained; supports net10.0 and NUnit; test-only, nothing ships in JIM containers) - approved 2026-07-22 under the third-party dependency governance process, for the new `test/JIM.Web.Tests/` project. No product/runtime dependencies added; air-gap safe.

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Razor-layer defects (parameter wiring, conditional rendering, callbacks) | Decision logic lives in `OutcomeDisplayMap`/builders (unit-tested); the thin components get bUnit tests per phase; Phase 5 runtime validation covers real-browser rendering |
| Summary sentences read wrongly for unanticipated outcome shapes | Template-per-shape with a tested generic fallback; the matrix includes no-change, error and legacy-data cases |
| Connected-system values contain hostile strings | Sentence and attribute values are rendered as text segments by Blazor's encoder; `MarkupString` is never used for data-derived content |
| Flow connector measurement races Blazor rendering | Measure in `OnAfterRenderAsync` + `requestAnimationFrame`; re-measure on resize; connectors are decorative, so failure degrades to a clean three-column layout |
| Six themes × light/dark could drift from the mock-up | All colours derive from `--mud-palette-*` tokens with `color-mix` softs; navy-o6 verified closely, remaining themes spot-checked in Phase 5 |
| Legacy rows (pre-#1085/#1086) missing enrichment | Builder treats `SyncRuleId`/`SyncRuleName`/detail messages as optional; tests pin the unenriched rendering |
| Page's overlapping sections (Projection Details, Metaverse Impact) look dated next to the new panel | Explicitly out of scope here; raise a follow-up issue after landing if the contrast jars |
