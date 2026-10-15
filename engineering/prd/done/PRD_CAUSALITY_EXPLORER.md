# Causality Explorer (Causal Provenance Phase 3)

- **Status:** Done
- **Created:** 2026-10-11
- **Author:** JayVDZ (drafted with Claude Code)
- **Issue:** [#2100](https://github.com/TetronIO/JIM/issues/2100)
- **Plan:** [CAUSALITY_EXPLORER.md](../../plans/done/CAUSALITY_EXPLORER.md)
- **UI mock:** [Causality Explorer](https://claude.ai/artifact/56hNH8ryx3TvQzTFdeJrxP) (revision 1, approved 2026-10-11)
- **Parent PRD:** [Causal Provenance](PRD_CAUSAL_PROVENANCE.md), whose Phase 3 was sketched directionally and left to "its own design pass". This is that pass.

## Problem Statement

Phases 1 and 2 answer two questions about one Run Profile Execution Item: what caused it, and what it went on to cause. Both answers live on that item's own page, in the causality panel's Lineage. Three gaps remain, and each is visible in the product today.

**Questions start somewhere else.** An administrator rarely begins with an execution item. They begin with a Pending Export they do not recognise, or a Connected System Object or Metaverse Object that looks wrong, and they would first have to find the item that explains it. Phase 1's first user story begins on exactly such a page ("a Pending Export with no local explanation"), yet the Pending Export page has no route to the run that queued it. The data is already there: a Pending Export records the synchronisation item that staged it (`QueuedByRunProfileExecutionItemId`), and each synchronisation that stages a change carries an outcome naming the Pending Export.

**Following a chain means changing page.** Every cause and consequence card links to "View activity item", which opens that item's full page. The Lineage there sits below the Execution Summary, so following five hops costs five page loads and five scrolls, and the only record of the route is the browser's back button.

**Objects are dead ends.** A Lineage column's head links to the object's own page, which leaves causality entirely. To ask "what else happened to this object, and why", an administrator opens its change history and then each change's item, one at a time. Change history only exists where change tracking is switched on, so on some deployments even that route is missing.

## Goals

- From a Pending Export, a Connected System Object, a Metaverse Object or an execution item, an administrator reaches the explanation of what they are looking at in one click. Verifiable: each of the four pages carries an entry point, and the explorer it opens shows the relevant cause without further navigation.
- Following causes and consequences from item to item happens in place, with a visible trail back. Verifiable: five hops in the explorer cause no full page load, the trail lists all five foci, and selecting any one returns to it exactly.
- Every view in the explorer costs what one item's Lineage costs today, however large the graph behind it. Verifiable: the walk bounds are unchanged, cohorts and timelines are paged, and an object with 10,000 execution items opens its first page in the same time as one with ten.
- Every read the explorer adds is available over REST and PowerShell. Verifiable: the object timelines and a Pending Export's queueing runs each have an endpoint and a cmdlet, with tests and docs.

## Non-Goals

- **No free-form node-and-edge graph canvas.** Decided 2026-10-11: a cohort of 4,000 Groups cannot be drawn usefully as nodes, and a second visual language would compete with the Lineage rather than extend it.
- **No Activity-wide aggregate** ("everything this run caused"). An Activity can hold hundreds of thousands of items; aggregating their consequences is a different read with different bounds, and nothing in this design depends on it.
- **No new capture.** No new causal edge types and no worker changes. Everything here reads what Phases 1 and 2 already record.
- **No retention class for causal edges.** The parent PRD's open question 4 stays open.
- **No attribute-value provenance** ("why does this attribute hold this value"). The Metaverse Object's attribute inspector already names each value's contributing source.
- **No redesign of the item page's Lineage.** The explorer reuses it. The item page keeps its own links and gains one action that opens the explorer.

## User Stories

1. As an administrator looking at a Pending Export, I want to see which runs staged it and why, so that I can explain or challenge the change before it is exported.
2. As an administrator looking at a Connected System Object or a Metaverse Object, I want to see what happened to it across runs and the cause of each, so that I can explain its current state without opening every change.
3. As an administrator following a cascade several hops, I want to move from item to item without losing my place, so that I can trace a long chain to its root and come back.
4. As an administrator whose trace reaches the edge of what the panel shows, I want to continue from the farthest point reached, so that the walk's bounds never end my investigation.
5. As an administrator who scripts investigations, I want the same reads over REST and PowerShell, so that I can answer these questions in bulk.

## Requirements

### Functional Requirements

#### Entry points

1. The Pending Export page MUST show a **Queued by** panel listing each run that staged a change into this Pending Export, newest first. Each row names the run (Run Profile, Connected System, when), what it staged, and its nearest recorded cause in one line, and opens the explorer focused on that item. A Pending Export staged by no recorded item MUST say so plainly rather than show an empty panel.
2. The Connected System Object and Metaverse Object pages MUST each offer an action that opens the explorer focused on that object.
3. The execution item page's causality panel MUST offer an action that opens the explorer focused on that item.

#### The explorer

4. The explorer is a page of its own with two kinds of focus: an execution item, and an object (a Connected System Object or a Metaverse Object).
5. An **item focus** MUST render the Lineage exactly as the item page does (what caused it, this run, after this run), with the same walk bounds (5 levels up, 6 down) and the same cohort paging.
6. An **object focus** MUST render the object's causal timeline: the execution items about it, newest first and paged, each naming when, the run, what happened and its nearest recorded cause in one line. Selecting a row re-centres on that item.
7. **Re-centring.** Inside the explorer, every reference to an item (a cause, a consequence, a cohort member) MUST re-centre on that item, and every object's head MUST re-centre on that object, without a full page load. Each focus MUST have its own address, so the browser's back and forward buttons and bookmarks work.
8. **Trail.** The explorer MUST show the foci visited, in order. Selecting one returns to it. The trail MUST survive a reload and be shareable as a link; it keeps the most recent foci up to a fixed limit.
9. **Way out.** Every focus MUST link to the full page it stands for (the item page, the object page), so the explorer never traps the reader.
10. **Going further.** Where a walk stops at its bound, its last card MUST offer to re-centre on the farthest item reached. Re-centring is how a trace continues beyond the bounds; the bounds themselves do not grow.

#### Surface parity

11. REST MUST expose a Connected System Object's and a Metaverse Object's causal timeline, and a Pending Export's queueing runs, each row carrying its nearest cause.
12. PowerShell MUST expose the same reads, as parameters on the existing cmdlets where they fit (`Get-JIMActivityExecutionItem`, `Get-JIMPendingExport`) rather than as new nouns.
13. Re-centring and the trail are portal-only navigation and have no REST or PowerShell counterpart: the API already walks causes and consequences from any item id, which is all a script needs to do the same.

### Non-Functional Requirements

- **Bounded per view.** A focus reads one item's walk (bounded as today) or one page of one object's timeline, never the whole graph. Nothing in the explorer scales with the size of the graph behind it.
- **Indexed timelines.** An object's timeline MUST be read through indexed columns. Items are indexed by Connected System Object already; a Metaverse Object's timeline spans its joined objects' items, its Metaverse Object changes and the items that name it directly, and the plan must show each of those is indexed or add the index.
- **No per-row queries.** The nearest cause for a page of timeline or Queued by rows is read in one batch for the page.
- **Authorisation** follows the parent PRD's resolved open question 2: the explorer requires what Activity pages require, and the REST endpoints stay Administrator-only like the rest of the Activities API.
- **No polling.** As in Phase 2, a focus is a reading taken when it loaded; the existing **Check again** affordance is the only refresh.

## Examples and Scenarios

### Scenario 1: A Group's Pending Export, explained from its own page

**Given**: Project-Pulse has a Pending Export removing three members, staged by the Delta Synchronisation that processed three leavers' deletions in Yellowstone APAC.
**When**: the administrator opens that Pending Export.
**Then**: **Queued by** names the Delta Synchronisation run, says it staged the removal of three members from Static Members, and gives the nearest cause ("3 Metaverse Objects deleted"). Selecting it opens the explorer focused on that synchronisation item, with the deletions above it.

### Scenario 2: An object's history, with the cause of each step

**Given**: Kate Hall's account in Glitterband EMEA was provisioned, updated twice and deprovisioned across four Activities.
**When**: the administrator opens the account's Connected System Object page and chooses to explore it.
**Then**: the explorer shows four rows, newest first: deprovisioned (because the Metaverse Object was deleted), two updates (each with the import that changed the source attribute), provisioned (because the Metaverse Object was projected). Selecting the deprovision row shows its full Lineage.

### Scenario 3: Five hops without leaving the page

**Given**: the explorer is focused on a confirming import.
**When**: the administrator follows its cause to the export, to the synchronisation that queued it, to the deletion, to the import that removed the source record.
**Then**: no full page load occurs, the trail lists five foci, back returns one focus at a time, and the link copied from the address bar reopens the same focus with the same trail.

### Scenario 4: The walk's bound is not the end

**Given**: an item whose consequences run deeper than six levels.
**When**: the administrator reaches the last card the walk shows.
**Then**: the card offers to re-centre on the farthest item reached, and doing so shows the next six levels.

### Scenario 5: No change tracking

**Given**: change tracking is switched off, so the Metaverse Object has no change history.
**When**: the administrator explores the Metaverse Object.
**Then**: the timeline still lists the items recorded against its joined Connected System Objects, and says that changes made directly to the Metaverse Object are not recorded on this deployment.

## Constraints

- No new runtime packages or JavaScript libraries; the explorer is Blazor and MudBlazor like the rest of the portal.
- Respect the layering: the new reads are exposed through `JimApplication`, never `Jim.Repository.*`, from `JIM.Web`.
- Self-contained and air-gapped; British English; JIM nouns in Title Case; no em dashes.

## Affected Areas

| Area | Impact |
|------|--------|
| Database | Possibly an index for the Metaverse Object timeline; no new tables |
| Worker | None |
| Application | Object timeline reads, a Pending Export's queueing runs, nearest-cause batch read |
| API | Timeline endpoints for Connected System Objects and Metaverse Objects; a Pending Export's queueing runs |
| PowerShell | New parameters on `Get-JIMActivityExecutionItem` and `Get-JIMPendingExport` |
| UI | A new explorer page; entry points on the Pending Export, Connected System Object, Metaverse Object and execution item pages; the Lineage's links made re-centrable |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/configuration/causality.md` | A section on exploring causality from any object, re-centring and the trail |
| `docs/powershell/activities.md`, `docs/powershell/connected-systems.md` | The new parameters, output shapes and examples |
| `engineering/DEVELOPER_GUIDE.md` | The explorer as a component, if the plan introduces shared navigation state |

## Dependencies

- Phase 1 ([#1223](https://github.com/TetronIO/JIM/issues/1223)) and Phase 2 ([#1811](https://github.com/TetronIO/JIM/issues/1811)), both done.
- The parent PRD gated this phase on Phases 1 and 2 being proven in real deployments. That gate is not met: JIM is pre-release, and the evidence is the Scenario 8 lab and runtime review. Starting now was a deliberate decision (2026-10-11), on the basis that the design reads only what is already captured, so it cannot invalidate the edge model.

## Open Questions

All five settled at mock review on 2026-10-11, each as recommended.

1. ~~Should the item page's own "View activity item" links open the explorer?~~ **Resolved: no.** The item page stays the record; the explorer is reached through its **Open in explorer** button.
2. ~~Can a deleted object be a focus?~~ **Resolved: yes,** found through its deletion record and by its external ID within its Connected System, the fallback the walk already uses, and the timeline says so.
3. ~~How long is the trail?~~ **Resolved:** the 10 most recent foci, items and objects alike, carried in the page address.
4. ~~Which views does the explorer offer?~~ **Resolved:** the Lineage only; Table and Timeline stay on the item page.
5. ~~Where does the explorer live in navigation?~~ **Resolved:** no menu entry. It always opens on a focus; with none, it says where to start from.

## Acceptance Criteria

- [x] The Pending Export page's **Queued by** panel lists every run that staged a change into it, each with its nearest cause, and opens the explorer on that item (Scenario 1). *Evidence: Phase 3-0 (every staging path findable, #2112); the panel's bUnit tests and runtime walk-through in Phase 3d; Scenario 8 at Medium asserts `-QueuedBy` over the same read.*
- [x] The Connected System Object, Metaverse Object and execution item pages each open the explorer focused on themselves. *Evidence: Phase 3d entry points, checked at runtime from each page.*
- [x] An object focus lists the object's items newest first, paged, each with its nearest cause read in one batch per page (Scenario 2), and a Metaverse Object's timeline is complete without change tracking for everything recorded against its joined objects (Scenario 5). *Evidence: the Phase 3a and 3b reads and their unit and PostgreSQL tests; the timeline component's paging in Phase 3d; the not-recorded note through the API in Phase 3e.*
- [x] Re-centring on an item or object causes no full page load; every focus has its own address; the trail survives reload and is shareable (Scenario 3). *Evidence: the explorer's page-level bUnit tests of re-centring and the trail (Phase 3d); Copy link and reload checked at runtime.*
- [x] A walk that stops at its bound offers to continue from the farthest item reached (Scenario 4). *Evidence: Continue from, upwards and downwards, in Phase 3d.*
- [x] REST and PowerShell expose both object timelines and a Pending Export's queueing runs, with tests and docs; re-centring and the trail are documented as portal-only. *Evidence: Phase 3e (controller and Pester tests, generated reference); the causality page's Causality Explorer section.*
- [x] An object with 10,000 items opens its first timeline page within the same budget as one with ten, verified by a database test against real PostgreSQL. *Evidence: the Phase 3a budget and EXPLAIN tests over the three filtered indexes.*
- [x] A changelog entry and a `docs/` update ship in the same pull request as the user-facing change. *Evidence: Phase 3f.*
