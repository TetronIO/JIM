# Post-Clear Reconciliation: Full Import Gate and Deletion Rule Evaluation after a Connector Space Clear

- **Status:** Done
- **Created:** 2026-09-03
- **Author:** Jay Van der Zant (design artefacts and exploration by Claude)
- **Issue:** [#1605](https://github.com/TetronIO/JIM/issues/1605)

## Problem Statement

Clearing a Connector Space hard-deletes the system's Connected System Objects without obsoletion. #1549 closed the value half of that gap with a flag-gated stranded-value sweep at the next Full Synchronisation. Two problems remain, one inherited and one new:

1. **Deletion Rules are never evaluated for the disconnections a clear causes.** A Metaverse Object whose only connector was the cleared system is left wholly orphaned: no deletion-rule decision, no grace-period marking, nothing queued for housekeeping. The gap is unbounded: every later path that could delete the object, Synchronised Deprovisioning's orphan marking included, keys on a Connected System Object of the deleted system, and after a clear there is none. Nothing in JIM queries for a Metaverse Object with zero joins. Objects whose type names the cleared system as an authoritative source are also missed.
2. **The shipped sweep can fire on an empty Connector Space.** The #1549 flag arms at the clear and the sweep runs at the next Full Synchronisation whatever state the Connector Space is in. A Full Synchronisation run before the re-import (by a Schedule, another administrator, or habit) treats every previously joined object as departed. Today that recalls every value the system contributed; with Deletion Rules evaluated it would be mass deletion of every no-grace type plus the deprovisioning exports behind it. Dialog copy is the only defence.

A third hazard is adjacent: a Full Import that succeeds but returns far fewer objects than the clear removed (a broken filter or base DN) would have the sweep treat the rest as departed. The obsoletion threshold planned in #1618 cannot catch this, because the Connector Space was empty and there was nothing to obsolete.

Design record: the After the Clear artefact (agreed shape, 2026-09-03) and the Orphans of the Clear artefact (exploration and options), both linked from the issue.

## Goals

- The post-clear sweep runs only when it can be correct: inside a Full Synchronisation, after a Full Import of the same system has completed successfully later than the arming, and never on an empty or half-rebuilt Connector Space.
- After a clear and a successful re-import, an object that did not return receives exactly the treatment an import-detected deletion would have given it: values recalled (#1549, unchanged) and its type's Deletion Rule applied with honest attribution, grace period, policy snapshot and re-join cancellation.
- A re-import that returns far fewer objects than the clear removed is refused for reconciliation, loudly, until the administrator re-imports or raises the threshold.
- Historical strays (clears that predate this feature) are found from state and given their type's rule where the rule is state-convergent.
- The administrator is told what will happen before the clear, what is waiting on the Connected System page, and what the Full Synchronisation did, refused, or was not armed to do.
- Zero cost on ordinary runs: a Full Synchronisation that is not armed pays one nullable-timestamp read.

## Non-Goals

- **No clear-time choice.** A "clear only" option was considered and rejected: with the Full Import gate it protects against nothing an ordinary Full Import would not also do; decommissioning is the Connected System deletion flow (#809), which asks its own question; the "freeze the Metaverse" intent is per-type policy already (a Manual Deletion Rule, or contributed-value removal on obsoletion switched off); and the option reads as the safe pick while re-enabling the #1549 defect.
- **No Pause Synchronisation.** Protecting the maintenance window itself is #1619, a post-v1.0.0 follow-on; nothing here depends on it.
- **No Run Profile caps.** Max creates, updates and deletes on export and the obsoletion threshold on Full Import are #1618. This PRD introduces one threshold setting the sweep needs; #1618 may later move or override it per Run Profile.
- **No change to Deletion Rule semantics.** The sweep calls the same evaluation the obsoletion path calls, with the same marking, grace and cancellation machinery.
- **No reach into Specific-sources mode for historical clears.** That mode is event-only; no join record exists for clears that predate the feature, and evaluating it from state would delete objects the rule was never meant to reach.

## User Stories

1. As an administrator rebuilding a Connector Space, I want a Full Synchronisation run before my re-import to do nothing destructive and tell me why, so a Schedule or a colleague cannot turn my maintenance into a mass recall or deletion.
2. As an administrator whose re-import returned fewer objects than before because people left, I want the departed objects' Metaverse Objects to follow their type's Deletion Rule exactly as they would after any other import, so identities do not linger forever with stale data.
3. As an administrator whose re-import silently returned a fraction of the population because a filter was wrong, I want JIM to refuse the reconciliation and say so, rather than deleting most of my Metaverse.
4. As an administrator upgrading a deployment that has cleared Connector Spaces in the past, I want the orphaned objects those clears left behind to be found and given their type's rule, so the backlog does not persist indefinitely.
5. As an operator reviewing a run, I want the Full Synchronisation Activity to state whether the reconciliation ran, was refused and why, or was not armed, so the run's effects are auditable.

## Functional Requirements

### The gate (layer 1)

1. `ConnectedSystem.StrandedValueSweepPending` (bool) becomes `ConnectedSystem.StrandedValueSweepArmedAt` (nullable UTC timestamp). Armed means not null. Every clear stamps it with the clear's time; the sweep clears it on completion; an interrupted sweep leaves it set. The migration stamps the migration time for every row whose flag is true, then drops the flag, so the #1549 upgrade backfill also waits for a Full Import.
2. `ConnectedSystem.LastSuccessfulFullImportCompletedAt` (nullable UTC timestamp) is stamped by the worker when a Full Import run's Activity completes as `Complete`, or as `CompleteWithWarning` where the warning came from a connector-level warning message and no object-level errors were recorded. An import that completed with object-level errors, with errors, failed, or was cancelled does not stamp it: an object that failed to import is not staged, and the sweep would otherwise treat it as departed.
3. `ExecuteStrandedValueSweepIfArmedAsync` runs the sweep only when `LastSuccessfulFullImportCompletedAt` is later than `StrandedValueSweepArmedAt`. Otherwise it leaves the arming in place and appends to the run's Activity Message a sentence stating that the sweep is armed (with the arming time), was skipped because no Full Import has completed successfully since, and that a Full Import followed by a Full Synchronisation is the remedy. Delta Synchronisation remains untouched.
4. The Connected System API DTO and PowerShell output expose both timestamps. The Connected System page shows a notice while a sweep is armed: waiting for a Full Import, or (once one has completed since the arming) waiting for a Full Synchronisation.
5. The clear dialog, the clearing section of the public docs and the changelog state the gate.

### The join record (layer 2)

6. As step zero of the clear's transaction, one `INSERT ... SELECT` records (Connected System id, Metaverse Object id, cleared-at) for every Connected System Object of the system that is joined. The record is consumed and deleted when the sweep completes, replaced by a re-clear that precedes any sweep (delete then insert), and deleted as a new step of Connected System deletion.

### Deletion Rule evaluation in the sweep (layer 2)

7. After the value recall (unchanged from #1549), for each recorded Metaverse Object that still has no joined Connected System Object of this system: evaluate the object's type Deletion Rule event-shaped, with the cleared system as the disconnecting system, using the same evaluation and marking code the obsoletion path uses. Manual rules do nothing. Grace types are marked with the full marker set and policy snapshot; a later re-join cancels the marking through the existing cancellation. No-grace fates are flushed with the #809 batch machinery: capture reference-recall context, stage deletion-cascade exports, delete with change records, stage reference-recall exports.
8. Every evaluated object gets a Run Profile Execution Item under the run's Activity, with the policy snapshot, as the obsoletion path records. The Activity's summary gains the counts: evaluated, marked, deleted, exports staged.

### Re-join shortfall check (layer 2)

9. A new Service Setting, `Sync.PostClearReconciliation.MaxMissingPercent` (integer, default 10, category Synchronisation), bounds the share of recorded objects that may lack a re-join before the sweep refuses. When the share exceeds it, the sweep does not run (neither the value recall nor the deletion evaluation), stays armed, and the Activity states the counts, the threshold and the remedy (re-import, or raise the setting). Surface parity for the setting comes free from the Service Settings catalogue (portal, REST, PowerShell); the docs list it.

### State-convergent zero-join pass (layer 2)

10. In every armed sweep, after requirement 7, every Projected Metaverse Object with zero joined Connected System Objects, no existing deletion marking, and a type whose Deletion Rule is state-convergent (When Last Connector Disconnected, or When Authoritative Source Disconnected in the all-sources trigger mode) is marked or deleted per its rule, with a null triggering system and a policy-snapshot reason naming state convergence, and rendered honestly on the Pending Deletions page ("no longer connected to any source" rather than a system name). Specific-sources mode is excluded.
11. The same pass runs as the final step of Synchronised Deprovisioning, so a system cleared and then deleted without an intervening Full Synchronisation leaves no orphans behind.

### Copy and reporting (both layers)

12. The clear dialog states what the clear discards (objects and Pending Exports), that the Metaverse is not changed by the clear, and what the next Full Import and Full Synchronisation will do, including the Deletion Rule consequence and the shortfall refusal. It asks nothing.
13. The Full Synchronisation Activity always says one of: not armed (nothing said, as today), armed and skipped (with reason), refused on shortfall (with counts), or executed (with counts, zero-findings case explicit).

## Examples and Scenarios

- **Routine reset.** Clear, Full Import, Full Synchronisation. Every recorded object re-joins before the sweep; the sweep finds nothing to recall or evaluate, the state pass finds no zero-join objects, the record is deleted, the arming clears. Activity states zero findings.
- **Full Synchronisation before any import.** The gate refuses; nothing is recalled or evaluated; the Activity names the arming time and the remedy; the arming remains.
- **Clear then partial re-import.** The departed objects' values are recalled (#1549) and their types' Deletion Rules applied: grace types marked, no-grace types deleted with their exports. A re-import within the grace window cancels the marking.
- **Broken re-import.** A filter change returns 30% of the population. The share missing exceeds the threshold; the sweep refuses, stays armed, and the Activity states the counts. The administrator fixes the filter, re-imports, and the next Full Synchronisation reconciles.
- **Historical strays.** After upgrade, each system's first Full Synchronisation following a Full Import runs the state pass and gives wholly orphaned convergent-rule objects their rule.
- **Clear then delete the system.** Synchronised Deprovisioning's final step runs the state pass, so the clear-then-delete hole closes.

## Constraints

- Synchronisation Integrity rules in full: fast/hard failures, all errors via RPEIs/Activities, batch summary statistics.
- No new deletion semantics; reuse `EvaluateMvoDeletionRule`, the marking helpers, the re-join cancellation and the #809 batch flush.
- Layering: portal/REST call `JimApplication` only; the sweep runs in the worker's Full Synchronisation processing.
- The join record's `INSERT ... SELECT` and the zero-join query need real-PostgreSQL test coverage.

## Dependencies

- #1549 stranded-value sweep (shipped, unreleased): its flag becomes the armed-at timestamp.
- #809 Synchronised Deprovisioning batch machinery and #119 policy snapshots (shipped).
- #1618 (Run Profile safeguards) and #1619 (Pause Synchronisation) are related, not dependencies.

## Acceptance Criteria

- [ ] Armed-at timestamp replaces the flag; migration converts existing armed rows; clear stamps it; sweep clears it.
- [ ] `LastSuccessfulFullImportCompletedAt` is stamped only for successful Full Imports, per requirement 2, with unit coverage of every status branch.
- [ ] A Full Synchronisation with an arming newer than the last successful Full Import skips the sweep, keeps the arming, and says so on the Activity; Scenario 7 proves it end to end.
- [ ] The Connected System page, API DTO and PowerShell output expose the armed and last-import timestamps; the notice reads correctly in both waiting states.
- [ ] The clear records its join set inside the clear's transaction; re-clear replaces it; system deletion removes it; proven on real PostgreSQL.
- [ ] The sweep evaluates Deletion Rules for recorded objects still lacking a join, with the same outcomes as the obsoletion path (grace marking, immediate fate via the #809 flush, re-join cancellation), proven by red-first unit tests and Scenario 7.
- [ ] The shortfall check refuses above the threshold and the setting is visible on every Service Settings surface.
- [ ] The zero-join pass finds and processes historical strays and runs at the end of Synchronised Deprovisioning; the Pending Deletions page renders the null-system attribution honestly.
- [ ] Dialog, docs and changelog updated as described.
