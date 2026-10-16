# Causality Explorer: Implementation Plan (Causal Provenance Phase 3)

- **Status:** Done
- **Issue:** [#2100](https://github.com/TetronIO/JIM/issues/2100)
- **PRD:** [PRD_CAUSALITY_EXPLORER.md](../../prd/done/PRD_CAUSALITY_EXPLORER.md)
- **Earlier phases:** [Phase 1 plan](CAUSAL_PROVENANCE.md), [Phase 2 plan](CAUSAL_PROVENANCE_PHASE_2.md)
- **UI mock:** [Causality Explorer](https://claude.ai/artifact/56hNH8ryx3TvQzTFdeJrxP) (revision 1, approved 2026-10-11)
- **Created:** 2026-10-11

## Overview

Phases 1 and 2 explain one execution item from that item's own page. This phase lets an administrator start where the question arises (a Pending Export, a Connected System Object, a Metaverse Object) and follow a chain without changing page. It adds three entry points, a Causality Explorer page that re-centres the existing Lineage in place with a trail back, and the matching REST and PowerShell reads. Nothing new is captured beyond Phase 3-0's stager list: every read uses what Phases 1 and 2 already record, plus three indexes.

## Business Value

An administrator who finds a Pending Export they do not recognise has no route today to the run that queued it, although JIM recorded it. Following a five-hop chain costs five page loads, each landing above the Lineage. An object's history is reachable one change at a time, and only where change tracking is on. The explorer turns each of those into one click, at the same read cost as a single item page.

## Resolved design decisions

Approved at mock review, 2026-10-11:

1. **Scope:** entry points plus re-centring. No graph canvas, no Activity-wide aggregate, no new capture.
2. **The item page keeps its own links.** "View activity item" still opens the item page; the explorer is reached through an **Open in explorer** button on the causality panel.
3. **A deleted object can be a focus,** found through its deletion record and, as the walk already does, by its external ID within its Connected System.
4. **The trail holds the 10 most recent foci,** items and objects alike, and travels in the page address.
5. **The explorer shows the Lineage only.** Table and Timeline stay on the item page.
6. **No menu entry.** The explorer always opens on a focus; the breadcrumb is Activities › Causality Explorer, and an address with no focus says where to start from.
7. **Parity:** the two new reads (an object's causal timeline, a Pending Export's queueing runs) ship on all three surfaces. Re-centring and the trail are portal-only navigation, because the API already walks from any item id.

## Technical Architecture

### Current state (verified against the code)

- **Lineage links are strings built inside the builders.** `CausalityLineageModelBuilder.GetActivityItemHref` and the consequence placement write `/activity/item/{id}`, object heads carry the object page's address, and `CausalityLineageConsequenceCard.razor` hard-codes the same address for paged cohort members.
- **The item page assembles everything the panel needs itself,** in roughly 250 lines of `ActivityRunProfileExecutionItemDetail.razor`: the item, its Activity, both Connected System headers, the identity (with the deleted-identity fallback), the Metaverse Object Type (four fallbacks), live Pending Export ids, provisioned object names, the decision-time deletion policy, the upward chain, and the consequences after first render.
- **A live Pending Export lists its stagers** (`StagedByRunProfileExecutionItemIds`, oldest first, the most recent 50), since Phase 3-0. Before that it named only the latest, and a later run restaging an export still queued replaced it under a new id, losing the earlier run entirely.
- **Items have no time of their own;** their time is their Activity's `Executed`. Items are indexed by `ConnectedSystemObjectId` and `ExternalIdSnapshot`, not by `MetaverseObjectId` (set only on export scope review items).
- **Deleted objects keep their history under a different column.** On deletion, `MetaverseObjectChanges` rows move from `MetaverseObjectId` to `DeletedMetaverseObjectId`, and a deleted Connected System Object's change records carry `DeletedConnectedSystemObjectId`. Neither is indexed.
- **Batched cause reads exist:** `GetCausalEdgesByEffectRunProfileExecutionItemIdsAsync` and `GetRunProfileExecutionItemCausalSummariesAsync`. The source-import hop is read per object (`GetLatestImportItemForCsoAsync`, `...ForExternalIdAsync`).
- **Both walks already mark where they stopped:** `CausalChainResolution.DepthLimitReached` on an upward cohort, `CausalConsequenceCohort.IsFurtherThanShown` downward, each with member item ids.

### Proposed solution

**1. A link policy for the Lineage (`ICausalityLinks`, JIM.Web/Causality).** One interface for every address the Lineage writes: an item, a Connected System Object, a Metaverse Object, plus the label an item link carries. `CausalityPageLinks` reproduces today's addresses and labels exactly; `CausalityExplorerLinks` produces explorer addresses carrying the trail, labelled **Explore from here**. The builders take the policy instead of formatting strings, and the view passes it to the consequence card for paged members. This is the only change the item page sees, and its existing tests must pass unchanged.

**2. One loader for an item's causality (`CausalityItemLoader`, JIM.Web/Causality).** The item page's assembly code moves, unchanged, into a class both the page and the explorer call. It returns a `CausalityItemData` (everything `CausalityPanel` takes today) and keeps the consequences read separate, so both hosts can still read it after first render. The panel stays a rendering component with no data access of its own.

**3. The explorer page (`Pages/CausalityExplorer.razor`).**

| Route | Focus |
|-------|-------|
| `/causality` | none: says where to start from |
| `/causality/items/{id:guid}` | an execution item: the Lineage |
| `/causality/connected-system-objects/{id:guid}` | a Connected System Object, live or deleted: its causal timeline |
| `/causality/metaverse-objects/{id:guid}` | a Metaverse Object, live or deleted: its causal timeline |

The trail is the `trail` query value: comma-separated tokens `i:`, `c:` and `m:` followed by an id, oldest first, at most 10. `CausalityTrail` (a plain class) parses, appends, truncates at a revisited focus and serialises it; malformed or unknown tokens are dropped, never thrown on. Every explorer address is a real link, so Blazor's router re-parameterises the same page instance without a page load, and new-tab and back behave as anywhere else. The page reloads its focus in `OnParametersSetAsync`, keyed on the focus as the item page keys its consequences read, so a superseded read never lands on the wrong focus. Trail labels for all tokens resolve in one batch read; a focus that no longer resolves reads "No longer retained". **Copy link** uses the existing clipboard interop.

**4. An object's causal timeline (`ActivityServer.GetCausalTimelineAsync`).** One page (50) of the execution items about an object, newest first by Activity `Executed`, then id. The item ids come from a union, then one join to Activities for ordering and paging, with the total counted from the same set:

| Subject | Item sources |
|---------|--------------|
| Live Connected System Object | items by `ConnectedSystemObjectId` |
| Deleted Connected System Object | items whose change records carry `DeletedConnectedSystemObjectId`; items in its Connected System's Activities with its `ExternalIdSnapshot` (the walk's existing fallback) |
| Live Metaverse Object | items its `MetaverseObjectChanges` name; items of its currently joined Connected System Objects; items with `MetaverseObjectId`; the item that scheduled its deletion (`DeletionScheduledByRunProfileExecutionItemId`, Phase 2-0), which a disconnection can leave with no change record |
| Deleted Metaverse Object | items its change records name through `DeletedMetaverseObjectId`; items with `MetaverseObjectId` |

Raw SQL into a header DTO (the Summary tier: four sources unioned is one statement, not four EF queries). Where change tracking is off, the Metaverse Object sources reduce to its joined objects' items, and the timeline says what it cannot show (PRD Scenario 5).

**5. Nearest causes in one batch (`ActivityServer.GetNearestCausesAsync`).** For a page of item ids: their causal edges in one read (existing), grouped into first-level cohorts by the walk's own `GroupIntoCohorts`, plus the source-import hop for synchronisation items, read for the whole page by a new batched repository method (a lateral join per object, replacing the per-object call in this path only). The result is `CausalChainCohort`s, the walk's own type, so the portal words them with the existing `CausalityCauseWording.Sentence` and the API returns the existing `-Causes` row shape. Each item keeps at most three cohorts and the total.

**6. A Pending Export's queueing runs (`ActivityServer.GetPendingExportQueuedByAsync`).** The runs the Pending Export lists itself (`PendingExport.Stagers()`, at most 50), newest first, each with its nearest causes from (5). One row read (`GetPendingExportCoreAsync`, the row without its graph) and one batch of item entries (`GetCausalTimelineEntriesAsync`); no index needed.

**7. One migration, three filtered indexes.**

| Index | Serves | Write cost |
|-------|--------|------------|
| `MetaverseObjectChanges (DeletedMetaverseObjectId)` where not null | deleted Metaverse Object timeline | Only the deletion re-key update |
| `ConnectedSystemObjectChanges (DeletedConnectedSystemObjectId)` where not null | deleted Connected System Object timeline | Only deletion records |
| `ActivityRunProfileExecutionItems (MetaverseObjectId)` where not null | Metaverse Object timeline | Only export scope review items |

**8. REST and PowerShell.**

| Read | REST | PowerShell |
|------|------|------------|
| Queueing runs | `GET /api/v1/synchronisation/pending-exports/{id}/queued-by` | `Get-JIMPendingExport -Id <guid> -QueuedBy [-All]` |
| Connected System Object timeline | `GET /api/v1/synchronisation/connected-systems/{csId}/connector-space/{csoId}/causal-timeline` | `Get-JIMActivityExecutionItem -ConnectedSystemId <int> -ConnectedSystemObjectId <guid> [-All]` |
| Metaverse Object timeline | `GET /api/v1/metaverse/objects/{id}/causal-timeline` | `Get-JIMActivityExecutionItem -MetaverseObjectId <guid> [-All]` |

Routes mirror each object's existing `change-history` route. The timeline rows are execution items, so they belong to `Get-JIMActivityExecutionItem`, and each row's `Id` pipes straight into `-Causes` or `-Consequences`. Piping an object into the timeline sets uses an `-InputObject` parameter, following `Get-JIMScheduleExecution`, because the cmdlet already owns `-Id` and cannot alias it (`src/JIM.PowerShell/CLAUDE.md`).

### Data flow

```
Pending Export page --Explore--+
Connected System Object page --+--> /causality/... ?trail=...
Metaverse Object page ---------+         |
Item page (Open in explorer) --+         v
                                 CausalityExplorer page
                                   |               |
                         item focus|               |object focus
                                   v               v
                         CausalityItemLoader   GetCausalTimelineAsync
                         (chain, consequences)  + GetNearestCausesAsync
                                   |               |
                                   v               v
                         Lineage (explorer links)  timeline rows --Explore--> item focus
```

## Implementation Phases

Each phase is test-first (red, green, refactor) and builds clean with zero warnings.

### Phase 3-0: Queued by completeness ✅

Merged as #2112, a stack layer below the feature. A later synchronisation changing an object whose export was still queued replaced the Pending Export under a new id, so the earlier run could no longer be found from it, and Phase 2's walk from that run could not reach its own change. Fixed by a stager list on the Pending Export (`StagedByRunProfileExecutionItemIds`, recorded only through `RecordStager`), a replacement that inherits the original's stagers and, while it is still `Pending`, its id, and one queueing edge per stager at export. A Delete superseded by an Update is withdrawn and carries neither. Queued by now reads the list, so the planned outcome index, the plan's largest performance risk, is no longer needed.

### Phase 3a: Indexes and reads ✅

The migration (§7, `CausalTimelineIndexes`) and the repository reads behind §4 to §6, as raw SQL in `ActivitiesRepository.CausalTimeline.cs`:

- `GetCausalTimelinePageAsync(CausalTimelineSubject, skip, take)`: one statement per object kind gathers every source, live and deleted alike (a live object has no deletion record, a deleted one nothing under its own id), then orders, pages and counts in the database. A page past the end still carries the total.
- `GetCausalTimelineEntriesAsync(ids)`: items as `CausalTimelineEntry` rows (the item and its Activity's own snapshots of the run), for Queued by and the trail's labels.
- `GetLatestImportEventsForItemsAsync(ids)`: the source-import hop for a page of items in one lateral read, on the walk's exact rules; a test pins it to the per-item reads, and both now break a same-run tie by id, so they always agree.
- `GetPendingExportCoreAsync(id)`: the Pending Export's row alone, for `Stagers()`.

Delivery notes:

- A deleted Connected System Object's external-ID fallback is bounded twice, each against a real neighbour: to items whose record id is null (a live record reusing the external ID is not it) and to items no later than its deletion (a record created after it and since deleted is not it).
- The live Metaverse Object timeline also takes the item that scheduled its deletion (§4 table).
- The index-use tests EXPLAIN the repository's real statements with sequential scans off. The Connected System Object test seeds 5,000 items of deleted records and analyses first: on empty tables the planner reaches deleted items through the null record ids, a path that would scan every such item in production.
- Measured against PostgreSQL in the sandbox: a 10,000-item object's first page in about 20 ms, a ten-item one's in about 2.5 ms; the test's budget is 300 ms for both. Cost grows with the history, slowly, because the items are ordered by their Activity's time, which no single index holds; a 100,000-item object would take roughly ten times as long, about 200 ms, still inside it.

### Phase 3b: Application reads ✅

`GetCausalTimelineAsync`, `GetNearestCausesAsync`, `GetPendingExportQueuedByAsync`, and the trail's label batch, on `ActivityServer` (a new partial, `ActivityServer.CausalityExplorer.cs`). Unit tests cover cohort grouping matching the walk's, the three-cohort cap, the change-tracking-off reduction, and deleted-object resolution.

Delivery notes:

- **Object headers** (`GetCausalObjectHeadersAsync`) needed four repository reads the plan did not list: a Core read per kind for live objects (`GetConnectedSystemObjectsCoreByIdsAsync`, `GetMetaverseObjectsCoreByIdsAsync`, loading only the values that identify and name an object, so a Group is named without its members) and a deletion-record read per kind for the rest (`GetDeletedCsoChangesByObjectIdsAsync`, `GetDeletedMvoChangesByObjectIdsAsync`, the latest Deleted record per id). Deletion records are read only for the objects not found live. The timeline carries its subject's header, and the trail's label batch (`GetCausalFocusLabelsAsync`) is entries plus headers.
- A row's nearest causes are built from the row itself: a timeline entry carries every field the walk's item summary does, so a page costs one edge read, one export-resolution read and one batched source-import read, never a read per row. The walk and the explorer share one source-import cohort builder, so the two cannot word the hop differently.
- Each row's ending follows the walk's member rule: Resolved where it has causes; otherwise CauseNotRetained for a synchronisation item whose record's deletion severed its timeline, and NoFurtherCauses for the rest.
- Queued by settles a tie between items of one run (same Activity time) by the order the Pending Export recorded them, not by id, and flags a full stager list as possibly missing earlier runs.
- Timeline paging is clamped (page from 1, page size 1 to 100) and widened before multiplying, so a page far past the end reads empty instead of overflowing.

### Phase 3c: Portal groundwork (no visible change) ✅

`ICausalityLinks` and `CausalityPageLinks`, threaded through the Lineage builders and the consequence card. Then a move-only extraction of `CausalityItemLoader` from the item page. The existing UI suite and the item page's own tests stay green throughout, with new tests pinning that the page policy writes today's addresses. The **Open in explorer** button on the causality panel follows.

Delivery notes:

- **The policy travels on `CausalityPageContext.Links`** rather than as a parameter to each builder. The context already reaches every builder, including the context each later run's item is built under when consequences are placed; a parameter would have had to be threaded through every private method of `CausalityModelBuilder`, and a missed one would silently send that link back to the item page. The Lineage model carries the policy on to the cards (`CausalityLineageModel.Links`), which take from it what an item link says and the addresses of a cohort's paged members. A test proves the per-item context carries the policy by failing without it.
- **The policy decides whether a Metaverse Object can be addressed.** The item page's address needs the type's plural name and is withheld without one, as before; an explorer address needs only the id, so the explorer can link a Metaverse Object whose type the item page could not resolve.
- What the policy covers: execution items, Connected System Objects and Metaverse Objects, wherever the Lineage, the summary band, the Timeline or the Table writes them, since all four read the same causality model. Connected Systems, Synchronisation Rules, Pending Exports and deletion records keep their own pages on both hosts.
- `CausalityItemLoader.LoadAsync` returns a `CausalityItemData`: everything the panel takes, plus the headers and object type the item page's own sections show, so the page reads nothing twice. What the item went on to cause stays a separate read, owned by each host.
- Six of the item page's tests reached its private `BuildCausalityPageContext` and `ResolveIdentityId` by reflection, because the page had no render harness. Both methods moved into the loader, so the tests moved with them into `CausalityItemLoaderTests`, with identical assertions; only `ShowsCausality`'s stays with the page.
- Side effect: the page is reused across navigations between items, and kept a previous item's chain, headers or object type where the next item had none. The loader returns fresh values, so that can no longer happen.
- **Open in explorer** moves to Phase 3d with the page it opens, so this phase changes nothing visible.
- Verified at runtime by comparison: every link and line of the panel's text, in all three views, for 20 items chosen to cover each change type, deleted records, chain hops of each edge type and the items with the most consequences, read from the image before this phase and again after it. The 570 links were identical; the only text that changed was the "Checked at" time.

### Phase 3d: The explorer and its entry points ✅

- `CausalityTrail` and `CausalityExplorerLinks` (plain unit tests).
- The explorer page; a trail bar component; an object timeline component; the focus header with its way out (item page, object page, or the deletion record); "Continue from …" on cards the walk stopped at, continuing from the cohort's sole member or, for a larger cohort, from each member's own link.
- **Explore causality** on the Connected System Object and Metaverse Object pages; the **Queued by** panel on the Pending Export page, including its no-recorded-run state.
- bUnit covers the trail bar, the timeline, the Queued by panel, and re-centring. Re-centring is only observable at page level (the router re-parameterising one page instance), so the explorer joins the item page as the second sanctioned page-level exception in `test/CLAUDE.md`.

Delivery notes:

- **Lineage only means no summary band either,** as the mock shows: the focus header already says what is in view. The panel's `LineageOnly` mode offers the Lineage alone and neither reads nor writes the stored view preference, so exploring never changes what the item page opens on.
- **The trail is read from the address,** not supplied as a query parameter, so following a link that changes only the trail is still seen. A read superseded by a later re-centring is discarded rather than landing on the wrong focus.
- **Continue from shows on both hosts.** Where the walk stops at its bound on a single item, upwards on the depth-limit ending and downwards on the further-than-shown card, a "Continue from <name>" link carries on from it: in the explorer it re-centres, on the item page it opens that item's page. A cohort of several already links each member, so it gets none. This is the one visible change to the item page besides **Open in explorer**.
- **Queued by shows what each run did,** as the item list's outcome chips, rather than the mock's per-run "Staged" column: the Pending Export holds the merged changes, not each run's share of them, so a per-run staged change is not recorded to show. Its attribute changes stay listed below it.
- **The Metaverse Object page's button sits in its tab bar,** beside the view switcher, because the page has no action row; it shows on every tab. The Connected System Object page's sits beside Preview Sync, and Open in explorer in the causality panel's band.
- Two pieces became shared components rather than second copies: the Activity page's outcome chips (`OutcomeSummaryChips`) and a row's nearest cause (`CausalityNearestCause`), used by the object timeline and Queued by.
- Showing more of an object's timeline skips any row already shown, since an item recorded after the first page was read moves later rows down one.
- Verified at runtime on the full stack by driving every entry point and re-centring path: item links, object heads and timeline rows re-centre with the trail; crumbs and browser back return; Copy link offers its dialog over plain HTTP; a deleted Connected System Object focus offers its deletion record; a Pending Export staged by two runs and one purged run lists both and counts the third. Layout fixes from the screenshots: the Metaverse Object page's button and the run cells no longer wrap, and trail steps clip later.
- **An item that recorded no object type takes its record's.** The walk-through showed one trail step naming "jimGroup" and the next not: an import updating a record it already holds records only the name. The timeline reads now fall back to the Connected System Object's own type, keeping a recorded one, which can name the Metaverse Object. The timeline page now also reads its columns for the page's rows alone rather than for the whole history it counts.

### Phase 3e: REST and PowerShell ✅

The endpoints and DTOs from §8, with controller tests and an OpenAPI generation run. New parameter sets on `Get-JIMPendingExport` and `Get-JIMActivityExecutionItem`, with Pester tests of binding (piped objects included), paging and output shape.

Delivery notes:

- **Each row carries its nearest cause twice:** as cohorts (`NearestCauses`, at most three, with `NearestCauseCount`) and as `Because`, the one line the portal reads. The ", and N more causes" wording moved into `CausalityCauseWording` (`NearestCauseText`, `MoreCausesSuffix`) so the component and the API share it.
- **The object header decides 404,** for live and deleted objects alike; a Connected System Object read under another Connected System's route is also a 404, as change history's is.
- **No object piping into the timeline sets (a deviation from §8).** A piped Connected System Object or Metaverse Object carries its own id as `Id`, which the item sets already bind by default, so `-InputObject` could only select a timeline through a switch existing for nothing else. The timeline takes the object's ids explicitly; its rows pipe into `-Causes` and `-Consequences`, and a piped row with no switch still fetches the item (both pinned by Pester).
- **`-QueuedBy` has no `-All`** (a deviation from §8): the endpoint is not paged, since a Pending Export keeps a bounded list of stagers. Rows from the Pending Export list pipe in by `Id`; warnings say when runs are no longer retained or earlier ones may be missing.
- Verified at runtime on the full stack: both timelines return worded rows with their header; `-All` pages a six-item timeline at two a page; a timeline row piped into `-Causes` walks its causes; an unknown object, a Connected System Object under the wrong Connected System and an unknown Pending Export each return 404. The sandbox held no Pending Exports at the time, so Queued by's populated response rests on the controller tests and on 3d's runtime check of the panel over the same read.

### Phase 3f: Documentation, integration and close-out ✅

- `docs/configuration/causality.md` gains a section on the explorer, the entry points and the trail. "Causality Explorer" gets a glossary entry and a `vocabulary.psd1` entry (TERM-1). The PowerShell reference gains the new parameters and output tables. Docs conformance and docs coverage run clean.
- Changelog entries.
- Scenario 8 gains a step: after the leavers' Delta Synchronisation and before the Export, a Group's `-QueuedBy` names that synchronisation, and a leaver's account timeline lists its deprovision with the deletion as cause. Run at Medium.
- Runtime check on the full stack against the mock, with screenshots.
- PRD and plan moved to `done/` in the pull request that closes #2100.

Delivery notes:

- **The Scenario 8 step found a real gap, fixed in a stack layer below this one ([#2242](https://github.com/TetronIO/JIM/pull/2242)).** A Connected System Object that its Metaverse Object's deletion had already disconnected is deleted quietly by the confirming synchronisation, and that path wrote no deletion record. Deleting the row also clears the object's id from its execution items, so a deprovisioned account could not be traced back to its history at all: the explorer, the REST API and PowerShell answered 404 for it, and Deleted Objects did not list it. The quiet path now writes the Deleted record (no execution item, so run statistics are unchanged); the explorer needed no change of its own.
- **The account timeline is asserted after the Export, not before it** (a change to the step as planned): the deprovision item the assertion looks for is the Export's, so it does not exist until the Export has run. Queued by is asserted before the Export, as planned.
- Scenario 8 passed at Medium on OpenLDAP with the layer beneath: a Group losing a leaver and the leaver's account deletion each name the apply Synchronisation in `-QueuedBy`, and the deleted account's timeline lists its deprovision because "The Metaverse Object Tina Adams (S8-599) was deleted, so this run deleted the Connected System Object".
- Verified at runtime on the full stack (Scenario 8 at Micro, one leaver deleted from the source directory and driven through to the confirming synchronisation): the quietly deleted Target object's explorer focus lists its four items, from its provisioning to the import confirming its deletion; the deprovision's Explore re-centres on its Lineage, which reads the whole story across both Connected Systems with the trail recording both steps; and View deletion record opens Deleted Objects on its history.
- `main` moved `Get-JIMPendingExport`'s reference to its own generated page while this was in flight, so `-QueuedBy` declares its output type per parameter set and its example sits on the Pending Exports page.

### Phase ordering

3-0 first (done). Then 3a, 3b, 3c, 3d in order; 3e can start once 3b is in. 3f closes. One pull request closes #2100, plus a stack layer below it if 3-0 finds a gap.

## Success Criteria

- Every PRD acceptance criterion is met, each with named evidence in the plan's delivery notes.
- The item page renders exactly as before (its tests unchanged and green); the only addition is the **Open in explorer** button.
- Scenario 8 at Medium passes with the new step.

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Extracting the loader from a 1,219-line page breaks something its tests miss | Move-only commit first, page tests green before and after, then a runtime check of the item page before anything builds on it |
| Re-centring keeps a stale read: a slow load for focus A lands after focus B | Key every read on the focus it was started for and discard any result whose key no longer matches, as the item page does for consequences |
| The Metaverse Object timeline is incomplete without change tracking | Say so on the timeline (PRD Scenario 5) rather than imply completeness |
| A long trail makes unwieldy addresses | Capped at 10 tokens (about 400 characters) |

## Dependencies

- Phases 1 (#1223) and 2 (#1811), both merged.
- No new packages. The clipboard interop and `SegmentedToggle`, `ObjectChip`, `PageInfo` and `SearchField` components already exist.
