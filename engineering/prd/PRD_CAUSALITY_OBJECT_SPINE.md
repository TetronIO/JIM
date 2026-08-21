# Causality Object Spine

- **Status:** Planned
- **Created:** 2026-08-21
- **Author:** JayVDZ (with Claude)
- **Issue:** [#1495](https://github.com/TetronIO/JIM/issues/1495)

## Problem Statement

A Run Profile Execution Item's detail page currently splits one question across two presentations. The
causality panel (Flow, Timeline and Graph views) shows what this run did, laid out in three lanes that
read as the classic ILM pipeline: source system on the left, Metaverse in the middle, target systems on
the right. The **Caused by** list underneath separately narrates why, as sentences walking back across
earlier runs.

This split fails the administrator in three ways, all observed in real use:

1. **The lanes lie on export items.** An Export item's subject is a record in a *target* system, but the
   Flow view presents it in the left-hand "Source" lane, so it reads as a source-system record. The
   Identity lane sits empty ("No Identity changes."), which reads as a missing Metaverse Object rather
   than as "this run did not touch the Identity".
2. **The object graph is invisible.** The administrator's mental model is the object graph the
   documentation teaches: source record → Identity → target record. Neither presentation shows it. The
   panel shows lanes of events; the list shows sentences. Which objects the story is about, and how they
   relate, has to be reconstructed in the reader's head.
3. **Attention is split.** The panel and the list describe the same story in different vocabularies at
   different scroll positions. Reading one, then finding the corresponding part of the other, is work the
   page should be doing.

## Goals

- One canvas answers "what happened and why": the object graph behind the outcome (each record involved,
  the Identity, and the relationships between them), with every causally relevant event placed on the
  object it happened to. The spine replaces both the Flow and Graph views; Timeline remains for strict
  chronological reading.
- This run's work is unmistakable at a glance; earlier runs' work is present but visibly older, each
  event carrying its run and a link to the item that recorded it.
- The **Caused by** section is retired, with none of its content lost: hops, cohort collapse, snapshot
  wording and the three chain endings all render on the canvas.
- The same layout serves every item type: an import lights the source column, a synchronisation lights
  the Identity (and any staging it did), an export lights the target column.
- Verified by: rendering the canvas for a create-export, an update-export, a deprovision, an import and a
  synchronisation item from a Scenario 4 run, and confirming each reads correctly without the retired
  section.

## Non-Goals

- **No graph-layout library or pan/zoom canvas technology.** The layout is deterministic (a handful of
  object columns, events stacked by time), computed server-side like the existing views. Wide stories
  scroll horizontally. A free-form zoomable canvas (Cytoscape-class) was evaluated and rejected: it costs
  a third-party dependency and reads worse for the common three-object story. Revisit only if chains
  routinely fan out beyond what columns can show.
- **Not an audit timeline.** The canvas shows the events on this item's causal story, not each object's
  full history; complete per-object history stays on the object detail pages.
- **No new capture.** The canvas renders data already recorded: this item's sync outcomes and the causal
  chain (#1223 snapshots and the derived source-import hop). No schema or write-path changes.
- **No REST/PowerShell surface change.** This is a portal rendering of data those surfaces already carry
  through the Activity endpoints; parity is display-inapplicable here. (Called out per the surface-parity
  rule; confirm agreement rather than deferring silently.)

## User Stories

1. As an administrator, I want one view showing the objects involved in an operation and what happened to
   each, so that I can understand an outcome without reconstructing the object graph in my head.
2. As an administrator looking at an exported record, I want to see the Identity it serves and the source
   record that fed it, with the provisioning decision and import that led here, so that I can trace an
   export to its true root in one place.
3. As an administrator investigating a deprovision, I want the deletion decision and its Deletion Rule on
   the same canvas as the export that carried it out, so that "why was this account deleted?" is answered
   without leaving the page.

## Requirements

### Functional Requirements

1. The causality canvas presents the object graph as columns: each record involved (with its Connected
   System named beneath it) and the Identity, ordered source → Identity → target(s), joined by labelled
   relationships (imported, projected, joined, provisioned, exported). Columns are headed by the `R` /
   `ID` chip vocabulary; a record's column never carries the `CS` glyph, because the column is the record,
   not the system.
2. Every causally relevant event renders as a card on the column of the object it happened to: this
   item's own sync outcomes, plus the hops of the causal chain (including the derived source-import hop).
   Chain-hop cards use the chain's snapshotted wording, names and rule attributions unchanged, so deleted
   and renamed objects still read correctly.
3. Events from this run are visually primary (accent ring, "This run" marker); events from earlier runs
   are visually subdued and each carries its run type, its timestamp and a "View activity item" link.
   Subdued must remain legible without interaction: text contrast meets WCAG AA before any hover effect.
4. A column is headed by the single object when the story has one, and by its role when plural: a cohort
   of causes sharing an attribution tuple renders as one subdued card carrying a count, expanding in
   place to name each member, exactly as the Caused by list collapses cohorts today.
5. Each chain ending renders as a quiet footer under the column it closes, with today's three endings
   kept distinct: end of the recorded chain, cause no longer retained, more causes beyond the depth
   bound. Endings are neutral, never warning-toned.
6. Export outcomes state the decision, never a bare "Exported": record created, changes applied, or
   record deleted, from the change data already on the item.
7. The canvas serves every ObjectChangeType the panel serves today, lighting the column where this run's
   events land; no item type loses causality display in the migration.
8. The **Caused by** section, the **Flow** view and the **Graph** view are removed in the same release
   the canvas ships; the view switcher offers Spine and Timeline, with Spine the default. A stored view
   preference for a retired view falls back to Spine without being overwritten.
9. The Technical names toggle continues to work across the canvas (plain language by default, technical
   vocabulary on demand), and the summary sentence band above the canvas is unchanged.
10. Stories wider than the viewport scroll horizontally within the canvas; the page body never scrolls
    sideways. On narrow viewports columns stack vertically, as the Flow view's columns do today.

### Non-Functional Requirements

- No additional database queries beyond what the panel and chain load today; the canvas is a
  re-projection of already-loaded data.
- Keyboard navigation and visible focus for every interactive element; `prefers-reduced-motion`
  respected; both themes designed, not inverted.

## Examples and Scenarios

### Scenario 1: Create export

**Given**: Baseline User was imported from Training Records Source, projected, and provisioned to
Yellowstone OpenLDAP; this item is the export that created the LDAP record.
**When**: the administrator opens the export item.
**Then**: three columns render: `R person: Baseline User` (record in Training Records Source) with a
subdued "Imported · new record" card and an "End of the recorded chain" footer; `ID Baseline User` with
subdued "Identity created · Projected" and "12 attributes flowed" cards; `R inetOrgPerson: Baseline User`
(record in Yellowstone OpenLDAP) with a subdued "Provisioning decided" card naming the Synchronisation
Rule, and a primary "This run · Record created · Exported" card. No Caused by section renders.

### Scenario 2: Deprovision after deletion

**Given**: an Identity was deleted by a Deletion Rule, staging a delete export; this item is the export
that removed the account.
**When**: the administrator opens the deprovision item.
**Then**: the Identity column carries the subdued deletion decision (with the snapshotted Deletion Rule
attribution) even though the Identity no longer exists; the target column carries the primary "Record
deleted" card; the chain ending appropriate to the walk renders under the leftmost column.

### Scenario 3: Cohort cause

**Given**: ten Users were deleted, removing them from a group's Static Members, and this item is the
group's export.
**When**: the administrator opens the item.
**Then**: the Identity column carries one subdued card, "10 Users were deleted", expanding in place to
name each; ten separate columns are never rendered.

### Scenario 4: Import item

**Given**: a record arrived in an import.
**When**: the administrator opens the import item.
**Then**: the source record's column carries the primary card; Identity and target columns show only what
the chain records (typically nothing yet), and the layout is the same shape as every other item's.

## Constraints

- Air-gap deployable; no external assets or services.
- No new NuGet or JavaScript dependencies (see Non-Goals).
- British English throughout; JIM's proper-cased domain entity names; the established chip vocabulary
  (`R`, `CS`, `ID`, `SR`).

## Affected Areas

| Area | Impact |
|------|--------|
| UI | New spine canvas component set replacing the Flow and Graph views, with Spine the panel's default; `CausalityCausedBy*`, `CausalityFlowView` and `CausalityGraphView` (and their layout calculators) retired; `CausalityModelBuilder` extended to project outcomes and chain hops onto object columns |
| Application | None expected: `GetCausalChainAsync` and the item load already supply the data |
| Database / API / Worker | None |
| Tests | New bUnit + model-builder suites in `test/JIM.Web.Tests/`; retirement of Caused By rendering tests; migration of Flow view tests |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/configuration/activities.md` | Rewrite the causality view section around the canvas; retire the Caused by section's description into it |
| `engineering/plans/doing/CAUSALITY_VISUALISATION_REDESIGN.md` | Close out or supersede; the spine replaces its Flow-view centrepiece |

## Dependencies

- #1223 (causal provenance) merged: the chain, its snapshots and the export queueing seam are what the
  canvas renders. The `feature/causal-provenance` branch must land first.

## Decisions

1. **The spine replaces the Graph view as well as the Flow view** (decided 2026-08-21). The Graph view's
   node-link rendering of same-run events is subsumed by the spine, and keeping it would mean maintaining
   two graph-shaped views; Timeline stays for strict chronological reading.
2. **Wide stories rely on horizontal scroll alone** until real data shows a "+n more systems" collapse is
   needed.

## Acceptance Criteria

- [ ] The five Scenario 4 item types (create export, update export, deprovision, import, synchronisation)
      each render the canvas correctly, verified against a live integration-test run.
- [ ] The Caused by section no longer renders anywhere, and no information it carried is unreachable:
      hops, cohorts, snapshot wording and all three endings render on the canvas.
- [ ] This run's events are visually distinct from earlier runs' events, and the subdued treatment passes
      WCAG AA without hover.
- [ ] Export outcomes name the decision (created / changes applied / deleted); a bare "Exported" no
      longer appears.
- [ ] The Technical names toggle, both themes, keyboard focus, reduced motion and narrow-viewport
      stacking all verified.
- [ ] bUnit and model-builder suites cover the projection rules (column derivation, cohort collapse,
      endings, lit-column selection per item type).

## Additional Context

- Design exploration and approved direction (Option B, "the object spine"): the Caused By artefact's
  "Exploration: one canvas" section, including the rejected alternatives (point fixes only; a
  Cytoscape-class zoomable canvas) and why.
- The Flow-view lane misreading and the empty-Identity complaint were raised against item
  `2932861b-35b8-4158-a1d7-48885ec16203` (Baseline User create export) on a Scenario 4 run, 2026-08-21.
- Prior art: `engineering/prd/doing/PRD_CAUSALITY_VISUALISATION_REDESIGN.md` (the three-view redesign the
  spine builds on) and `engineering/prd/doing/PRD_CAUSAL_PROVENANCE.md` (#1223, the chain the spine
  renders).
