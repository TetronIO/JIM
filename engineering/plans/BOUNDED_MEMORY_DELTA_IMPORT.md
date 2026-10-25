# Bounded-Memory Delta Import

- **Status:** Planned
- **Issue:** [#917](https://github.com/TetronIO/JIM/issues/917)
- **PRD:** [PRD_BOUNDED_MEMORY_DELTA_IMPORT.md](../prd/PRD_BOUNDED_MEMORY_DELTA_IMPORT.md)

## Overview

Make a Delta Import save each page's updates as it goes, instead of holding every page's Connected System Objects until the last page has been read. Updates to objects JIM already holds are where the memory goes. A changed group is hydrated with every member, and a busy change window can touch thousands of large groups. Saving updates per page sets the peak Worker memory by one page rather than by the change window.

Creates are still held until the end of the run, as today. That keeps net change: an object created and deleted within one run never appears in JIM, so it is never provisioned downstream. This holds even when the deletes are withheld by the Run Profile's limits or the run stops part-way.

Every other whole-run behaviour keeps its meaning: deletion limits, a later page's copy superseding an earlier one, reference resolution, Pending Export reconciliation and the watermark rule.

Full Import is out of scope; [#2521](https://github.com/TetronIO/JIM/issues/2521) reuses the per-page staging this plan builds.

## Business Value

- An administrator can size the Worker for their steady state; a large change window takes longer but no longer needs more memory.
- A Delta Import stopped by the memory guard (#2507), a failure or a cancellation keeps the updates it saved, so the next run finishes the job instead of starting again.
- A long change window no longer has a single, run-sized save phase at the end, during which the Activity looks stalled.

## Technical Architecture

### Current state

All in `src/JIM.Worker/Processors/SyncImportTaskProcessor.cs`, `PerformImportAsync`:

```
for each Connector page:
    ProcessImportObjectsAsync(page)      hydrate matched CSOs (all attribute values), diff, build creates/updates,
                                         RPEIs; appends to run-wide lists
after the last page:
    WithholdConnectorReportedDeletesOverTheLimitsAsync   #2089: all deletes stand, or none
    ResolveReferencesAsync               in-memory lookups over every create and update, then one DB batch
    save creates (batches of 2,000), FixupCrossBatchReferenceIdsAsync, save updates (batches of 2,000)
    ReconcilePendingExportsAsync         over every updated CSO id
    JudgeUnreconciledInitialPasswordHoldsAsync
    flush remaining RPEIs, record watermark (#1868), Activity message
```

What stays resident until the save phase:

| Run-wide state | Grows with | Size per item |
|---|---|---|
| `connectedSystemObjectsToBeUpdated` | changed objects | the whole hydrated graph: every attribute value, plus pending additions and removals. A long-tail group is hundreds of thousands of values. |
| `connectedSystemObjectsToBeCreated` | new objects | the new object's attribute values |
| `_activityRunProfileExecutionItems` | changed objects | an RPEI, and its change graph once saved |
| `_reconciliationRpeiLookup` | updated objects | the RPEI, kept after its flush until reconciliation |
| `_connectorReportedDeletes` | deletes | a reference to the hydrated CSO (all its values) |
| `_earlierPageCopies` (#2074) | objects seen on more than one page | the held CSO and its RPEI |
| `externalIdsImported`, `crossPageSeenExternalIds` | objects read | about 100 to 200 bytes; tolerable |
| `_csoExternalIdLookup` | every CSO in the Connected System | about 200 bytes; loaded once, never changed |

**One finding shapes the design.** The OpenLDAP accesslog source removes repeated changes to the same DN only within a page (`LdapAccesslogDeltaSource`, `processedDns`), and it fetches the object's current state for each. A group modified across 40 pages therefore arrives 40 times, each time with every member. Today the copies supersede one another in memory (#2074) and cost only the incoming payload. Once updates are saved per page, each copy would reload and re-compare the group from the database, unless the run remembers what it saved.

### Proposed design

```
for each Connector page:
    ProcessImportObjectsAsync(page)      unchanged shape; creates go to the held list as today,
                                         updates to the page's list; uses the staged-update register below
    StagePageAsync(page updates):
        resolve references (in-memory over the page and the held creates, then DB)
        save the page's updates (existing batch writer), stamp import state, flush RPEIs
        reconcile Pending Exports for the page's updated CSO ids
        record every staged update in the staged-update register
        release the page's objects (ReleaseHydratedAttributeValues, clear the list, clear the tracker)
after the last page:
    WithholdConnectorReportedDeletesOverTheLimitsAsync   unchanged decision, over deferred delete records
    resolve the held creates' references, save them (existing path, unchanged)
    apply the deletes that stand, in batches             re-hydrating only what the delete needs
    sweep staged updates' still-unresolved references    one set-based pass, same rules as ResolveReferencesAsync
    report what is still unresolved on the items that hold it
    JudgeUnreconciledInitialPasswordHoldsAsync           over the run's reconciled ids
    record watermark (#1868), Activity message
```

Run-wide state after the change:

| Run-wide state | Holds |
|---|---|
| Held creates | as today: each new object and its values (kept for net change; see invariant 2) |
| Staged-update register | duplicate key → staged CSO id, outcome counted, content hash of the staged copy |
| Deferred deletes | CSO id, status and last-updated before, RPEI id |
| Reconciled ids | CSO ids, for `JudgeUnreconciledInitialPasswordHoldsAsync` |
| Items with unresolved references | RPEI id and CSO id, for the end-of-run report |
| `_csoExternalIdLookup` | as today, never changed mid-run |

### The invariants, one by one

**1. Deletion limits (#2089).** Deletes are not applied page by page. Each delete is recorded as a small deferred record, and the hydrated CSO is released with its page. After the last page, the existing decision runs over the deferred records. If the deletes stand, they are applied in batches. Phase 2 confirms what the deletion's change record needs from the object. If it needs only the header, nothing is re-hydrated. If it needs the values, the batch re-hydrates them. Updates were never subject to the limits, so saving them early changes nothing. When deletes are withheld, the run keeps its watermark (as today), and the next run re-reads updates that are already staged. The staged diff makes that a no-op.

**2. Net change, and a later page's copy (#2074).** Creates are held in memory until the end of the run, so every case involving a create behaves exactly as today. A later copy of a held create replaces it, and a later delete of a held create means it is never created. Neither depends on the deletion limits or on the run completing.

Staging an object's create page by page was considered and rejected. A completed run would leave an Obsolete object that should never have existed. A run whose deletes were withheld, or that stopped before reading the delete, would leave a normal object that the next synchronisation provisions downstream.

For updates, the register replaces `_earlierPageCopies` once an earlier copy has been saved:

| Earlier copy | Later copy | Behaviour |
|---|---|---|
| staged update | identical content hash | Counted unchanged; no hydration, no item. This is the busy-group case above. |
| staged update | different update | Hydrate the staged CSO from the database and compare as usual. A second item records the change from the saved state. The object is counted once, as updated. |
| staged update | delete | A deferred delete of the staged CSO, subject to the limits as usual. The object existed before the run, so no net change is lost: it was updated, then deleted. |
| deferred delete | re-insert or update | Withdraw the deferred delete (it is still in memory), as today, and apply the later copy to the object. |
| held create | anything | In memory, exactly as today. |

The content hash is the existing `ImportContentHashCalculator.CalculateContentHash`. It is trusted only against a copy this run computed and staged itself, never against a stamp from an earlier run, so the D7 rule ("never trust a Delta Import payload's hash") is not weakened.

**3. Matching later copies.** Because creates are not saved before the end of the run, `_csoExternalIdLookup` stays exactly as it is documented: loaded once, never changed. A later copy of a held create is found through the existing cross-page detection (`crossPageSeenExternalIds` and the in-memory supersede). A later copy of a staged update is found through the lookup as usual, since the object existed before the run.

**4. References.** Each page resolves its updates' references in memory against the page's own updates and every held create, then queries the database for everything else. The query is the existing Phase 2 in `ResolveReferencesAsync`, which keeps #1285's type scoping and #2051's case rules.

A staged update can reference a held create that has no row yet. It cannot be saved as a foreign key, so it is saved unresolved (`UnresolvedReferenceValue`, null `ReferenceValueId`), which the schema already allows, and the item that holds it is noted.

After the creates are saved at the end, a sweep resolves those values. It must use the same semantics as `ResolveReferencesAsync` Phase 2, not `FixupCrossBatchReferenceIdsAsync`: that query matches secondary external IDs only, ignores a declared target type, and spans the whole Connected System. Whatever is still unresolved is reported on its saved item through a raw RPEI update (`BulkUpdateRpeiFieldsRawAsync` pattern), honouring `UnresolvedReferenceHandling`. Change records written for a value later resolved need the same treatment `FixupCrossBatchChangeRecordReferenceIdsAsync` gives them today.

The held creates' own references resolve at the end exactly as today: in memory against each other, then the database, where the staged updates already are.

**5. Pending Export reconciliation.** `ReconcilePendingExportsAsync` already reloads each CSO's values from the database per page, using only the ids it is given. It runs per staged page, using the page's ids and its RPEI lookup, which is then released. Creates cannot have Pending Exports, as today. The background pre-load of Pending Exports stays whole-run (it is keyed by CSO and already resident). An entry is removed once a page has reconciled it, so a later copy of the same object reconciles against what is left. That is what two consecutive Delta Imports do today.

**6. Counts.** `_updatedObjectCount` and the other counters stay counts of distinct objects. The register records how each staged update was counted, so a later copy moves it between figures rather than adding a second count, which is what `SupersedeEarlierPageCopy` does today. The end-of-run "counted twice" warning stays as the guard.

**7. Watermark (#1868).** Unchanged: recorded only after the last page is staged and the end-of-run steps succeed. A stop at any point keeps the starting watermark.

**8. A stopped run.** Today, cancellation discards everything ("nothing has been persisted yet"). After this change, staged updates stay staged. Held creates and deferred deletes are dropped unapplied, exactly as today, and so are the reference sweep and the watermark. This happens on cancellation, a memory-guard stop and a failure alike.

The Activity message says how many updates were saved, that creates and deletes were not applied, and that the next Delta Import reads from the same point. A staged reference left unresolved by a stop is resolved by the next run's sweep. That sweep covers every unresolved value the Connected System's objects hold, not only that run's.

A stopped run can leave an object with an intermediate update (page 1's change, without page 3's). The object existed before the run and the next run applies the rest, so this is the same exposure as a change window ending between the two changes. It never creates or removes an object.

### Value budget within a page (conditional)

The Connector bounds a page by entry count, not size: one LDAP page can hold several 495k-member groups. Splitting a page into sub-batches by attribute value count (summed from the import objects before hydration) would cap the hydrated graphs at a budget. It cannot reduce the incoming page, which the Connector already holds. Phase 5 adds it only if Phase 3's measurements show a single page is still the problem.

## Implementation Phases

Each phase follows TDD: write failing tests first, then implement, then keep both `dotnet build JIM.sln` and `dotnet test JIM.sln` clean. Phases 1 to 4 are one PR or one stack, and so is Phase 5 if it is needed.

### Phase 0: Baseline (before implementation)

- Run Scale750k40kGroups on `main` once the host's drive issue is resolved (this was already planned). Record the Delta Import's peak Worker memory from the docker-stats CSV and the save-phase GC heap log lines.
- Run Scenario 8 at Scale100k5kGroups for the same figures, which are comparable with the July numbers in #917.
- Add a Worker test harness measurement: a fake Connector producing N pages of large group updates, recording `GC.GetTotalMemory(true)` after each page. Today's figure should grow with N. This is the red test for the goal.

### Phase 1: Separate staging from the end of the run (no behaviour change)

- Extract the update save, stamp, flush and reconcile steps from `PerformImportAsync` into `StagePageAsync(updates)`, still called once after the last page. The create save stays where it is.
- Change the whole-run consumers to take ids: `ReconcilePendingExportsAsync` and `JudgeUnreconciledInitialPasswordHoldsAsync` already use only `cso.Id`.
- Introduce the staged-update register and the deferred-delete record types (in `JIM.Models` if they cross a boundary, otherwise private records as `EarlierPageCopy` is today).
- Every existing test passes unchanged.

### Phase 2: Deferred deletes

- Record Connector-reported deletes as deferred records. The decision in `WithholdConnectorReportedDeletesOverTheLimitsAsync` runs over the records, and the deletes that stand are applied in batches after it.
- Confirm in `ConnectedSystemServer.UpdateConnectedSystemObjectsAsync` and the change-record builder what a deletion's change record needs from the hydrated object, and hydrate only that.
- Tests: `DeltaImportDeletionLimitTests` pass unchanged; a new test shows the hydrated CSO of a deferred delete is released before the next page.

### Phase 3: Stage each page's updates

- Call `StagePageAsync` after each page of a call-based Delta Import. File-based imports and Full Import keep calling it once.
- Replace `_earlierPageCopies` for staged updates with the register, following the table in invariant 2. Keep in-memory supersede for held creates and deferred deletes.
- Count distinct objects.
- Tests:
  - Each row of the invariant 2 table.
  - Created on page 1 and deleted on page 3 leaves nothing in JIM, in three cases: the run completes, its deletes are withheld, and it is stopped after page 2.
  - A busy group on many pages is hydrated once.
  - Counts across pages.
  - `ImportDuplicateExternalIdTests` and `ImportObjectCountReportingTests` pass. Tests asserting one item for an update superseded across pages are updated deliberately (decision 1) and listed in the PR.
- `RequiresPostgres` test: two pages staged on one context with tracked instances from page 1 still present. This guards the "raw SQL writes must fix up or detach tracked instances" rule in `src/CLAUDE.md`.
- The Phase 0 harness test turns green: peak memory flat in N.

### Phase 4: References across pages and the stopped run

- Note items saved with unresolved references. After the creates are saved, sweep with Phase 2 semantics, report what is left on the saved items, and fix up change records.
- New repository method for the sweep, scoped to the Connected System's unresolved values and set-based, with a `RequiresPostgres` round-trip test. Raw SQL follows the bulk column rules; the RPEI update uses `RpeiBulkColumns`.
- Stopped-run behaviour and Activity wording (`ImportOutcomeMessage`). Tests for cancel, failure and memory-guard stop after page k:
  - The watermark is kept.
  - Updates up to page k are staged.
  - No create or delete is applied.
  - The next run produces no duplicate change records.
- `CrossPageReferenceProvisioningTests` pass. Add a test for a staged update referencing a held create (a group on page 2 gains a member created on page 7).

### Phase 5: Value budget within a page (only if measurements call for it)

- Sub-batch a page's import objects by summed value count before hydration; default budget chosen from Phase 3 measurements.
- Tests: a page with several very large groups stays under the budget's hydrated size.

### Phase 6: Validation, docs and changelog

- Integration:
  - Scenario 8 at Scale100k5kGroups and Scale750k40kGroups, with peaks compared against Phase 0.
  - Scenario 4 (deletion rules).
  - The full parallel suite at Small or Medium for regressions.
- Docs, per the PRD's Documentation Impact table.
- Changelog: ⚡ Delta Import memory now depends on the page size, not the size of the change window. 🔄 A Delta Import stopped part-way keeps the updates it had already saved.
- Move the PRD and plan to `done/` in the PR that closes #917.

## Success Criteria

- The Phase 0 harness test shows peak memory within 25% between 1x and 5x the update count.
- Scale100k5kGroups and Scale750k40kGroups Delta Import peaks are lower than the Phase 0 baseline; the run passes on the same host.
- Delta Import wall time for a single-page window is within 5% of today's; for multi-page windows, report the change. A small per-page cost is acceptable.
- Net change is unchanged: created-then-deleted within a run leaves nothing in JIM in every case above.
- No regression in any Worker test or integration scenario beyond the deliberate decision 1 change.

## Benefits

- **Performance:** memory sized by page for the updates that dominate it; no run-sized update save at the end; busy objects across pages compared once.
- **Resilience:** stopped runs keep their updates; the memory guard's Stop becomes a pause rather than a total loss.
- **Architecture:** Delta Import follows the per-page pattern synchronisation adopted in #451, and #2521 gets a ready mechanism.

## Dependencies

- No new packages. No schema change expected; if the sweep needs an index on unresolved reference values, it is an additive migration (`AddFunctionalIndexesForCrossBatchReferenceFixup` may already cover it).
- Merged: #2503, #2507.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Memory still grows with creates | Accepted to keep net change. Creates are usually small (about 250 MB for 50k new users). A window creating thousands of very large objects is a first-load pattern, covered by #2521. The Phase 0 baseline records the create share. |
| Reference sweep resolves differently from in-memory resolution | It reuses `ResolveReferencesAsync` Phase 2 logic, not the coarse fixup query; tests per #1285 and #2051 case |
| Hot path regressions from per-page saves on many small pages | The existing batch writers are used unchanged; measure in Phase 3; a page smaller than a batch costs one round of writes |
| Tracked-instance faults on the long-lived context across staged pages | `ClearChangeTracker` after each page, as the update batches already do; `RequiresPostgres` multi-page test |
| A synchronisation run between a stopped import and the next sees an intermediate update | Only to objects that existed before the run; no object is created or removed. Same as a change window ending between two changes; documented in `import-watermarks.md` |

## Decisions

Settled with Jay on 2026-10-25:

1. **A later copy of an object whose update is already staged:** keep both execution items, each recording a real change from saved state, and count the object once. Withdrawing the earlier item would mean deleting a saved change record, so the object's history would skip a step.
2. **Created then deleted within a run:** must not create the object (net change). Creates are held until the end of the run; see invariant 2 for why staging them was rejected.
3. **Value budget within a page:** measure after Phase 3, then decide (Phase 5).
