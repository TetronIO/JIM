# Causal Provenance: Phase 2 Implementation Plan (Consequences)

- **Status:** Done (Phase 2-0 merged as #2071; Phases 2a to 2e delivered under #1811)
- **Issue:** [#1811](https://github.com/TetronIO/JIM/issues/1811)
- **PRD:** [PRD_CAUSAL_PROVENANCE.md](../../prd/done/PRD_CAUSAL_PROVENANCE.md), Phase 2 requirements 1 to 5, Scenarios 2 and 5
- **Phase 1 plan:** [CAUSAL_PROVENANCE.md](../done/CAUSAL_PROVENANCE.md)
- **UI mock:** [Lineage Consequences](https://claude.ai/artifact/BrXDKc81EodFKcAhSvC9WH) (revision 3)
- **Created:** 2026-10-11

## Overview

Phase 1 walks causal edges upward: what caused this Run Profile Execution Item. Phase 2 walks them downward: what this item went on to cause, so an administrator reviewing a change can see its blast radius. It renders on the Lineage view, not as a separate list: each column carries on below an **After this run** rule, objects the cascade reached get columns of their own, and every branch ends with a named reason (confirmed, waiting on a named run, nothing followed, or further than shown). The same walk is available from the REST API and PowerShell, in both directions, closing Phase 1's parity gap.

## Business Value

Reviewing a source change today shows what happened to one object in one run, and nothing after it. An administrator asking "what did this leaver's end date do?" has to open the next Synchronisation, then the Export, then each Group, by hand, and cannot tell an item whose consequences have not happened yet from one that will never have any. At scale it is worse: one deletion can remove a reference from thousands of Groups.

## Resolved design decisions

| Decision | Resolution |
|---|---|
| Placement | On the Lineage: each column continues below an After this run rule; reached objects get columns. No separate Consequences list (it would restate what the columns name, the duplication #1495 removed) |
| Timeline and Table | Unchanged: they describe this run. The summary band gains an After this run strip, shown in all three views; choosing a pill opens the Lineage at that column |
| Fan-out | A cohort per attribution tuple, as Phase 1's fan-in: one card with an exact count ("4,218 Groups"), members loaded 50 at a time on expand, with a find box |
| Time honesty (PRD Open Question 3) | Every branch ends with a named state. The footer says when the walk ran; **Check again** appears only while a branch is waiting. No polling |
| Read bounds | Bound what the panel reads, never what is recorded: depth bounded at read time, cohorts counted by aggregate query, members paged. A depth stop says "Some consequences go further than shown" |
| Why lines (PRD requirement 5) | A card names the setting that decided it: the export Synchronisation Rule and its Deprovisioning Action, the Deletion Rule and its grace period. Capture landed in [#2061](https://github.com/TetronIO/JIM/pull/2061) |
| Surface parity | REST and PowerShell for both directions, plus cohort member paging. Phase 1's upward chain was portal-only; it gains parity here |
| Authorisation (PRD Open Question 2) | No separate check. Activity pages require the User role and nothing scopes Activities by Connected System, so walking a chain reveals nothing viewing each Activity would not. REST stays Administrator-only like the rest of the Activities API |

## Technical Architecture

### Current state

- `CausalEdges` indexes `EffectRunProfileExecutionItemId`, `CauseRunProfileExecutionItemId` and `CauseMetaverseObjectId`. The two cause-side indexes were added for this phase; nothing reads them yet.
- `CausePendingExportId` is **not** indexed, yet it is the only cause a confirming import's edge names, and Phase 1's upward walk already filters on it (`GetExportExecutionItemIdsByPendingExportIdsAsync`), so that read scans today.
- No edge links an import to the Synchronisation that acted on it: Phase 1's upward source-import hop is a per-object timeline read (`GetLatestImportEventAsync`, by Connected System Object id and `Activity.Executed`).
- **Nothing links a grace-period deletion to the Synchronisation that scheduled it.** Housekeeping deletes the Metaverse Object in a different Activity; the only shared key is the outcomes' `TargetEntityId` (the Metaverse Object id), which is not indexed. Upward, a grace-period chain stops at the housekeeping item; downward, the walk would stop at "deletion scheduled". This is fixed in its own stack layer (see Phase 2-0).
- The Lineage builder keys record objects by **Connected System alone** (`GetRecordColumn`). That is right for the upward chain, where one system holds one story, and wrong for consequences, where Tina's own Glitterband EMEA account and the Groups that referenced her are different objects in the same system.

### The downward hops

| From | To | How it is found | Index |
|---|---|---|---|
| Any item | Items its edges caused (reference recall, standalone deprovision, export execution, generated value revision) | `CausalEdges.CauseRunProfileExecutionItemId = item` | Exists |
| Export execution item | Its confirming import | The Pending Export ids on the item's own effect edges, then `ExportCausedImportConfirmation` edges whose `CausePendingExportId` matches | **New**, filtered on not null |
| Import item | The Synchronisation that acted on it | Per-object timeline: the first synchronisation item for the same Connected System Object whose Activity started after the import's Activity completed | `ConnectedSystemObjectId` exists |
| Synchronisation item that scheduled a deletion | The housekeeping deletion | New edge `MetaverseObjectDeletionScheduleCausedDeletion` (Phase 2-0) | `CauseRunProfileExecutionItemId` exists |
| Any item | Consequences within its own run | Its outcome tree (Disconnected, MvoDeleted, Deprovision queued, Disconnected from target system), already on the item | n/a |

The walk is breadth-first over **cohorts**, one round trip per level, exactly as Phase 1. Each level is one SQL statement: it finds every hop out of the frontier, then counts and groups the results in the database, so a cohort of 4,218 Groups arrives as one row carrying its count, its earliest and latest occurrence, its attribution tuple, the outcome summary its members share (so "Exported to 4,216" and "Export failed on 2" split into two cohorts), and its first page of members. The next level's frontier comes back on the same row as an id array, the only per-member data that leaves the database, capped per level; past the cap the walk says it went further than shown rather than loading more.

(Revised while building 2a: the plan first said the frontier would be a SQL predicate over the previous level rather than an id list. Carrying ids keeps each level a single plain statement that can be tested and explained on its own, where a predicate grows with depth, and the ids are the only thing carried.)

**Waiting branches are consequences, not labels.** A sync item can stage exports to three systems, two of which have run: the item continues, and the third is still waiting. So outstanding work is read as a hop of its own, live, in the same statement: a Pending Export the item staged or executed that still exists (its status gives Waiting for an Export, Waiting for a retry, Needs attention or Waiting for a confirming import), and a deletion the item scheduled that the Metaverse Object still carries (the 2-0 column, so a cancelled schedule is not shown as pending). An item with no hop at all ends as Waiting for a Synchronisation, Nothing followed (an import) or No further consequences.

### Branch endings

Every branch ends in one of these, resolved in the same pass. The same set is returned by the API, so a script can tell "done" from "not yet".

| Ending | When | Shown as |
|---|---|---|
| Confirmed | The branch reaches a confirming import | Folded "Confirmed by the next import" line |
| Waiting for a Synchronisation | Import item, and no Synchronisation of its Connected System has started since the import completed | Waiting card naming the system |
| Nothing followed | Import item, a Synchronisation has run since, and it recorded no item for the object | Settled note, no Check again |
| Waiting for an Export | A Pending Export the branch queued still exists, status Pending or Executing | Waiting card, links the Pending Export(s) |
| Waiting for a retry | Status ExportNotConfirmed, with `NextRetryAt` | Waiting card with the retry time |
| Needs attention | Status Parked or Failed | Error-toned card linking the Pending Export; not "waiting", since nothing will happen without someone |
| Waiting for a confirming import | Exported, no confirmation edge yet | Waiting card naming the system |
| Deletion scheduled | MvoDeletionScheduled with no housekeeping deletion yet | Waiting card with the due date |
| Further than shown | Depth bound reached | Foot of the canvas: "Some consequences go further than shown" |
| No further consequences | The item's type causes nothing (an import that only confirmed an export included), or nothing it could cause was recorded | No After this run rule at all |

Live Pending Export status comes from the ids already on the outcomes (`TargetEntityId`), read by primary key, extending `GetExistingPendingExportIdsAsync` to return status and `NextRetryAt`. No index on `QueuedByRunProfileExecutionItemId` is needed.

### Model

New DTOs in `JIM.Models/Activities/DTOs`, mirroring Phase 1's shape so the two directions read alike:

- `CausalConsequences`: `RunProfileExecutionItemId`, `Cohorts`, `IsTruncatedByDepth`, `CheckedAt`, `HasWaitingBranches`.
- `CausalConsequenceCohort`: the attribution tuple (edge or hop kind, reason, Connected System, Synchronisation Rule, object type and plural, attribute), `MemberCount`, `FirstOccurred`, `LastOccurred`, `Outcomes` (the shared outcome summary, as `SyncOutcomeNode`s, so a consequence item's own tree places cards on the right objects), `Members` (first page), `Ending`, `Consequences` (the next level), and an opaque `CohortKey` for paging.
- `CausalConsequenceEnding`: the enum in the table above, plus `ConnectedSystemName`, `DueAt` and Pending Export ids where they apply.

The `CohortKey` encodes the cohort's path from the root (each level's tuple), so member paging is stateless: the server re-derives the predicate from the key rather than holding state between requests.

## Implementation Phases

### Phase 2-0: Grace-period scheduling link (stack layer below the feature) ✅

Discovered work, isolated per the stacked-PR doctrine: it is a capture gap in its own right (Phase 1's upward walk stops at the housekeeping item for exactly the same reason), and the downward walk needs it.

- New nullable `MetaverseObject.DeletionScheduledByRunProfileExecutionItemId`: an unconstrained snapshot scalar like `PendingExport.QueuedByRunProfileExecutionItemId`, set wherever MvoDeletionScheduled is recorded. The item id is assigned at the seam rather than read, because ids are assigned at flush (the Phase 1b lesson).
- New append-only `CausalEdgeType.MetaverseObjectDeletionScheduleCausedDeletion` (ordinal 5) written by housekeeping: effect = the housekeeping deletion item and its MvoDeleted outcome, cause = the scheduling item. Ordinal pinning test.
- Raw SQL guard: the new column goes in every Metaverse Object `*BulkColumns` list, with its `BulkInsertColumnCompletenessTests` entry and `RequiresPostgres` round-trip test. Migration generated after the latest `main` migration.
- The upward walk gains the hop for free; assert it in `CausalChainWalkTests`.
- Runtime: Scenario 004 Test 4 (grace period) asserts the edge.

### Phase 2a: Index and downward reads ✅

- Migration: filtered index on `CausalEdges.CausePendingExportId` (`WHERE "CausePendingExportId" IS NOT NULL`), which also fixes Phase 1's scanning read.
- Repository reads in `ActivitiesRepository`, raw SQL: `GetCausalConsequenceLevelAsync` (one level: edge hops by cause item, confirmations via the queueing edges' Pending Export ids, the next synchronisation per Connected System Object or external ID after an import, outstanding Pending Exports, pending scheduled deletions, and the endings of frontier items with no hop, aggregated per cohort with the first member page and the next frontier) and `GetCausalConsequenceCohortMembersAsync` (one cohort's members, paged, with an optional name filter). Enum values are parameters, never literals.
- `RequiresPostgres` tests for each read, including an `EXPLAIN` assertion that the cause-side reads use their indexes on a seeded table.

### Phase 2b: Application walk ✅

- `ActivityServer.GetCausalConsequencesAsync(Guid itemId, int maxDepth = 6)` and `GetCausalConsequenceCohortMembersAsync(Guid itemId, string cohortKey, int page, int pageSize)`.
- Ending resolution as tabled above; live Pending Export status read once per walk.
- Unit tests beside `CausalChainWalkTests`, from the PRD's example: import with no Synchronisation yet, nothing followed, mid-cascade (exports queued), complete, 4,218-member cohort with a failed-export split, depth bound, confirming import (no consequences), grace-period deletion via the 2-0 edge, retention purge mid-chain. One round trip per level asserted, as Phase 1 does.

### Phase 2c: Lineage UI ✅

- `CausalityLineageModelBuilder` takes a `CausalConsequences` alongside the `CausalChain`. Consequence objects are keyed by object (or cohort), not by system, so the account and the Groups are separate objects; the upward placement is unchanged.
- New card shapes beside `CausalityLineageChainCard`: the consequence card (operation chip, sentence, why line, run chip and time, activity link, members toggle with paging and a find box), the waiting card, the settled note and the folded confirmation.
- The After this run rule appears only on objects of item types that can cause consequences.
- Summary band: a second, outlined strip of After this run pills, distinct from this run's own pills.
- Footer: "Checked at …", branch state, and **Check again** only while a branch is waiting.
- The "deleted after this run" note becomes the Deleted card, which names the Deletion Rule and links the deletion record. The note stays as the fallback when the deleting item is no longer retained.
- The page loads consequences after first render, so the panel never waits on the walk.
- bUnit tests for the view (each ending, members paging, after-this-run rule present or absent), plain tests for the builder (column keying, placement, pills). Screenshots of the five mock moments published as an Artifact for review.

### Phase 2d: REST and PowerShell ✅

- `GET /api/v1/activities/items/{id}/causes` (Phase 1 parity), `GET /api/v1/activities/items/{id}/consequences`, and `GET /api/v1/activities/items/{id}/consequences/cohorts/{cohortKey}/members?page=&pageSize=` returning `PaginatedResponse<T>`. Administrator-only, like the controller. DTOs with `FromModel`, XML docs, controller tests including the route-and-role assertion pattern from `MetaverseControllerConnectionsTests`.
- OpenAPI generated with `scripts/Generate-OpenApiDoc.ps1` to catch route-binding and schema-depth failures before CI.
- PowerShell: `Get-JIMActivityExecutionItem -Causes` and `-Consequences` (new parameter sets, flattened to one row per cohort with Level, Change, Count, Object, Connected System, Run, Ending), and `Get-JIMCausalCohortMember -ExecutionItemId -CohortKey [-All]` through `Invoke-JIMPagedFetch`. Pester tests on the requests built and the pipeline binding.

### Phase 2e: Documentation, integration and close-out ✅

- `docs/configuration/activities.md`: a "What it went on to cause" section beside "Why it happened", the ending states, and Check again. `docs/powershell/activities.md`: the new parameters and cmdlet, with Output tables checked against the DTOs.
- `CHANGELOG.md` ✨ entry.
- Integration: Scenario 008's leaver cohort asserts the downward walk from the import reaches the four Groups and their confirmations (the PRD's acceptance criterion for traversal in both directions), at Medium.
- Runtime check of the five mock moments on the full stack.
- PRD and this plan to `done/` in the PR that closes #1811, with the acceptance criteria ticked.

### Phase ordering

2-0 is its own layer and lands first. 2a then 2b are sequential. 2c and 2d both need 2b and can overlap. 2e needs all of them.

## Delivery notes

Agreed with the product owner at the runtime review of Phase 2c (2026-10-11), and recorded here because they depart from the mock:

- The objects a cascade reaches are separate objects stacked in their side's column, not columns of their own. The Lineage keeps one column per side of the Metaverse so the canvas stays at most four columns wide however many Connected Systems a cascade reaches.
- A Synchronisation's Disconnected outcome sits on the Metaverse Object, where that item's own Lineage places it, so one event never appears in two places depending on which item is opened.
- An import that only confirmed an export ends with no further consequences rather than following the import-to-Synchronisation hop, which the approved mock's confirming-import entry point required and the first runtime pass showed was missing.
- The documentation moved to its own Causality page: the Activities page was over its word budget, and the standard's remedy is a split rather than a trim.

## Success Criteria

- Scenario 2: from Tina's import, the Lineage shows the scope loss, the deletion, her account's deprovisioning, and one "Removed Tina Adams from Static Members on 4 Groups" cohort expandable to the four Groups, at runtime against Scenario 008's fixture.
- Scenario 5: the same item shows "Waiting for the next Synchronisation" before the cascade and the full chain after it, with Check again only while a branch waits.
- A 4,000-member cohort renders with an exact count without loading its members, and pages them on request.
- The REST API and PowerShell return the same cohorts and endings as the portal, in both directions.
- `dotnet build JIM.sln` and `dotnet test JIM.sln` clean; Scenarios 004 and 008 green at the templates noted.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Fan-out at scale: a cohort of thousands, then thousands of exports and confirmations beneath it | Levels as SQL predicates, not id lists; counts by aggregate; members paged; a test asserting no member materialisation beyond the page |
| "Nothing followed" misread when a Synchronisation was running across the import | Only a Synchronisation that **started** after the import's Activity completed counts; covered by a test at the boundary |
| The import-to-Synchronisation hop picks the wrong cycle when an object is imported twice before a Synchronisation | Take the first synchronisation item after the import; both imports then share it, which is correct, since one Synchronisation acted on both changes |
| Lineage column keying change regresses the upward view | Consequence objects get the new keying; upward placement is untouched and its tests stay as they are |
| Two persisted enums or columns added | Ordinal pinning and raw SQL column guards in the same change (Phase 2-0) |

## Dependencies

- [#2061](https://github.com/TetronIO/JIM/pull/2061): the deciding rule on deprovisioning outcomes and the account left in place on a deletion (the bottom stack layer).
- Phase 2-0, its own layer between #2061 and this feature.
