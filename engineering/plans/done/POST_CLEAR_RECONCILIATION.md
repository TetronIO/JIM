# Post-Clear Reconciliation: Implementation Plan

- **Status:** Done
- **Issue:** [#1605](https://github.com/TetronIO/JIM/issues/1605)
- **PRD:** [PRD_POST_CLEAR_RECONCILIATION.md](../../prd/done/PRD_POST_CLEAR_RECONCILIATION.md)

## Overview

Two stacked layers deliver #1605 in the #1549 pattern. Layer 1 retrofits the shipped stranded-value sweep with the Full Import gate (armed-at timestamp, last-successful-import timestamp, refusal message, surfaces). Layer 2 adds the join record, Deletion Rule evaluation for recorded objects, the re-join shortfall check, the state-convergent zero-join pass, and the copy. Design rationale: the After the Clear and Orphans of the Clear artefacts linked from the issue.

## Phase 1: The gate (branch `feature/clear-reconciliation-gate`) ✅

1. Model: replace `ConnectedSystem.StrandedValueSweepPending` with `StrandedValueSweepArmedAt` (`DateTime?`, UTC) and add `LastSuccessfulFullImportCompletedAt` (`DateTime?`, UTC). Migration: add both columns, `UPDATE ... SET "StrandedValueSweepArmedAt" = now() WHERE "StrandedValueSweepPending"`, drop the bool. `Down` reverses (armed-at not null becomes true).
2. Repository (`IConnectedSystemRepository` / `ConnectedSystemRepository`): `SetStrandedValueSweepArmedAtAsync(int, DateTime?)` and `SetLastSuccessfulFullImportCompletedAtAsync(int, DateTime)`, both narrow raw-SQL setters with the in-memory fallback, replacing `SetStrandedValueSweepPendingAsync`.
3. Clear path (`ConnectedSystemServer.ClearConnectedSystemObjectsAsync`): stamp `StrandedValueSweepArmedAt = DateTime.UtcNow`.
4. Worker (`Worker.cs`, run-profile branch after `CompleteActivityBasedOnExecutionResultsAsync`): for `FullImport`, when the completed Activity is `Complete`, or `CompleteWithWarning` with zero object-level errors (connector-level `WarningMessage` only), stamp `LastSuccessfulFullImportCompletedAt`. Make the success predicate a small testable unit.
5. Sweep entry (`ConnectedSystemServer.StrandedValueSweep.cs`): `ExecuteStrandedValueSweepIfArmedAsync` returns null when not armed (one read); when armed but `LastSuccessfulFullImportCompletedAt` is null or not later than the arming, append the skipped sentence to the Activity Message, persist, keep the arming, and return a result marked skipped; otherwise sweep as today. `ExecuteStrandedValueSweepAsync` keeps its armed precondition, now on the timestamp, and clears it on completion.
6. Surfaces: `ConnectedSystemDetailDto` gains both timestamps (PowerShell inherits them; regenerate the OpenAPI document to confirm it builds); `ConnectedSystemDetail.razor` shows an armed notice in the two waiting states beside the obsolete-object notice.
7. Copy and docs: the clear dialog in `ConnectedSystemObjectList.razor` states the gate; `docs/configuration/connected-systems.md` clearing section replaces the import-first tip with the gate; the two `[Unreleased]` #1549 changelog entries are amended (the feature is unreleased) rather than adding a changed-behaviour entry.
8. Tests, red first: workflow tests for the three gate states; `RequiresPostgres` tests for the migration backfill and both setters; unit tests for the import-success predicate; Scenario 7 Test 4 gains a Full Synchronisation before the re-import asserting the skipped message and the arming still set, then the existing import-then-sync path asserting the sweep ran.

## Phase 2: Join record, Deletion Rules, shortfall, zero-join pass (branch `feature/clear-reconciliation-gate-stack-deletion-rules`) ✅

1. Model and migration: `ConnectorSpaceClearJoinRecord` (`ConnectedSystemId`, `MetaverseObjectId`, `ClearedAt`; composite key; index on `ConnectedSystemId`). Written by `DeleteAllConnectedSystemObjectsAndDependenciesAsync` as its first statement (delete existing rows for the system, then `INSERT ... SELECT` from joined Connected System Objects); deleted by the sweep on completion; deleted as a new step of the Connected System deletion sequence.
2. Service Setting `Sync.PostClearReconciliation.MaxMissingPercent` (integer, default 10, Synchronisation category), seeded beside `Sync.PageSize`, read through a `ServiceSettingsServer` accessor.
3. Sweep, before any recall: load the recorded ids, compute those still lacking a join to the system; if `missing / recorded * 100 > threshold`, append the refusal sentence, keep the arming and the record, return. Otherwise proceed.
4. Sweep, after the value recall: evaluate each still-missing recorded object with `EvaluateMvoDeletionRule` (disconnecting system = the cleared system) and the obsoletion path's marking helper; accumulate grace markings and immediate fates; flush immediate fates with the #809 sequence (`CaptureReferenceRecallContextAsync`, `EvaluateMvoDeletionsAsync`, `DeleteMetaverseObjectsAsync`, `StageReferenceRecallExportsAsync`); record an execution item per object with the policy snapshot. Delete the record. Extend `StrandedValueSweepResult` and `BuildSweepActivityMessage` with the counts.
5. Zero-join pass: a new repository query for Projected Metaverse Objects with zero joined Connected System Objects, unmarked, whose type rule is state-convergent; evaluate with a null triggering system and a snapshot reason naming state convergence; same flush. Shared helper called from the sweep and from the final step of Synchronised Deprovisioning. Pending Deletions page renders the null system honestly.
6. Copy: the clear dialog gains the Deletion Rule consequence and the shortfall sentence; docs (`connected-systems.md` clearing section, `metaverse.md` deletion behaviour, `service-settings.md` catalogue); changelog ✨ entry.
7. Tests, red first: unit tests for the evaluation per rule and mode, the shortfall boundary, the zero-join selection and attribution; `RequiresPostgres` tests for the join record write, replace and cleanup and the zero-join query; Scenario 7 gains grace and no-grace departures, the shortfall refusal, and the second-sync no-op.

## Success Criteria

Per the PRD's acceptance criteria. An ordinary Full Synchronisation pays one nullable-timestamp read.

## Risks & Mitigations

- **Import success misclassified**: a `CompleteWithWarning` from object errors would let the sweep treat failed objects as departed. The predicate keys on the object-error count, not the status alone, with a test per branch.
- **Retention pruning**: the gate reads a column on the Connected System, not the Activities table, so history retention cannot erase the evidence of the import.
- **Threshold too tight or loose**: default 10% is stated in the docs with the reasoning; #1618 may make it per Run Profile.
- **Zero-join pass touching objects it should not**: scoped to Projected objects, unmarked, state-convergent rules only; the in-memory provider cannot express the query faithfully, so `RequiresPostgres` coverage is mandatory.
