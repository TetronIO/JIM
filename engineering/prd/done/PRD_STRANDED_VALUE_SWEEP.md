# Stranded Contributed Values: Flag-Gated Recall Sweep after a Connector Space Clear

- **Status:** Done
- **Created:** 2026-09-01
- **Author:** Jay Van der Zant (design artefact and exploration by Claude)
- **Issue:** [#1549](https://github.com/TetronIO/JIM/issues/1549)

## Problem Statement

Clearing a Connected System's connector space hard-deletes its Connected System Objects without running obsoletion processing. Metaverse attribute values the system contributed survive the clear with their provenance fully intact, which is correct for the dominant use case (a reset: clear, Full Import, Full Synchronisation). But a source object that never returns after the clear leaves its contributed values stranded on the Metaverse Object indefinitely:

- Obsoletion recall never runs: the clear bypasses it, and no Connected System Object is ever created for the departed object to carry an obsoletion later.
- The orphaned-contribution recall (#1533 / PR #1536) is structurally unreachable (it takes a Connected System Object as input) and would consider the values owned anyway (the Synchronisation Rule link is live).
- No Full Synchronisation of any system ever visits the affected Metaverse Object: both sync passes iterate only the synchronised system's own Connected System Objects.

The values are indistinguishable from healthy contributions: live `ContributedBySystemId` and `ContributedBySyncRuleId`, winning precedence, exporting downstream indefinitely. The strand is cleaned up only when the system is eventually deleted with Synchronised Deprovisioning (#809's residue pass); the gap is the whole lifetime of the system between the clear and its deletion.

Two defects were found during the design exploration and are folded into this delivery:

1. **Surface inconsistency:** the portal queues the clear as a worker task with an Activity and cleared-object counts; the REST endpoint (which PowerShell calls) runs the deletion synchronously in the request with no Activity, no counts and no audit trail.
2. **Copy defect:** the Clear Connector Space dialog promises that a Full Import plus Full Synchronisation on all Connected Systems "rebuild[s] the correct state", which provably cannot recover stranded values today.

Full design rationale, options considered and stress tests: the #1549 design artefact (Stranded Contributions), agreed by the product owner 2026-09-01.

## Goals

- Recall stranded contributed values (with surviving-contributor re-election or a No Contributor clear, and Pending Export staging) at the first Full Synchronisation of the contributing system after a Connector Space clear.
- Zero cost on normal synchronisation runs: the sweep is gated by a per-system flag stamped by the clear; a run without the flag pays one boolean read. Delta Synchronisation is untouched.
- Behavioural parity with obsoletion: the sweep must be indistinguishable in policy terms from the obsoletion that never ran (per-type `RemoveContributedAttributesOnObsoletion` honoured, #1570 last-known-state preservation applied, Metaverse Objects pending deletion skipped).
- One upgrade-time self-heal for historical strays: the migration sets the flag for every existing system.
- The sweep is fully visible in the Full Synchronisation run's Activity: that it ran, why it was armed, and its outcome, including the zero-findings case.
- All clear surfaces (portal, REST, PowerShell) queue the same worker task, produce the same Activity, and stamp the sweep flag.

## Non-Goals

- **No recall choice on the Clear dialog.** The strand depends on future source state unknown at clear time; a recall-at-clear option is a trap for the dominant reset case (rejected as Option 2 in the design artefact). The "withdraw this system's values" intent is served by Synchronised Deprovisioning (#809).
- **No tombstone/marker per contribution.** The stranded state is fully computable from provenance plus join absence (rejected as Option 3).
- **No standing per-run sweep.** The flag-gated form was chosen deliberately over invariant enforcement on every run; synchronisation run time is an optimisation priority.
- **No change to recall, re-election or precedence semantics.** The sweep reuses the shipped #1537/#809 recall engine.
- **No sweep in Delta Synchronisation**, even when the flag is set; the flag waits for the next full run.

## User Stories

1. As an administrator who cleared and re-imported a source system whose population shrank in the window, I want the values contributed by never-returned objects recalled at the next Full Synchronisation, so stale data stops exporting to downstream systems.
2. As an administrator decommissioning a source (clear, then never import again), I want one Full Synchronisation to withdraw the system's contributions (with surviving contributors re-elected and live target accounts preserved as last known state), so the Metaverse does not carry dead data until the system is deleted.
3. As an operator reviewing a Full Synchronisation run, I want the Activity to state that a stranded-value sweep ran, why it was armed, and what it recalled, so the run's effects are auditable.
4. As an automation author, I want the REST/PowerShell clear to be queued, audited and trackable exactly like the portal's, so scripts can wait on the Activity and the audit trail is complete regardless of surface.

## Functional Requirements

### Clear path unification (layer 1)

1. `POST /connected-systems/{id}/clear` must queue `ClearConnectedSystemObjectsWorkerTask` (attributed to the calling user or API key, mirroring the Run Profile execution endpoint) and return **202 Accepted** with a response carrying the Activity id, Worker Task id and a message. It must no longer run the deletion inline.
2. `Clear-JIMConnectedSystem` must surface the tracking object and gain `-Wait` / `-Timeout` (via `Wait-JIMActivityCompletion`), consistent with `Remove-JIMSyncRule`.
3. The queued clear's Activity behaviour (target, operation type, cleared counts on completion) is already implemented for the portal path and must now serve every surface.

### The sweep flag (layer 2)

4. A new `ConnectedSystem` column (working name `StrandedValueSweepPending`) with lifecycle: set by every successful clear (in the shared server clear method, so all surfaces inherit it); read by Full Synchronisation; cleared only when the sweep completes. An interrupted run leaves it set and the next Full Synchronisation re-sweeps (the recall is idempotent).
5. The migration sets the flag for every existing system, giving each one exactly one self-heal at its first Full Synchronisation after upgrade. New systems default to unset.
6. Delta Synchronisation ignores and preserves the flag.

### The sweep (layer 2)

7. When the flag is set, the Full Synchronisation run executes a stranded-value sweep after its existing passes: per import Synchronisation Rule of the system (**enabled and disabled alike**; a disabled rule's retention doctrine (#1537) protects a paused flow while the source object remains, not values whose object is gone; obsoletion recalls by system regardless of rule state and the sweep must match), select Metaverse Objects holding values the rule contributed where no Connected System Object of this system is joined (`NOT EXISTS` on the join foreign key), and recall them through the shipped engine (`RecallSyncRuleContributedValuesAsync` machinery) with a new `ContributorRecallScope.ForStrandedContribution(connectedSystemId)` scope.
8. The scope excludes the whole system from re-election and sets `IsDeliberateWithdrawal = false`, so the #1570 last-known-state preservation applies: an object whose remaining joined systems include no enabled import source for its type keeps its values, reported as a Values Preserved outcome.
9. Rules whose Connected System Object Type has `RemoveContributedAttributesOnObsoletion` disabled are skipped: retained values there are policy, not strands.
10. Metaverse Objects pending deletion are skipped. The sweep does not evaluate Metaverse Object deletion rules.
11. Every affected Metaverse Object gets a Run Profile Execution Item under the run's Activity; the Activity's summary states that the sweep ran because a clear armed it, with counts (values recalled, re-elected, cleared, preserved, objects processed), and states the zero-findings case explicitly. Values whose provenance is null (severed by a #1537 "keep" choice) are never candidates.
12. Pending Exports are staged for recalled/re-elected values through the normal export evaluation, with recall semantics (update existing target objects, never provision).

### Copy and documentation

13. The Clear Connector Space dialog's remediation copy is corrected: state that values contributed by objects that do not return are recalled at the next Full Synchronisation of this system, and that import-before-synchronisation ordering avoids a recall-then-re-assert export cycle.
14. Public docs cover the queued clear on all surfaces and the stranded-value sweep story; changelog entries for both.

## Examples and Scenarios

### Scenario 1: routine reset

**Given** a cleared system whose Full Import returns every object. **When** the Full Synchronisation runs. **Then** every Metaverse Object holds a joined Connected System Object again, the armed sweep finds nothing, the Activity reports the sweep ran with zero findings, and the flag clears. No downstream exports beyond the run's normal output.

### Scenario 2: clear then partial re-import (the motivating case)

**Given** a cleared system whose re-import returns all objects except one departed employee. **When** the Full Synchronisation runs. **Then** the sweep recalls exactly the departed employee's contributed values: attributes with a surviving contributor re-elect, sole-contributed attributes clear with a No Contributor outcome, corrective Pending Exports are staged, one Run Profile Execution Item records the object, and the flag clears.

### Scenario 3: decommission without delete

**Given** a cleared system that will never be imported again. **When** the administrator runs one Full Synchronisation. **Then** the sweep withdraws the system's contributions; Metaverse Objects whose only remaining connections are provisioned targets are preserved as last known state (#1570) with Values Preserved outcomes.

### Scenario 4: upgrade self-heal

**Given** a deployment carrying strays from clears that predate this feature. **When** each system's first Full Synchronisation after upgrade runs. **Then** the migration-set flag arms the sweep once per system; strays are recalled and reported; subsequent runs pay only the boolean read.

## Constraints

- Synchronisation Integrity rules in full: fast/hard failures, all errors via RPEIs/Activities, batch summary statistics.
- No new recall semantics; reuse `ContributorReElectionService`, the per-rule recall executor and export evaluation.
- Layering: portal/REST call `JimApplication` only; the sweep runs in the worker's Full Synchronisation processing.
- The selector needs real-PostgreSQL test coverage; the in-memory provider cannot express the join-absence semantics faithfully.

## Dependencies

- #1537 recall engine (`ContributorRecallScope`, `ContributorReElectionService`, `RecallSyncRuleContributedValuesAsync`) - shipped.
- #809 residue pass precedent and `RemainingImportSourceEvaluator` (#1570) - shipped.
- #892 scope-review pass as the structural precedent for a Metaverse-Object-driven step in Full Synchronisation.

## Acceptance Criteria

- [ ] REST/PowerShell clear queues the worker task, returns 202 with Activity and Task ids, and is audited identically to the portal.
- [ ] `Clear-JIMConnectedSystem` surfaces the tracking object and supports `-Wait`/`-Timeout`.
- [ ] The flag column exists, is stamped by every clear surface, migration-set for existing systems, cleared only on sweep completion, and ignored by delta runs.
- [ ] The sweep recalls stranded values per requirements 7-12, proven by red-first unit tests, real-PostgreSQL selector tests, and an integration scenario covering clear-then-partial-re-import.
- [ ] The Full Synchronisation Activity surfaces the sweep (armed reason, counts, zero-findings case).
- [ ] Dialog copy corrected; public docs and changelog updated.
