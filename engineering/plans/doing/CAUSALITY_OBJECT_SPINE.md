# Causality Object Spine — Implementation Plan

- **Status:** Doing
- **Issue:** [#1495](https://github.com/TetronIO/JIM/issues/1495)
- **PRD:** [`engineering/prd/doing/PRD_CAUSALITY_OBJECT_SPINE.md`](../../prd/doing/PRD_CAUSALITY_OBJECT_SPINE.md)

## Overview

Replace the causality panel's Flow and Graph views, and the separate **Caused by** section, with one
canvas: the object spine. Columns are the objects in the item's causal story (each record with its
Connected System named beneath it, and the Identity), joined by labelled relationships; every causally
relevant event renders as a card on the object it happened to, with this run's events visually primary
and earlier runs' events subdued, each linking to the item that recorded it. Timeline remains as the
second view. The design is Option B of the one-canvas exploration recorded in the PRD; the decisions
already taken there (Graph view retired too; horizontal scroll rather than a column cap; no graph
library) are treated as settled.

## Business Value

An administrator opening a Run Profile Execution Item today reads two presentations of one story: lanes
of this run's events, then sentences about earlier runs. On export items the lanes actively mislead (the
target record renders in the "Source" lane; the Identity lane reads as missing). The spine shows the
object graph the documentation teaches, answers "what happened and why" in one place, and removes a whole
section from the page.

## Technical Architecture

### Current state

- `CausalityModelBuilder` (JIM.Web/Causality) projects the item's `SyncOutcomes` into a `CausalityModel`
  of `CausalityEvent` trees, each event assigned a `CausalityLane` (Source / Identity / Downstream).
  `CausalityFlowView` renders the lanes with measured SVG connectors (`CausalityFlowConnectorCalculator`);
  `CausalityGraphView` renders the same events as a node-link SVG (`CausalityGraphLayoutCalculator`);
  `CausalityTimelineView` renders them chronologically.
- `ActivityServer.GetCausalChainAsync` walks the causal edges (plus the derived source-import hop) into a
  `CausalChain` of `CausalChainCohort`s, each cohort carrying snapshotted wording inputs (edge type,
  reason code, Connected System, Synchronisation Rule, object nouns, attribute) and `CausalChainMember`s
  whose `Causes` nest the next level; `IsTruncatedByDepth` and per-member resolution state drive the
  three endings. `CausalityCausedBy`/`CausalityCausedByHop` render it as the sentence list, with
  `CausalityCauseWording` producing the sentence and reason text.
- `CausalityPanel` owns the view switcher (Flow default), the technical-names toggle, the summary band,
  and the shared attribute-detail drawer; preferences persist per user, and an unknown stored view
  already falls back to the default.

### Proposed

One new projection layer and one new view; the chain walk, wording and capture layers are untouched.

- **`CausalitySpineModel`** (new, JIM.Web/Causality): the canvas as data. `Columns` (ordered source →
  Identity → targets), each column carrying its head (object identity: record + system name, or Identity,
  or a role head when plural), its `Cards` in time order, and its optional `Ending`; `Joins` between
  adjacent columns carrying the relationship label. Cards are either **this-run cards** (wrapping the
  existing `CausalityEvent`, so attribute rows, links, tones and the drawer keep working) or **chain
  cards** (wrapping a `CausalChainCohort` + member context, with sentence/reason text from
  `CausalityCauseWording`, run chip, timestamp, "View activity item" link, cohort expansion and
  per-member nesting flattened onto the owning column).
- **`CausalitySpineModelBuilder`** (new, pure static): builds the spine from
  `(CausalityModel, CausalChain, CausalityPageContext)`. Owns the projection rules: column derivation
  (which objects exist in this story, and their order), event→column assignment (from `CausalityLane`
  plus the event's system), chain-hop→column assignment (from the cohort's snapshot: system for record
  hops, Identity for Metaverse hops), lit-column selection per `ObjectChangeType`, cohort collapse
  (a cohort is one card; never one column per member), ending placement, and join labelling.
- **`CausalitySpineView`** (new Razor) + a spine card component: renders the model. Primary cards get the
  accent ring and "This run" marker; subdued cards use tone-token treatments that keep text at WCAG AA
  without hover (no bare opacity dimming). Reuses `CausalityEntityChip`, the drawer, and the existing
  responsive idiom (columns stack under the Flow view's existing breakpoint; the canvas scrolls
  horizontally inside its own scroller).
- **Panel**: view switcher becomes Spine | Timeline with Spine the default; the existing unknown-view
  fallback absorbs stored Flow/Graph preferences without writes.
- **Retired**: `CausalityFlowView`, `CausalityFlowConnectorCalculator` (+ measurement types),
  `CausalityGraphView`, `CausalityGraphLayoutCalculator` (+ node/edge/layout types), `CausalityCausedBy`,
  `CausalityCausedByHop`, and their CSS blocks. `CausalityCauseWording` survives as the chain cards'
  text source. `ActivityRunProfileExecutionItemDetail.razor` stops passing/rendering anything
  Caused-by-specific.

### Export decision captions (PRD requirement 6)

`OutcomeDisplayMap`'s Exported labels become decision-aware: "Record created" / "Changes applied" /
"Record deleted", derived from the queueing edge's reason code (`ExportCreateStaged` /
`ExportUpdateStaged` / `ExportDeleteStaged`), which the causal chain already carries to the page. The
item's `ConnectedSystemObjectChange.ChangeType` cannot supply this: `ExportChangeHistoryBuilder` records
`Exported` for creates and updates alike, so the reason code is the only durable copy of the decision
(the Pending Export row that knew it is deleted on execution). Items with no queueing edge (pre-#1223
history) keep the bare "Exported" label honestly. This lands in the display map so the Timeline and the
summary band benefit as well as the spine.

## Implementation Phases

### Phase 1: Spine model and builder (pure C#, TDD)

- `CausalitySpineModel` types and `CausalitySpineModelBuilder`, with a test suite in
  `test/JIM.Web.Tests/` written first, covering: column derivation and order for each `ObjectChangeType`
  (create/update/delete export, deprovision, import, projection, join, attribute flow, disconnect,
  housekeeping deletion); lit-column selection; chain-hop assignment including the derived source-import
  hop and the deprovision chain (deleted Identity's column built from snapshots); cohort collapse; the
  three endings and their columns; join labels; plural role heads (Scenario 3 in the PRD).
- Export decision captions in `OutcomeDisplayMap` (+ tests), since the spine cards and Timeline both
  consume them.
- No UI change yet; `dotnet build JIM.sln` / `dotnet test JIM.sln` gates as usual.

### Phase 2: Spine view components

- `CausalitySpineView` + card component + CSS (tokens derived from the existing `--cz-*` palette; the
  artefact's Option B mock is the visual reference). bUnit tests: primary vs subdued rendering, run
  chips and activity links, cohort expansion, endings, chip vocabulary (`R`/`ID` heads, system named
  beneath, no `CS`-headed columns), technical-names toggle, drawer integration, keyboard focus.
- Wire into `CausalityPanel` as a selectable view (default not yet switched), so the spine can be
  verified against live data alongside the old views.
- Runtime verification against a Scenario 4 run: the PRD's five item types, both themes, narrow
  viewport. UI change → artefact mock refresh if the built result diverges from the approved mock.

### Phase 3: Make it the page (retirements)

- Spine becomes the default; switcher reduces to Spine | Timeline; stored Flow/Graph preferences fall
  back silently (covered by the panel's existing idiom + a pinning test).
- Remove `CausalityFlowView`, `CausalityGraphView`, `CausalityCausedBy*`, both layout calculators, their
  CSS and their tests; migrate any Flow/Graph test cases that pin still-relevant behaviour (connector
  semantics die with the views; wording tests stay on `CausalityCauseWordingTests`).
- Remove the Caused by rendering from `ActivityRunProfileExecutionItemDetail.razor` and the chain-prop
  plumbing that only it used (the chain itself continues to load, for the spine).
- Changelog (✨/🔄 + 🗑️ as fits one or two entries) and docs: rewrite the causality section of
  `docs/configuration/activities.md` around the spine; close out or supersede
  `engineering/plans/doing/CAUSALITY_VISUALISATION_REDESIGN.md` and its PRD per the filing lifecycle.

### Phase 4: Full-pass verification

- `dotnet build JIM.sln` / `dotnet test JIM.sln` clean; Scenario 4 integration run green; live checks of
  the PRD's acceptance criteria including WCAG AA on subdued cards (contrast-checked per theme, not
  eyeballed), reduced motion, and the empty-outcome fallback (outcome tracking off) still rendering the
  panel's existing notice.

## Success Criteria

The PRD's acceptance criteria, verbatim; the plan adds only mechanical gates (build/test clean per
phase, bUnit coverage of every projection rule, runtime verification on live Scenario 4 data).

## Benefits

- One canvas answers what happened and why; a whole page section is removed.
- Net code reduction: two views, two layout calculators and the list renderer retire against one new
  view and one pure builder.
- The projection layer is pure and unit-tested, so future item types or chain shapes land as builder
  cases, not view surgery.

## Dependencies

- #1223 causal provenance merged (`feature/causal-provenance`): the chain, snapshots and queueing seam
  are the spine's input. This plan starts on a fresh branch after that merge.
- No new packages, JS or otherwise.

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Subdued cards fail WCAG AA (opacity dimming fails contrast by construction) | Dim via colour tokens, not opacity; per-theme contrast check is an acceptance criterion |
| A chain shape the spine cannot place (unexpected edge type / missing snapshot) | Builder places unassignable hops on a neutral trailing column rather than dropping them; a test pins that nothing in the chain is ever silently omitted |
| Wide stories (many systems) become unreadable | Horizontal scroll inside the canvas (decided in the PRD); revisit a "+n more" collapse only on real data |
| Losing behaviour users rely on from Flow/Graph (attribute drawer, event selection) | The drawer and selection model are panel-level and carried over; bUnit tests assert parity before retirement |
| Stored view preferences point at retired views | The panel's existing unknown-view fallback; pinned by test |
| Timeline and spine drift in vocabulary | Both read `OutcomeDisplayMap` and `CausalityCauseWording`; the decision captions land in the shared map |
