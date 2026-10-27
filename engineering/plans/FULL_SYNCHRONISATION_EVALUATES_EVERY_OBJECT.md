# Full Synchronisation Evaluates Every Object - Implementation Plan

- **Status:** Planned
- **Issue:** [#891](https://github.com/TetronIO/JIM/issues/891)
- **Related Issues:** [#2092](https://github.com/TetronIO/JIM/issues/2092) (changes committed mid-run skipped by every later Delta and Full Synchronisation), [#2309](https://github.com/TetronIO/JIM/issues/2309) (objects needing re-processing skipped until their own data changes), [#892](https://github.com/TetronIO/JIM/issues/892) (Temporal Scope Reconciler), [#1750](https://github.com/TetronIO/JIM/issues/1750) (Metaverse-Derived Attribute Flows), [#1530](https://github.com/TetronIO/JIM/issues/1530) (Full Synchronisation preview)
- **Related Plans:** [`doing/CONNECTED_SYSTEM_FULL_SYNCHRONISATION_PREVIEW.md`](doing/CONNECTED_SYSTEM_FULL_SYNCHRONISATION_PREVIEW.md) (shares the unchanged-object rule this plan removes)
- **Last Updated:** 2026-10-27

## Overview

A Full Synchronisation does not currently re-evaluate every Connected System Object. A joined, Normal object with no pending mark and no update since `LastSyncCompletedAt` is flagged `IsUnchangedSinceLastSync` and returns before Pass 2, so it gets no scoping evaluation, no Attribute Flow, no drift detection and no export evaluation for its Metaverse Object. The skip is only switched off for a run when a Synchronisation Rule or mapping row changed after the last completed Full Synchronisation.

This plan removes that skip. A Full Synchronisation will evaluate every object, every time. Delta Synchronisation remains the fast path for routine runs.

## Business Value

Administrators run a Full Synchronisation for three reasons: initialising a new Connected System, applying a configuration change to every object, and **remediating anything a Delta Synchronisation missed**. The skip defeats the third, because it trusts the same signals (timestamps and marks) whose failure is the reason for remediating. Synchronisation integrity is JIM's first priority, so a Full Synchronisation must be the independent backstop that does not depend on them.

The skip has already produced data integrity defects:

- **v0.15.0:** a pure configuration change (attribute priority, rule enable or disable, scoping) never reached unchanged objects. Fixed by adding configuration-change detection, which is itself only as complete as the inputs it watches.
- **#2092:** a change committed while a synchronisation was running was skipped "by every later Delta and Full Synchronisation, until the object changed again". A Full Synchronisation could not remediate it. Fixed by adding `SynchronisationPending`.
- **#2309:** objects needing re-processing because of a change outside their own data were skipped until that data changed. Fixed by recording Synchronisation Rule deletions on Service Settings.
- **#891:** time-dependent scoping is never re-evaluated by a Full Synchronisation; the Temporal Scope Reconciler (#892) was built partly to compensate.

Each fix closes one known way in. Cases that remain open today, with no fix possible while the skip exists:

- A JIM upgrade that fixes an Attribute Flow or scoping defect never re-flows the wrong values already in the Metaverse, because the code version is not treated as a configuration change.
- Metaverse damage, or a lost Pending Export, on an object whose source data has not changed is never repaired.
- Configuration outside Synchronisation Rule and mapping rows does not switch the skip off.
- The documentation recommends a Full Synchronisation "for periodic reconciliation"; with the skip on, it reconciles nothing that is unchanged.

With the skip on, a Full Synchronisation is roughly a Delta Synchronisation plus unjoined objects plus automatic detection of rule changes. It has collapsed into the fast path, and JIM already has one.

## Scope

**In scope:** the Full Synchronisation unchanged-object skip, the configuration-change detection that exists only to switch it off, the same rule's copy in the Full Synchronisation preview, and the tests, integration scenarios and documentation that describe or depend on it.

**Out of scope, deliberately kept:**

| Mechanism | Why it stays |
|---|---|
| Full Import content-hash skip (#1082) | Every object is still read from the source, so deletion detection is unaffected. Only the attribute comparison is skipped, and only when a hash of freshly read data matches the stored stamp. Delta Imports and export confirmations clear the stamp, and Verification Mode turns the skip off. A missed delta makes the hash differ, so Full Import still remediates. |
| `SynchronisationPending` (#2092) | Delta Synchronisation selects objects with it. A change committed during a run is stamped below the watermark that run writes, so without the mark Delta Synchronisation would never process it. |
| `DerivedInputChangePending` (#1750) | Delta Synchronisation selects objects with it; the object's own data has not changed, but a derived flow's input has. |
| `ScopeReviewPending` (#892) and the Temporal Scope Reconciler | Delta Synchronisation reads it (`SyncDeltaSyncTaskProcessor.cs:98, 379`); Delta remains blind to clock-driven scope changes without it. |
| `LastSyncCompletedAt` | The Delta Synchronisation watermark. |
| Full Synchronisation clearing those marks at page flush | Still needed, so the next Delta Synchronisation does not reprocess what the Full Synchronisation already handled. |

After this change the marks still have to be correct for Delta Synchronisation to be correct, but a mark that is missed becomes recoverable by running a Full Synchronisation.

## Technical Architecture

### Current state

1. `SyncFullSyncTaskProcessor.PerformFullSyncAsync` computes `csoLoadWatermark` from `ConnectedSystem.GetUnchangedObjectWatermark(GetLatestSyncRuleConfigurationChangeAsync())`, which returns `LastSyncCompletedAt` unless a rule or mapping changed after `ConfigurationLastFullyAppliedAt`.
2. `ConnectedSystemRepository.GetConnectedSystemObjectsAsync` has a watermark branch: it loads every object's scalars, flags those where `ConnectedSystemObject.IsUnchangedSince(watermark)` holds, and loads attribute and reference values only for the rest.
3. `SyncTaskProcessorBase.ProcessActiveConnectedSystemObjectAsync` returns early for a flagged object (`SyncTaskProcessorBase.cs:1160`).
4. At the end of a successful run, `ConfigurationLastFullyAppliedAt` is set to the run's start time.
5. `SyncPreviewServer` applies the same rule and reports watermark-skipped objects as `FullSyncPreviewItemKind.Unchanged`, counted in `FullSyncPreviewResult.UnchangedObjectCount`.

### Proposed state

A Full Synchronisation always loads every object page with the full Include chain (the existing no-watermark branch, already exercised by every first synchronisation and by every run after a configuration change) and runs Pass 1 and Pass 2 for every object. The preview evaluates every object as the run would.

### What is removed

| Area | Item |
|---|---|
| Worker | `csoLoadWatermark` block and `ConfigurationLastFullyAppliedAt` assignment in `SyncFullSyncTaskProcessor`; the early return at `SyncTaskProcessorBase.cs:1160`; `BuildReferenceObjectTypeCache` and `_objectTypesWithReferenceRules` (built but never read) |
| Models | `ConnectedSystemObject.IsUnchangedSinceLastSync`, `ConnectedSystemObject.IsUnchangedSince`, `ConnectedSystem.GetUnchangedObjectWatermark` (both overloads), `ConnectedSystem.ConfigurationLastFullyAppliedAt`, `ServiceSettings.SyncRuleLastDeletedAt` |
| Data | The watermark branch and `lastSyncTimestamp` parameter of `GetConnectedSystemObjectsAsync` (PostgreSQL, in-memory `FlagUnchangedObjects`, interfaces, `ReadOnlySyncRepositoryGuard`); `GetLatestSyncRuleConfigurationChangeAsync`; `GetConnectedSystemSynchronisationWatermarksAsync` if the preview is its only caller; the `SyncRuleLastDeletedAt` stamping in `ConnectedSystemRepository` (around lines 8048 and 8742) |
| Database | One new migration dropping `ConnectedSystems.ConfigurationLastFullyAppliedAt` and `ServiceSettings.SyncRuleLastDeletedAt` (released migrations are append-only) |
| Preview | `FullSyncPreviewItemKind.Unchanged`, the watermark lookup in `SyncPreviewServer`, `FullSyncPreviewResult.UnchangedObjectCount`, and the portal line that shows it, once confirmed it is fed only by the watermark skip (`ConfigurationChangePreviewPanel.razor:358`) |
| Tests | `UnchangedObjectOptimisationTests`, `SyncRuleDeletionConfigurationWatermarkDatabaseTests`, `SyncRepositoryConfigurationChangeTests`; skip-specific cases in `DerivedInputChangePendingDatabaseTests`, `SynchronisationPendingDatabaseTests`, `KeysetCsoPagingDatabaseTests`, `SyncRuleMappingUpdateTests`, `RemovedMappingRecallWorkflowTests`, `DerivedAttributeFlowWorkflowTests`, `SyncPreviewFidelityTests.*` (each reviewed, not deleted wholesale: keep any assertion that is about Delta Synchronisation) |

Synchronisation Rule and mapping `LastUpdated`/`Created` stamping stays; it is ordinary audit data with other readers.

## Implementation Phases

### Phase 1: Tests that fail today (red)

Worker and workflow tests (`test/JIM.Worker.Tests/`), each confirmed failing against current `main` for the right reason:

1. **Metaverse damage is repaired.** Join and synchronise an object; alter its Metaverse Object's contributed value directly; run a Full Synchronisation with no data or configuration change. Expect the value restored and recorded on an execution item.
2. **A missed mark is caught.** An object updated with a timestamp at or below `LastSyncCompletedAt` and no `SynchronisationPending` mark (the #2092 shape with the mark deliberately omitted) is processed by a Full Synchronisation.
3. **Clock-driven scope change is caught without the reconciler.** An object whose relative-date scoping criterion has passed, with static data and no `ScopeReviewPending` flag, is disconnected out of scope by a Full Synchronisation.
4. **Idempotency guard (green before and after, but now meaningful).** A second Full Synchronisation over unchanged data and configuration produces no Metaverse changes, no Pending Exports and no change-bearing execution items. Before this change it passes trivially because nothing is evaluated; after it, it proves evaluation of every object is a no-op when nothing changed.

Tests 1 to 3 must run against the PostgreSQL repository (database tests) as well as the in-memory one, since the in-memory repository models the watermark separately and EF in-memory tracking can mask loading gaps.

### Phase 2: Remove the skip (green)

Worker, model and repository removals from the table above, excluding the migration and preview. Run `dotnet build JIM.sln` and the Worker, Models and InMemoryData test projects.

### Phase 3: Remove configuration-change detection and migrate

Remove `GetLatestSyncRuleConfigurationChangeAsync`, `ConfigurationLastFullyAppliedAt`, `SyncRuleLastDeletedAt` and their writers; add the migration. Verify the migration applies cleanly on a seeded database in the full stack.

### Phase 4: Full Synchronisation preview

Remove the `Unchanged` kind and the watermark from `SyncPreviewServer` and `FullSyncPreviewResult`; update `SyncPreviewFidelityTests` so preview and run agree that every object is evaluated. Update the preview duration estimate's documentation (`docs/configuration/connected-systems.md:133`): its premise that "a routine run skips objects" no longer holds, though the estimate's conservative floor can stay. Confirm the change with the owners of the in-flight preview plan (#1530) so the two do not conflict.

### Phase 5: Integration scenarios

Two scenarios use a plain Full Synchronisation as a negative control that must **miss** a clock-driven scope change. After this change a Full Synchronisation catches it, so the controls invert:

- **Scenario 008, Step 7.6** (`Invoke-Scenario-008-CrossDomainEntitlementSync.ps1:1836`): switch the negative control to a **Delta Synchronisation**, which must still disconnect nothing without the reconciler. Add a positive check that a plain Full Synchronisation disconnects exactly the cohort, or keep the reconciler path and assert on it via Delta Synchronisation, whichever keeps the reconciler covered with the least scenario time.
- **Scenario 012, T4** (`Invoke-Scenario-012-RelativeDateScoping.ps1:398`): the reconciler-then-sync step should use a Delta Synchronisation, so it still proves the reconciler is what Delta needs; add an assertion that a Full Synchronisation alone also catches the transition.
- Update `engineering/INTEGRATION_TESTING.md` (Step 5 of the leaver cohort flow) to match.

Run Scenarios 008, 012, 013 and 026 at `Small`, plus the full suite at `Small` before the PR.

### Phase 6: Measure the cost

Run a repeat Full Synchronisation (no data changes) at `Scale100k50Groups` on `main` and on the branch, in the devcontainer, and record both durations and peak memory in this plan. The no-watermark path is already the one every first synchronisation takes at scale (#2492 measured it at 500k), so this is about repeat-run cost, not feasibility.

Decision gate: if the repeat run is impractical for scheduled use, the answer is documentation steering schedules to Delta Synchronisation, not restoring the skip. If the user decides otherwise at this gate, the fallback is an opt-in, default-off Run Profile setting (option B from the #891 review), planned separately.

### Phase 7: Documentation and changelog

- `docs/configuration/run-profiles.md:21`, `docs/concepts/synchronisation-pipeline.md:116`, `docs/reference/glossary.md:64`, `docs/configuration/sync-preview.md:43`, `docs/configuration/connected-systems.md:133`: a Full Synchronisation evaluates every object. State when to use each synchronisation type: Delta Synchronisation for scheduled runs; Full Synchronisation for a new Connected System, after configuration changes, after a JIM upgrade, and to remediate anything a Delta Synchronisation may have missed.
- `engineering/developer/diagrams/FULL_SYNC_CSO_PROCESSING.md` (lines 8, 22, 189), `PENDING_EXPORT_LIFECYCLE.md:244`, `EXPORT_EXECUTION_FLOW.md:224`: remove references to the unchanged-object fast path.
- `CHANGELOG.md` under `[Unreleased]`: a 🔄 entry, for example "Full Synchronisation now evaluates every Connected System Object on every run, so it repairs anything a Delta Synchronisation missed. It takes longer than before on large systems; schedule Delta Synchronisation for routine runs. (#891)".

## Success Criteria

- Phase 1 tests 1 to 3 fail on `main` and pass on the branch; test 4 passes on both.
- No code path reads a watermark to decide whether a Full Synchronisation processes an object (`grep` for `IsUnchangedSince`, `GetUnchangedObjectWatermark`, `ConfigurationLastFullyAppliedAt` and `SyncRuleLastDeletedAt` returns nothing outside migrations).
- Delta Synchronisation behaviour and its tests are unchanged.
- Full integration suite passes at `Small`; Scenarios 008 and 012 prove both that Delta Synchronisation needs the reconciler and that Full Synchronisation does not.
- Repeat Full Synchronisation cost at `Scale100k50Groups` recorded here, and the Phase 6 gate decided.
- `dotnet build JIM.sln` and `dotnet test JIM.sln` clean.

## Benefits

- **Integrity:** a Full Synchronisation is an independent backstop for every failure mode of the timestamps and marks Delta Synchronisation relies on, including ones not yet discovered.
- **Upgrades:** a Full Synchronisation after an upgrade applies fixed Attribute Flow and scoping logic to existing data.
- **Simplicity:** removes a second loading path, a configuration-change detector, two database columns and the preview's copy of the rule. Fewer special cases for each future mark.
- **Honest semantics:** "Full" means full; the duration estimate and docs no longer need caveats about skipped objects.

## Dependencies

None external. Coordinate with the in-flight Full Synchronisation preview plan (#1530), which shares `IsUnchangedSince`.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| **Latent non-idempotency surfaces.** Evaluating unchanged objects may stage spurious updates or Pending Exports on every run (DateTime precision, multi-valued ordering, case, Null handling) that the skip has been hiding. | Phase 1 test 4 and an integration-level check (a second Full Synchronisation on unchanged data reports no changes) catch these. Each one found is a real defect that already affects first synchronisations and post-configuration-change runs; fix it in its own stack layer below this one. |
| **Longer repeat Full Synchronisations** on large systems, and slower schedules that use Full Synchronisation routinely. | Phase 6 measurement and gate; documentation steers schedules to Delta Synchronisation. Pre-release, so there are no deployed schedules to break. |
| **Scenario semantics change** in 008 and 012, risking loss of Temporal Scope Reconciler coverage. | Phase 5 moves the reconciler assertions onto Delta Synchronisation, where the reconciler is still required. |
| **Preview and run diverge** while the preview plan (#1530) is in flight. | Phase 4 lands in the same PR as Phase 2; fidelity tests enforce agreement. |
| **Memory per page rises** because every object's attribute values are loaded. | This is the existing first-synchronisation path, already exercised at 500k (#2492); Phase 6 records peak memory. |
