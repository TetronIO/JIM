# Execution Item Outcomes and Changes over REST and PowerShell - Implementation Plan

- **Status:** Doing (Phases 1-5 complete)
- **Issue:** [#2033](https://github.com/TetronIO/JIM/issues/2033)
- **Follows:** #2032 (`GET /activities/items/{id}`, `Get-JIMActivityExecutionItem`)

## Overview

The portal's item page (`/activity/item/{id}`) shows an execution item's outcome tree and the attribute changes recorded with it. REST and PowerShell return only the flattened `OutcomeSummary`. This plan adds both as opt-in sections of the existing item detail, keeping the default response exactly as #2032 left it.

## What already exists (and changes the issue's scope)

| Need | Already shipped | Consequence |
|---|---|---|
| Causal edges | `GET /activities/items/{id}/causes`, `/consequences`, cohort members; `Get-JIMActivityExecutionItem -Causes / -Consequences`, `Get-JIMCausalCohortMember` | **Out of scope.** Causality parity is already complete; `include=causality` would duplicate it. |
| Outcome tree DTO | `SyncOutcomeNodeDto` (Sync Preview's speculative tree, nested `Children`, documented as "the same shape so a caller can render both through the same component"), generating cleanly in the OpenAPI document today | **Reuse it, nested.** The generator only fails on cycles through types it inlines; a registered DTO class referencing itself resolves to a `$ref`, as this one already proves. |
| Attribute change DTOs | `MvoChangeHistoryDto` / `CsoChangeHistoryDto` (with attribute and value change children), served by `.../objects/{id}/change-history` | **Reuse them.** Same shapes a script already reads from object history; only a by-item filter is new. |

Today a script can find an item's changes only by paging the object's whole change history and filtering on `ActivityRunProfileExecutionItemId`, which also misses changes on a since-deleted object. Direct access is still worth adding.

## Design

### REST

`GET /api/v1/activities/items/{id}?include=outcomes,changes`

- `include` is a comma-separated list, case-insensitive; an unknown value returns `400` naming the valid ones (fail loudly rather than silently returning less).
- No `include`: the response and its database work are unchanged.
- New nullable properties on `ActivityRunProfileExecutionItemDetailDto`, null unless requested:
  - `Outcomes: List<SyncOutcomeNodeDto>?`: root outcomes, ordered by `Ordinal`, with children nested.
  - `Changes: ExecutionItemChanges?` (a JIM.Models DTO, as `MvoChangeHistoryDto` is) with:
    - `MetaverseObjectChange: MvoChangeHistoryDto?` (synchronisation items)
    - `ConnectedSystemObjectChange: CsoChangeHistoryDto?` (import and export items)
    - `StagedExportChanges: List<CsoChangeHistoryDto>`: the per-outcome snapshots `PendingExportCreated` outcomes record of what they staged (the Pending Export itself is deleted on confirmation, so this snapshot is the only lasting record).
- `SyncOutcomeNodeDto` gains `ConnectedSystemObjectChangeId: Guid?`, linking a `PendingExportCreated` outcome to its entry in `StagedExportChanges`. Sync Preview leaves it null.

### PowerShell

`Get-JIMActivityExecutionItem -Id <guid> [-IncludeOutcomes] [-IncludeChanges]` (both in the `Item` parameter set; they compose). Output stays `JIM.ActivityRunProfileExecutionItemDetailDto`. The nested tree is filtered with a short recursive walk, documented as an example in `docs/powershell/activities.md`.

### Application and data

- `ActivityServer.GetExecutionItemOutcomesAsync(itemId)`: one query over `ActivityRunProfileExecutionItemSyncOutcomes` for the item, built into a tree in memory (same approach as the portal's loader). No `.Include` of the full item graph.
- `MetaverseRepository` / `ConnectedSystemRepository`: extract the existing flat change-history projections into a shared expression, then add `GetMvoChangeForExecutionItemAsync(itemId)` and `GetCsoChangesForExecutionItemAsync(itemId)` (the item's own CSO change plus the outcome-linked snapshots). One projection means object history and item detail can never disagree on a change's shape.
- The controller calls only the sections requested.

## Implementation Phases

1. **Outcomes (TDD)** ✅: flat repository read (`GetSyncOutcomesAsync`) and tree build (`ActivityServer.GetSyncOutcomeTreeAsync`, mapping through the existing `SyncOutcomeNode.FromSyncOutcome`); `include` parsing with `400` on unknown values; controller tests (default unchanged and not read, outcomes nested and ordered, none recorded reads as empty, unknown include); real-PostgreSQL test for the read on a no-tracking context. OpenAPI generation confirmed: `outcomes` and `children` resolve to a `$ref`. `ConnectedSystemObjectChangeId` moved to phase 2, where the changes it points at arrive.
2. **Changes (TDD)** ✅: `SyncOutcomeNode`/`SyncOutcomeNodeDto` gained `ConnectedSystemObjectChangeId` (the preview fidelity tests are unaffected); both change-history projections extracted into shared expressions (`MvoChangeHistoryProjection`, `CsoChangeHistoryProjection`), with object history's existing tests green unchanged; by-item reads (`GetMvoChangeForExecutionItemAsync`, `GetCsoChangeForExecutionItemAsync`, `GetCsoChangesAsync`), Generated Value marking shared with object history; `ExecutionItemChanges` (JIM.Models) returned as is, dates canonicalised as object history's are. Real-PostgreSQL tests cover a synchronisation item, an import whose object has since been deleted, and an item with no changes.
3. **OpenAPI** ✅: done alongside phases 1 and 2; `outcomes`, `children` and `changes` all resolve to a `$ref`, and ACC-3/COV-4 pass.
4. **PowerShell** ✅: `-IncludeOutcomes` and `-IncludeChanges` in the `Item` set only, composing into one `include`; Pester tests for each combination's request, the set membership and the nested wire shape; two help examples (Metaverse Object changes, a recursive walk of the outcome tree). The generated reference for `execution-items.md` (and `sync-preview.md`, whose outcome node gained `ConnectedSystemObjectChangeId`) is regenerated, with an explicit `With -Id` output label so the item's output is not shown as needing the switches.
5. **Docs and changelog** ✅: `docs/powershell/execution-items.md` (the cmdlet's page; the issue named `activities.md`) explains `Outcomes` and `Changes`, with examples for the Metaverse Object changes and for joining each staged Pending Export outcome to its change; its generated Output table names the DTOs' real properties. The REST reference is generated from the OpenAPI document. `CHANGELOG.md` `✨` entry.
6. **Runtime check**: light stack, run an import and a synchronisation, read one item each way, compare against the portal page.

## Risks

- **Changes payload size.** An item touching a large multi-valued attribute (for example group membership) can carry thousands of value changes. Mitigation: it is opt-in; document it. Revisit with a cap only if a real case shows it matters.
- **Projection refactor regressing object history.** Mitigation: phase 2 extracts first and runs the existing change-history tests before adding anything.
- **In-memory provider masking a missing join.** Mitigation: database tests for both new queries, per `test/CLAUDE.md`.

## Success Criteria

- An item's outcome tree and attribute changes are readable through REST and PowerShell, matching the portal page.
- A request without `include` produces the same response and the same queries as before.
- OpenAPI generation succeeds; API, database and Pester tests pass; docs name the DTOs' real properties.
