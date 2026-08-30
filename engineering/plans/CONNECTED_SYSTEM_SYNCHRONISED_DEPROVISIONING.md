# Connected System Synchronised Deprovisioning - Implementation Plan

- **Status:** Planned
- **Issue:** [#809](https://github.com/TetronIO/JIM/issues/809)
- **PRD:** [`engineering/prd/PRD_CONNECTED_SYSTEM_SYNCHRONISED_DEPROVISIONING.md`](../prd/PRD_CONNECTED_SYSTEM_SYNCHRONISED_DEPROVISIONING.md) (all five decisions resolved, 2026-08-29)

## Overview

Deliver the execution side of #809: deleting a Connected System offers **"Deprovision through synchronisation (recommended)"** (each object processed through the normal obsoletion semantics, then a by-provenance residue pass, then the existing deletion as the final step, all as a queued, resumable Worker task) or **"Delete immediately and keep contributed data"** (today's behaviour, warned). The #134 attribute impact preview is deliberately NOT in this plan: per the sequencing decision it lands later as an #827 framework adapter, against the evaluation seam Phase 1 creates. UX is fixed by the "Deprovisioning UX" artefact.

## Technical Architecture

### Current state

- `DeleteConnectedSystemAsync` → `ExecuteDeletionAsync`: `MarkOrphanedMvosForDeletionAsync` for sole-connector Metaverse Objects (when `evaluateMvoDeletionRules`), then the raw-SQL bulk delete, which also removes the system's Synchronisation Rules and thereby null-provenances every value they contributed. Large systems already queue `DeleteConnectedSystemWorkerTask` (Worker.cs dispatch ~500) and the REST endpoint already splits 200/202.
- The portal's `DeleteConnectedSystemDialog` already presents the #135 count tier via `ConsequenceConfirmationDialog` with name-to-confirm, a change-history checkbox and a background-job estimate.
- Per-object obsoletion semantics live in `SyncTaskProcessorBase.ProcessObsoleteConnectedSystemObjectAsync` (recall honouring `RemoveContributedAttributesOnObsoletion`, re-election via the already-extracted `ContributorReElectionService`, deletion-rule evaluation, export staging), coupled to the run-profile processor's context.

### Chosen mechanism

1. **One task, one mode flag**: `DeleteConnectedSystemWorkerTask` gains `SynchronisedDeprovisioning` (bool), following its existing option precedent (`EvaluateMvoDeletionRules`, `DeleteChangeHistory`). Immediate deletion keeps today's path bit-for-bit, including the small-system synchronous case.
2. **Fence first**: at queue time the system is marked deleting (the flag `ClearConnectedSystemObjectsAsync` already refuses on) and excluded from scheduling; the run's first step verifies the fence.
3. **Deprovisioning executor** (JimApplication, `ExecuteSchemaRefreshRemovalAsync`/`ExecuteSyncRuleDeletionRecallAsync` precedents):
   - **Per-object pass**: batched over the system's Connected System Objects, each processed through the obsoletion core extracted in Phase 1 (recall per Object Type setting, surviving-contributor re-election with a system-scoped `ContributorRecallScope`, Metaverse Object deletion-rule evaluation with grace periods, Pending Export staging), one RPEI per object, Activity counters per batch.
   - **Residue pass**: per import Synchronisation Rule, recall remaining values by provenance (the #1537 rule-recall path generalised), catching values with no connector-space presence; runs before any rule is deleted.
   - **Final step**: the existing `ExecuteDeletionAsync` (tombstone, bulk delete), then Activity completion with summary statistics.
4. **Resumability**: a checkpoint (last completed CSO id, phase marker) persisted on the task row per batch; per-object processing is idempotent (an already-obsoleted object is a no-op), so a worker restart resumes from the checkpoint without double-staging exports. The worker's existing stale-task recovery marks an interrupted Activity for retry rather than failing the deletion outright.
5. **Failure mode**: fast/hard; the system survives, fenced, consistent to the last completed batch; the deletion is retryable and the Activity says exactly where it stopped.

### The evaluation seam (what #134/#827 will reuse)

Phase 1's extraction gives JimApplication a callable "obsolete this CSO against this configuration" core whose outputs (recalls, re-elections, deletion-rule verdicts, would-be exports) are data. The later #827 adapter runs the same core in a read-only harness (the `ReadOnlySyncRepositoryGuard` pattern from sync previews) so preview equals execution by construction. Nothing else in this plan depends on #827.

## Implementation Phases

TDD red-first throughout; British English; Title Case domain nouns; changelog + docs with the behaviour.

### Phase 1: Obsoletion core extraction and impact summary

- Extract the reusable core of `ProcessObsoleteConnectedSystemObjectAsync` into JIM.Application (collaborators parameterised, exactly the `ContributorReElectionService` extraction pattern), with the processor delegating; behaviour-preserving, proven by the existing obsoletion suites before and after.
- `ContributorRecallScope.ForDeletedConnectedSystem(connectedSystemId)`: every contributor from the deleted system is ineligible; any other system's joined object is a survivor.
- Extend `GetDeletionPreviewAsync` with the deprovisioning impact counts (contributed values/objects, deletion-rule-eligible Metaverse Objects), count-query only.

### Phase 2: The deprovisioning run

- Task flag + migration; TaskingServer Activity branch wording; Worker dispatch case extension.
- Executor with the three passes above, checkpointing, fencing, RPEIs, batch summary logging.
- Workflow tests: surviving-contributor takeover across systems; sole-contributor clear; deletion rule fires for last-connector Metaverse Objects (and grace period holds); exports staged; residue pass catches a cleared-space value; failure partway leaves the system fenced and retryable; resume from checkpoint does not double-process; immediate mode byte-for-byte unchanged.

### Phase 3: REST and PowerShell

- **Retry/abort decision carried in from Phase 2**: after a failed run the system stays fenced (deliberate), and `DeleteAsync` refuses a Deleting system, so the surfaces must give the administrator an explicit retry (re-queue from checkpoint) and, if we choose to offer it, an abort that un-fences; decide and deliver both here.

- `DELETE connected-systems/{id}` gains the mode (deprovision default; the existing 200/202 split carries the tracking DTO); deprovisioning always queues.
- `Remove-JIMConnectedSystem`: mode parameter (deprovision default), impact-stating `ShouldProcess` text, tracking output with the Activity id; help carries the immediate-mode warning. Pester.
- OpenAPI regeneration.

### Phase 4: Portal

- `DeleteConnectedSystemDialog` gains the two-option radio group (deprovision pre-selected; selecting immediate reveals its warning), above the existing counts/name-to-confirm/change-history affordances, per the UX artefact. The count tier doubles as the impact statement until the #827 preview adapter lands; the dialog carries a disabled "Preview attribute impact" affordance labelled as coming, so the layout does not shift when it does.
- Queued state: snackbar with "View Activity"; the Connected System page shows a deprovisioning-in-progress banner (Activity link, system fenced) while the task runs.
- bUnit for the dialog logic (in scope: `Shared/`); full-stack runtime verification of both modes.

### Phase 5: Documentation and changelog

- `docs/configuration/connected-systems.md` deletion section rewritten around the choice; concepts cross-links (attribute-priority, jml-lifecycle).
- Changelog: ✨ the choice and the queued deprovisioning run; the immediate mode's behaviour unchanged.

## Success Criteria

The PRD's acceptance criteria, with the deprovisioning run proven at runtime in the devcontainer stack (two-source topology: takeover, clear, deletion rule, staged exports, residue value from a cleared space) as well as by the suites.

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Obsoletion core extraction regresses run-time obsoletion | Behaviour-preserving move first (suites green before/after), delegation kept thin, both call sites workflow-tested |
| Double-staged exports on resume | Idempotent per-object processing + batch checkpoint; resume test in Phase 2 |
| Scheduler races the run | Fence at queue time; run verifies the fence; scheduler skips fenced systems |
| Scale (1m objects) | 500-object batches, count-only impact, #917 memory precedents; correctness over speed per the PRD |
| Residue pass ordering mistakes | Pass runs strictly before rule deletion; workflow test pins a cleared-space value surviving into the pass |

## Dependencies

- #1537 (shipped): task shape, `ContributorRecallScope`, 202 convention, dialog pattern.
- #827/#134: downstream consumers of the Phase 1 seam; nothing here waits on them.
- #1549 may reuse the residue-pass mechanics for its own scenario; keep the recall scope input general.
