# Bounded-Memory Delta Import

- **Status:** Planned
- **Issue:** [#917](https://github.com/TetronIO/JIM/issues/917)
- **PRD:** [PRD_BOUNDED_MEMORY_DELTA_IMPORT.md](../prd/PRD_BOUNDED_MEMORY_DELTA_IMPORT.md)

## Overview

Make a Delta Import save each page of changes as it goes, instead of holding every page's Connected System Objects until the last page has been read. The peak Worker memory is then set by one page, not by the size of the change window. Every whole-run behaviour the import has today keeps its meaning: deletion limits, a later page's copy superseding an earlier one, reference resolution, Pending Export reconciliation and the watermark rule.

Full Import is out of scope here. Its follow-up issue reuses the per-page staging this plan builds.

## Business Value

- An administrator can size the Worker for their steady state; a large change window takes longer but no longer needs more memory.
- A Delta Import stopped by the memory guard (#2507), a failure or a cancellation keeps the work it did, so the next run finishes the job instead of starting again.
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

**One finding shapes the design.** The OpenLDAP accesslog source removes repeated changes to the same DN only within a page (`LdapAccesslogDeltaSource`, `processedDns`), and it fetches the object's current state for each. A group modified across 40 pages therefore arrives 40 times, each time with every member. Today the copies supersede one another in memory (#2074) and cost only the incoming payload. Once pages are saved, each copy would reload and re-compare the group from the database, unless the run remembers what it saved.

### Proposed design

```
for each Connector page:
    ProcessImportObjectsAsync(page)      unchanged shape; uses the staged-copy register below
    StagePageAsync(page lists):
        resolve references (in-memory over the page, then DB for everything already saved)
        save the page's creates, then its updates (existing batch writers)
        stamp import state, flush RPEIs
        reconcile Pending Exports for the page's updated CSO ids
        record staged creates in _csoExternalIdLookup; record every staged object in the staged-copy register
        release the page's objects (ReleaseHydratedAttributeValues, clear the lists, clear the tracker)
after the last page:
    WithholdConnectorReportedDeletesOverTheLimitsAsync   unchanged decision, over deferred delete records
    apply the deletes that stand, in batches             re-hydrating only what the delete needs
    sweep this run's still-unresolved references         one set-based pass, same rules as ResolveReferencesAsync
    report what is still unresolved on the items that hold it
    JudgeUnreconciledInitialPasswordHoldsAsync           over the run's reconciled ids
    record watermark (#1868), Activity message
```

Run-wide state after the change is limited to identifiers, hashes and counters:

| Run-wide state | Holds |
|---|---|
| Staged-copy register | duplicate key → staged CSO id, outcome counted, content hash of the staged copy |
| Deferred deletes | CSO id, status and last-updated before, RPEI id |
| Reconciled ids | CSO ids, for `JudgeUnreconciledInitialPasswordHoldsAsync` |
| Items with unresolved references | RPEI id and CSO id, for the end-of-run report |
| `_csoExternalIdLookup` | as today, plus an entry per staged create |

### The invariants, one by one

**1. Deletion limits (#2089).** Deletes are not applied page by page. Each delete is recorded as a small deferred record, and the hydrated CSO is released with its page. After the last page, the existing decision runs over the deferred records. If the deletes stand, they are applied in batches. Phase 2 confirms what the deletion's change record needs from the object. If it needs only the header, nothing is re-hydrated. If it needs the values, the batch re-hydrates them. Creates and updates were never subject to the limits, so saving them early changes nothing. When deletes are withheld, the run keeps its watermark (as today), and the next run re-reads updates that are already staged. The staged diff makes that a no-op.

**2. A later page's copy (#2074).** The register replaces `_earlierPageCopies` once an earlier copy has been saved:

| Earlier copy (staged) | Later copy | Behaviour |
|---|---|---|
| any | identical content hash | Counted unchanged; no hydration, no item. This is the busy-group case above. |
| update or create | different update | Hydrate the staged CSO from the database and compare as usual. A second item records the change from the saved state. The object is counted once, as updated (see open question 1). |
| update or create | delete | A deferred delete of the staged CSO, subject to the limits as usual. A create followed by a delete leaves an Obsolete CSO that the next synchronisation removes (see open question 2). |
| deferred delete | re-insert or update | Withdraw the deferred delete (it is still in memory), as today, and apply the later copy to the object. |

The content hash is the existing `ImportContentHashCalculator.CalculateContentHash`. It is trusted only against a copy this run computed and staged itself, never against a stamp from an earlier run, so the D7 rule ("never trust a Delta Import payload's hash") is not weakened.

**3. Matching a later copy to a staged create.** `_csoExternalIdLookup` is documented as never mutated mid-import. Staging breaks that assumption: an object created on page 1 must be found on page 3, or it would be created twice. `StagePageAsync` adds each staged create's lookup entry under the same key format. The secondary-external-ID lookup (`ResolveSecondaryLookupCandidatesAsync`) reads the database per page, so it sees staged creates already. The doc comment on the field changes accordingly.

**4. References.** Each page resolves its references in memory against the page's own creates and updates, then queries the database. Everything earlier pages saved is visible there, through the existing Phase 2 query in `ResolveReferencesAsync`, which keeps #1285's type scoping and #2051's case rules. A reference to an object a *later* page creates is saved unresolved (`UnresolvedReferenceValue`, null `ReferenceValueId`), which is what the schema already allows. The item that holds it is noted. After the last page, a sweep resolves those values against the objects now saved. It must use the same semantics as `ResolveReferencesAsync` Phase 2, not `FixupCrossBatchReferenceIdsAsync`: that query matches secondary external IDs only, ignores a declared target type, and spans the whole Connected System. Whatever is still unresolved is reported on its saved item through a raw RPEI update (`BulkUpdateRpeiFieldsRawAsync` pattern), honouring `UnresolvedReferenceHandling`. Change records written for a value later resolved need the same treatment `FixupCrossBatchChangeRecordReferenceIdsAsync` gives them today.

**5. Pending Export reconciliation.** `ReconcilePendingExportsAsync` already reloads each CSO's values from the database per page, using only the ids it is given. It runs per staged page, using the page's ids and its RPEI lookup, which is then released. The background pre-load of Pending Exports stays whole-run (it is keyed by CSO and already resident). An entry is removed once a page has reconciled it, so a later copy of the same object reconciles against what is left. That is what two consecutive Delta Imports do today.

**6. Counts.** `_updatedObjectCount` and the other counters become counts of distinct objects. The register records how each staged object was counted, so a later copy moves it between figures rather than adding a second count, which is what `SupersedeEarlierPageCopy` does today. The end-of-run "counted twice" warning stays as the guard.

**7. Watermark (#1868).** Unchanged: recorded only after the last page is staged and the end-of-run steps succeed. A stop at any point keeps the starting watermark.

**8. A stopped run.** Today, cancellation discards everything ("nothing has been persisted yet"). After this change, staged pages stay staged. On cancellation, a memory-guard stop or a failure, the run skips the end-of-run steps that need every page: deferred deletes are dropped unapplied, and the reference sweep and watermark are skipped. The Activity message says how many objects were saved, and that the next Delta Import reads from the same point. A staged reference left unresolved by a stop is resolved by the next run's sweep, which covers every unresolved value the Connected System's staged objects hold, not only that run's.

### Value budget within a page (conditional)

The Connector bounds a page by entry count, not size: one LDAP page can hold several 495k-member groups. Splitting a page into sub-batches by attribute value count (summed from the import objects before hydration) would cap the hydrated graphs at a budget. It cannot reduce the incoming page, which the Connector already holds. Phase 5 adds it only if Phase 3's measurements show a single page is still the problem.

## Implementation Phases

Each phase follows TDD: write failing tests first, then implement, then keep both `dotnet build JIM.sln` and `dotnet test JIM.sln` clean. Phases 1 to 4 are one PR or one stack, and so is Phase 5 if it is needed.

### Phase 0: Baseline (before implementation)

- Run Scale750k40kGroups on `main` once the host's drive issue is resolved (this was already planned). Record the Delta Import's peak Worker memory from the docker-stats CSV and the save-phase GC heap log lines.
- Run Scenario 8 at Scale100k5kGroups for the same figures, which are comparable with the July numbers in #917.
- Add a Worker test harness measurement: a fake Connector producing N pages of large group updates, recording `GC.GetTotalMemory(true)` after each page. Today's figure should grow with N. This is the red test for the goal.

### Phase 1: Separate staging from the end of the run (no behaviour change)

- Extract the save, stamp, flush and reconcile steps from `PerformImportAsync` into `StagePageAsync(creates, updates)`, still called once after the last page.
- Change the whole-run consumers to take ids: `ReconcilePendingExportsAsync` and `JudgeUnreconciledInitialPasswordHoldsAsync` already use only `cso.Id`.
- Introduce the staged-copy register and the deferred-delete record types (in `JIM.Models` if they cross a boundary, otherwise private records as `EarlierPageCopy` is today).
- Every existing test passes unchanged.

### Phase 2: Deferred deletes

- Record Connector-reported deletes as deferred records. The decision in `WithholdConnectorReportedDeletesOverTheLimitsAsync` runs over the records, and the deletes that stand are applied in batches after it.
- Confirm in `ConnectedSystemServer.UpdateConnectedSystemObjectsAsync` and the change-record builder what a deletion's change record needs from the hydrated object, and hydrate only that.
- Tests: `DeltaImportDeletionLimitTests` pass unchanged; a new test shows the hydrated CSO of a deferred delete is released before the next page.

### Phase 3: Stage each page

- Call `StagePageAsync` after each page of a call-based Delta Import. File-based imports and Full Import keep calling it once.
- Add staged creates to `_csoExternalIdLookup`; update its doc comment.
- Replace `_earlierPageCopies` for staged copies with the register, following the table in invariant 2, and keep in-memory supersede for deferred deletes.
- Count distinct objects.
- Tests:
  - Each row of the invariant 2 table.
  - A create on page 1 updated on page 3 (no duplicate CSO).
  - A busy group on many pages is hydrated once.
  - Counts across pages.
  - `ImportDuplicateExternalIdTests` and `ImportObjectCountReportingTests` pass, or are updated deliberately for open questions 1 and 2 and listed in the PR.
- `RequiresPostgres` test: two pages staged on one context with tracked instances from page 1 still present. This guards the "raw SQL writes must fix up or detach tracked instances" rule in `src/CLAUDE.md`.
- The Phase 0 harness test turns green: peak memory flat in N.

### Phase 4: Forward references and the stopped run

- Note items saved with unresolved references. After the last page, sweep with Phase 2 semantics, report what is left on the saved items, and fix up change records.
- New repository method for the sweep, scoped to the Connected System's unresolved values and set-based, with a `RequiresPostgres` round-trip test. Raw SQL follows the bulk column rules; the RPEI update uses `RpeiBulkColumns`.
- Stopped-run behaviour and Activity wording (`ImportOutcomeMessage`). Tests for cancel, failure and memory-guard stop after page k: the watermark is kept, pages up to k are staged, and the next run produces no duplicate change records.
- `CrossPageReferenceProvisioningTests` pass; add a forward-reference test (group on page 2, member created on page 7).

### Phase 5: Value budget within a page (only if measurements call for it)

- Sub-batch a page's import objects by summed value count before hydration; default budget chosen from Phase 3 measurements.
- Tests: a page with several very large groups stays under the budget's hydrated size.

### Phase 6: Validation, docs and changelog

- Integration:
  - Scenario 8 at Scale100k5kGroups and Scale750k40kGroups, with peaks compared against Phase 0.
  - Scenario 4 (deletion rules).
  - The full parallel suite at Small or Medium for regressions.
- Docs, per the PRD's Documentation Impact table.
- Changelog: ⚡ Delta Import memory now depends on the page size, not the size of the change window. 🔄 A Delta Import stopped part-way keeps the changes it had already saved.
- Move the PRD and plan to `done/` in the PR that closes #917.

## Success Criteria

- The Phase 0 harness test shows peak memory within 25% between 1x and 5x the change count.
- Scale100k5kGroups and Scale750k40kGroups Delta Import peaks are lower than the Phase 0 baseline; the run passes on the same host.
- Delta Import wall time for a single-page window is within 5% of today's; for multi-page windows, report the change. A small per-page cost is acceptable.
- No regression in any Worker test or integration scenario beyond the deliberately changed open-question behaviours.

## Benefits

- **Performance:** memory sized by page; no run-sized save phase at the end; busy objects across pages compared once.
- **Resilience:** stopped runs keep their work; the memory guard's Stop becomes a pause rather than a loss.
- **Architecture:** Delta Import follows the per-page pattern synchronisation adopted in #451, and Full Import gets a ready mechanism.

## Dependencies

- No new packages. No schema change expected; if the sweep needs an index on unresolved reference values, it is an additive migration (`AddFunctionalIndexesForCrossBatchReferenceFixup` may already cover it).
- Merged: #2503, #2507.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| A later copy matched to the wrong object, or created twice, because the lookup missed a staged create | Invariant 3; dedicated test; the existing duplicate-external-ID error remains the backstop |
| Reference sweep resolves differently from in-memory resolution | It reuses `ResolveReferencesAsync` Phase 2 logic, not the coarse fixup query; tests per #1285 and #2051 case |
| Hot path regressions from per-page saves on many small pages | The existing batch writers are used unchanged; measure in Phase 3; a page smaller than a batch costs one round of writes |
| Tracked-instance faults on the long-lived context across staged pages | `ClearChangeTracker` after each page, as the update batches already do; `RequiresPostgres` multi-page test |
| Staged-but-unreconciled state visible to a synchronisation run started between a stopped import and the next | No different from today's state between two Delta Imports of half the window each; documented in `import-watermarks.md` |
| Behaviour change for open questions 1 and 2 surprises administrators | Decided before implementation; changelog and watermarks page say what a stopped run keeps |

## Open Questions

Carried from the PRD; to decide before Phase 3.

1. **Later copy of a staged object: two items or one?** Recommend two items (each records a real change from saved state), counted as one object. Withdrawing the earlier item would mean deleting a saved change record, and the object's history would then skip the intermediate state.
2. **Create then delete across pages.** Recommend accepting "created, then marked deleted, then removed by synchronisation". It is what two consecutive Delta Imports already do. The alternative, holding creates until the end, brings back the memory growth this plan removes.
3. **Value budget.** Recommend measuring after Phase 3, then deciding.
