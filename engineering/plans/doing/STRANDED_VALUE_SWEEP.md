# Stranded Value Sweep: Implementation Plan

- **Status:** Doing
- **Issue:** [#1549](https://github.com/TetronIO/JIM/issues/1549)
- **PRD:** [PRD_STRANDED_VALUE_SWEEP.md](../../prd/doing/PRD_STRANDED_VALUE_SWEEP.md)

## Overview

Two stacked layers deliver #1549: layer 1 unifies the clear path (REST/PowerShell queue the same worker task as the portal), a prerequisite because every clear surface must stamp the sweep flag; layer 2 delivers the flag-gated stranded-value sweep in Full Synchronisation. Design rationale and option analysis live in the #1549 design artefact (Stranded Contributions), agreed 2026-09-01.

## Phase 1: Clear path unification (branch `feature/clear-connector-space-parity`)

1. `SynchronisationController.ClearConnectorSpaceAsync`: replace the inline `ClearConnectedSystemObjectsAsync` call with queueing `ClearConnectedSystemObjectsWorkerTask` (`ForUser` / `ForApiKey`, mirroring `ExecuteRunProfileAsync`); respond 202 Accepted with a new `ConnectorSpaceClearResponse` DTO (ActivityId, TaskId, Message). 404 for a missing system and 400 for a queue refusal are unchanged in spirit.
2. `Clear-JIMConnectedSystem`: emit the tracking object; add `-Wait` / `-Timeout` via `Wait-JIMActivityCompletion` (the `Remove-JIMSyncRule` pattern); update help and Output docs.
3. Tests: controller tests (queued task attribution for user and API key, 202 shape, 404); Pester tests for the cmdlet's new behaviour. Red first.
4. Docs: `docs/powershell/connected-systems.md` (or wherever the cmdlet is documented) and any REST doc surface; changelog 🔄 entry.

## Phase 2: The sweep (branch `feature/clear-connector-space-parity-stack-stranded-sweep`)

1. Model/migration: `ConnectedSystem.StrandedValueSweepPending` (bool, default false); migration sets `true` for existing rows. Regenerate OpenAPI doc if the property is API-reachable (or `[JsonIgnore]` if not needed on the wire).
2. Flag stamping: set in the server clear path (`ClearConnectedSystemObjectsAsync`) after a successful clear, so every surface inherits it via the queued task.
3. `ContributorRecallScope.ForStrandedContribution(int connectedSystemId)`: system's rules ineligible for re-election; `IsDeliberateWithdrawal = false` (the #1570 preservation applies).
4. Selector: `GetMetaverseObjectIdsWithStrandedValuesContributedBySyncRuleAsync(int syncRuleId, int connectedSystemId)` on the sync repository: values by `ContributedBySyncRuleId` with `NOT EXISTS` a `ConnectedSystemObjects` row joining the value's Metaverse Object to the system.
5. Recall executor: parameterise the #1537 per-rule recall so the sweep can supply the stranded selector and the non-deliberate scope; ensure the #1570 `RemainingImportSourceEvaluator` gate is applied when the scope is a disappearance.
6. Full Synchronisation pass in `SyncFullSyncTaskProcessor` (after the #892 scope-review step): when the flag is set, per import Synchronisation Rule (all rules, skipping types with `RemoveContributedAttributesOnObsoletion` off), run the recall; clear the flag on completion; surface armed-reason and counts on the Activity, zero-findings case included.
7. Dialog copy update (`ConnectedSystemObjectList.razor` clear dialog), public docs, changelog 🐛 entry.
8. Tests: red-first unit tests for the scope factory, gating, skips and Activity reporting; `RequiresPostgres` tests for the selector and migration default; integration scenario clear-then-partial-re-import.

## Success Criteria

Per the PRD's acceptance criteria. Normal synchronisation runs pay one boolean read; the sweep executes only when armed by a clear.

## Risks & Mitigations

- **Mid-reset Full Synchronisation** (clear then sync before import) sweeps everything the system contributed: semantically correct, noisy; dialog copy states the ordering. Accepted in the design.
- **Executor reuse drift**: the #1537 executor assumes deliberate withdrawal; wire the #1570 gate explicitly and cover with a red-first test (stranded value on a target-only Metaverse Object must be preserved).
- **In-memory provider blindness** to the join-absence predicate: `RequiresPostgres` coverage is mandatory, not optional.
