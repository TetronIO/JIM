# Connected System Deletion Impact Preview - Implementation Plan

- **Status:** Doing (Phases 0 to 2 complete)
- **Issue:** [#134](https://github.com/TetronIO/JIM/issues/134)
- **PRD:** [`engineering/prd/done/PRD_CONNECTED_SYSTEM_SYNCHRONISED_DEPROVISIONING.md`](../../prd/done/PRD_CONNECTED_SYSTEM_SYNCHRONISED_DEPROVISIONING.md) (FR 6-11; decisions 2-5 of 2026-08-29)
- **Framework:** [`engineering/plans/done/CONFIGURATION_CHANGE_PREVIEW.md`](../done/CONFIGURATION_CHANGE_PREVIEW.md) (#827); execution side [`engineering/plans/done/CONNECTED_SYSTEM_SYNCHRONISED_DEPROVISIONING.md`](../done/CONNECTED_SYSTEM_SYNCHRONISED_DEPROVISIONING.md) (#809)
- **UX artefact:** [Deletion Impact Preview](https://claude.ai/artifact/D36Chj1ZxUKHn6sE2C9vHQ) (the scenario walkthrough this plan's BDD section is drawn from)

## Overview

Deleting a Connected System with **Deprovision through synchronisation** (#809) recalls the values it contributed, hands attributes to surviving contributors, evaluates deletion rules and sends corrective exports downstream. Today the administrator sees only counts (#135) before committing. This plan delivers #134: a read-only **Configuration Change Preview adapter** for Connected System deletion that answers, before anything happens:

- which Metaverse Object attribute values would be **cleared** (no other contributor);
- which would **change value** because another Connected System takes over, and which one;
- which Metaverse Objects would become **eligible for deletion**;
- what would be **exported downstream**, per target Connected System, including objects that would be **deprovisioned** because a recalled value takes them out of an export rule's scope.

It reuses the #827 framework end to end (panel, stages, sampling prompt, Activity, REST, PowerShell) and runs the same obsoletion core #809 executes, so the preview and the deletion cannot disagree.

## Business Value

The failure #134 was filed for is real and expensive: an HR migration moves most Attribute Priority to the new source, one or two attributes are left on the old one, the old system is deleted, and Active Directory accounts lose their manager or drop out of scope and are disabled. Nothing in the portal today shows that coming. The preview turns "delete and hope" into "delete having read exactly what will change", and the deletion Activity records whether the administrator looked first, which is the audit answer high-trust customers ask for.

## UX: behaviour-driven scenarios

These scenarios are the acceptance criteria. Each is implemented test-first at the lowest layer that can prove it (bUnit for portal behaviour, unit tests for the adapter's classification, PostgreSQL-backed tests for the harness, Pester for PowerShell, one integration scenario end to end). Wording in quotes is indicative copy, finalised in review.

The reference data used throughout: **Old HR System** (being deleted, 12,847 Connected System Objects), **New HR System** (the replacement source), **Active Directory** (export target). `manager` and `employeeStatus` were left with Old HR System as their only contributor; `department` and `costCentre` priority was moved to New HR System.

### Where the preview lives

```gherkin
Feature: Preview what deleting a Connected System would do

  Background:
    Given I am an administrator viewing the "Old HR System" Connected System
    And it has 12,847 Connected System Objects joined to Metaverse Objects that New HR System also contributes to

  Scenario: The Danger Zone offers a preview before the delete button
    When I open the Danger Zone tab
    Then I see a "Deletion impact" section above the "Delete Connected System" button
    And it says the system's deletion has not been previewed
    And it offers "Preview deletion impact"

  Scenario: The delete dialog points to the preview when none has been run
    When I click "Delete Connected System"
    Then the dialog shows the existing counts and the deletion mode choice
    And it says "Not previewed. The deletion's Activity will record that it went ahead without one."
    And "Preview first" closes the dialog and starts the preview on the Danger Zone tab
```

### Running it

```gherkin
  Scenario: A large system asks how much object detail to keep
    Given the preview is estimated to produce more rows than the full data set prompt threshold
    When I click "Preview deletion impact"
    Then the existing data set size dialog asks whether to keep a sample of each group or every row
    And choosing either starts the preview

  Scenario: The preview runs in the background and survives leaving the page
    When the preview starts
    Then the Danger Zone shows the preview panel with progress "4,210 / 12,847 - Evaluating Connected System Objects"
    And I can cancel it
    When I navigate away and return to the Danger Zone tab
    Then the panel reattaches to the same running preview

  Scenario: Deleting while a preview is running
    Given a deletion preview for this system is still running
    When I open the delete dialog
    Then it says "A preview is still running (33%). Deleting now cancels it, and the deletion will record that no preview informed it."
```

### Reading the answer

```gherkin
  Scenario: The precedence mistake is caught
    When the preview completes
    Then the verdict leads with "312 objects would become eligible for deletion."
    And its detail continues worst first: "37 objects would be removed from their target Connected System. 1,021 objects would have a value replaced by another contributor's. 662 objects would have a value cleared. 1,204 objects would be updated in their target Connected System."
    And "What would change" lists one row per consequence, sorted by consequence then object count:
      | Change                          | Applies to                              | Attribute      | Objects |
      | Becomes eligible for deletion   | person objects                          |                |     312 |
      | Removed from the target system  | user objects in Active Directory        |                |      37 |
      | New contributor, value changes  | person objects, now from New HR System  | department     |     800 |
      | Value cleared                   | person objects                          | manager        |     650 |
      | New contributor, value changes  | person objects, now from New HR System  | costCentre     |     540 |
      | Value cleared                   | person objects                          | employeeStatus |      37 |
      | Updated in the target system    | user objects in Active Directory        | department     |     800 |
      | Updated in the target system    | user objects in Active Directory        | manager        |     650 |
      | Updated in the target system    | user objects in Active Directory        | costCentre     |     540 |
      | Contributed values kept         | position objects                        |                |     214 |
      | New contributor, same value     | person objects, now from New HR System  | displayName    |  11,950 |
    And the 37 removals drill down to "employeeStatus would be cleared, so they leave the export rule's scope"

  Scenario: Drilling into a group shows each object's before and after
    When I click the "Value cleared, manager" row
    Then I see each affected Metaverse Object with its current value and "(no value)" as the proposed value
    When I click the "New contributor, value changes, department" row
    Then each row shows the current value from Old HR System and the proposed value from New HR System

  Scenario: A takeover with an identical value is reported but not alarming
    Given New HR System holds the same displayName as Old HR System for 11,950 objects
    Then those appear as "New contributor, same value", sorted last
    And they are excluded from the verdict and produce no export rows

  Scenario: Objects becoming eligible for deletion keep their values until they go
    Given 312 Metaverse Objects have Old HR System as their last connector and a 30-day grace period
    Then they appear once, as "Becomes eligible for deletion"
    And their values are not reported as cleared, because the grace period freezes them

  Scenario: Values kept by policy are said to be kept
    Given an Object Type has "Remove contributed attributes on obsoletion" switched off
    Then its contributed values appear as "Contributed values kept"
    And none of them appear as cleared or exported

  Scenario: Deleting a system nothing depends on
    Given a Connected System with no joined Metaverse Objects
    When the preview completes
    Then it says "Deleting this system would change nothing outside it."
    And it still says the system's own Connected System Objects are deleted with it
```

### Acting on it

```gherkin
  Scenario: Fixing precedence makes the preview out of date
    Given a completed deletion preview
    When I change a Synchronisation Rule's Attribute Flow priority, or an import runs, after the preview started
    Then the panel says the preview may no longer be accurate and offers "Run again"
    And the delete dialog treats it as stale

  Scenario: Deleting with a current preview
    Given a completed, current deletion preview
    When I open the delete dialog
    Then the reserved preview slot shows "Complete", its age, the verdict's top lines and "Open full preview"
    When I type the system name and confirm
    Then the deletion's Activity records the preview's Activity id

  Scenario: Choosing "Delete immediately" after previewing
    Given a completed deletion preview
    When I choose "Delete immediately and keep contributed data"
    Then the preview summary says "This preview describes deprovisioning through synchronisation. Deleting immediately clears none of these values and sends no exports; the 312 objects still become eligible for deletion."

  Scenario: The audit trail shows whether the administrator looked first
    Given a Connected System was deleted
    When I open the deletion's Activity
    Then it shows "Informed by preview" linking to the preview's Activity and its verdict
    Or it shows "Went ahead without a preview"
```

### Scripting it (surface parity)

```gherkin
  Scenario: Previewing a deletion from PowerShell
    When I run "New-JIMConfigurationChangePreview -ConnectedSystemId 3 -Deletion -Wait"
    Then I receive the preview with its verdict, impact counts and groups
    And "Get-JIMConfigurationChangePreviewDelta" pages its object-level rows

  Scenario: Linking a scripted deletion to its preview
    When I run "Remove-JIMConnectedSystem -Id 3 -PreviewActivityId <id>"
    Then the deletion's Activity records that preview, exactly as the portal does
```

REST mirrors both: `POST /api/v1/connected-systems/{id}/deletion/preview` (202 with `ConfigurationChangePreviewStartResponse`), read back through the existing generic `previews/{activityId}` endpoints, and `DELETE /api/v1/connected-systems/{id}?previewActivityId=` on the existing delete endpoint.

## Technical Architecture

### Current state

- **Framework (#827):** adapters implement `IConfigurationChangePreviewAdapter` (`ValidateAsync`, `EstimateCostAsync`, `CountImpactAsync`, `EvaluateDeltasAsync`); `ConfigurationChangePreviewServer` orchestrates stages, worker dispatch and persistence; `PreviewSummariser` groups deltas by (transition, Metaverse Object Type, object type, Connected System, attribute). Registration is by hand in `JimApplication`.
- **Execution seam (#809):** `ConnectedSystemObjectObsoletionService.ProcessObsoleteConnectedSystemObjectAsync` is the shared obsoletion core; its own documentation names "the future #134/#827 preview adapter" running it in a read-only harness. `ContributorRecallScope.ForDeletedConnectedSystem` already exists. Execution then runs a **by-provenance residue pass** per import Synchronisation Rule before the final deletion.
- **Read-only machinery (#288):** `ReadOnlySyncRepositoryGuard`, `BeginRollbackOnlyTransactionAsync` and the pure engine functions (`SyncEngine.DecideOutboundStaging`, `ComputeAttributeValueChanges`) guarantee zero side effects; see `engineering/SYNC_PREVIEW_ZERO_SIDE_EFFECTS.md`.
- **Portal:** every surface hosts `ConfigurationChangePreviewPanel` inline on its page; none hosts it in a dialog. `DeleteConnectedSystemDialog` is a small dialog with a disabled "Preview attribute impact" placeholder.
- **Gaps:** the delete path (`ConnectedSystemServer.DeleteAsync`, the dialog, REST, `Remove-JIMConnectedSystem`) has no `previewActivityId`, so PRD decision 2 ("record whether a preview was run") is not implemented; and no surface shows the preview that informed a change on that change's Activity.

### What cannot be reused as-is

1. **The #809 executor** requires the Deleting fence and persists every batch; the preview calls the core, not the executor.
2. **`EvaluateExportRulesWithNoNetChangeDetectionAsync`** writes even with `deferSave: true` (appending to or deleting an existing Pending Export), so under the guard it throws for any target with a Pending Export. The preview uses the pure engine instead, with `recallSemantics: true`.
3. **`ExportEvaluationServer.EvaluateOutboundPreviewAsync`** hard-codes `recallSemantics: false` and treats every value as changed; it cannot express removals or re-elections.
4. **`SyncPreviewServer.BuildOutOfScopeCascadeAsync`** re-implements the core with the wrong scope and per-object remaining-connector logic; not a model for this.
5. **The residue pass** (`RecallSyncRuleContributedValuesAsync`) uses `Application.SyncRepo` directly and persists per batch; the preview needs a read-only equivalent.

### Proposed design

**Read-only deprovisioning harness** (`JIM.Application`, new `DeprovisioningPreviewHarness`): pages the system's Connected System Objects exactly as execution does and, per page, inside one rollback-only transaction behind the guard:

- clones the Connected System Objects and their Metaverse Objects (lift `SyncPreviewServer.CloneForPreview` into a shared helper);
- runs the obsoletion core with `ForDeletedConnectedSystem` scope, the same priority context (make `BuildRecallPriorityContextAsync` internal), an **evaluate-only** deletion-rule delegate, and no generated-value resolver (mirroring execution, which clears unresolved markers);
- answers "remaining connectors" against the **end state**: a decorator treats every one of this system's Connected System Objects as gone, so a Metaverse Object holding two of this system's objects gets the same verdict execution's end-of-run orphan backstop gives it;
- turns each core result into deltas: a removal with a matching addition is a contributor takeover (value changed or unchanged), a removal without one is a clear, `MvoDeletionDecision` is eligibility;
- evaluates outbound consequences with `DecideOutboundStaging(recallSemantics: true)` and `ComputeAttributeValueChanges(removedAttributes)` over a guarded export evaluation cache, plus `DecideMvoDeletionExport` for immediate deletions and export-scope exits;
- runs a read-only residue pass per import Synchronisation Rule after the object pass, so stranded values are counted.

**Adapter** (`ConnectedSystemDeletionPreviewAdapter`):

- Surface `ConnectedSystemDeletion = 10` (append-only), mapped to `ActivityTargetType.ConnectedSystem`, with a `BuildActivity` case and registration.
- Proposal `ConnectedSystemDeletionProposal`: no fields beyond the target id, which travels on `PreviewContext.TargetId`. It always previews deprovisioning through synchronisation; the immediate mode has no per-object consequences beyond eligibility, which the portal states as a contrast line rather than a second preview.
- `ValidateAsync`: blocking finding when the system no longer exists or is already being deleted (part of what a preview would describe has happened); blocking when the surviving derived flows form a cycle, which the real run refuses; one warning per Metaverse attribute the deletion withdraws that a surviving derived flow reads, naming the hosting systems (the real run marks those objects for re-derivation rather than re-deriving, so the derived values are not shown). Reference recall needed no warning: Phase 1 covers it.
- `EstimateCostAsync`: Connected System Object count by the distinct Metaverse attributes the system's import rules contribute (enabled or not, since the residue pass recalls by provenance), read from the store the real run sizes itself from. Data-derived rather than a constant measured on one fixture, so it tracks each system's shape.
- `CountImpactAsync`: streams `EvaluateDeltasAsync`, as every engine-driven adapter does, documenting the departure from set-based SQL. One count per transition, of distinct objects (the target's object for export transitions, the Metaverse Object otherwise), because the verdict states each count as "N objects would ...".

**Transitions** (append to `ActivityRunProfileExecutionItemSyncOutcomeType`, display entries in `OutcomeDisplayMap`, vocabulary tests):

| Transition | Label | Use | Tone |
|---|---|---|---|
| `WouldBecomeDeletionEligible` (existing) | Becomes eligible for deletion | Last connector gone | Error |
| `WouldStageDeleteExport` (existing) | Removed from the target system | Deprovisioned from a target, including export-scope exits caused by a cleared value | Error |
| `WouldTakeOverContributedValue` (new) | New contributor, value changes | The winning system rides on the delta's `ConnectedSystemId` | Warning |
| `NoContributor` (existing, gains the sentence form "have a value cleared") | Value cleared | No surviving contributor | Warning |
| `WouldStageUpdateExport` (new) | Updated in the target system | Corrective update to a target system, per attribute | Info |
| `WouldRetainContributedValues` (existing) | Contributed values kept | Kept by Object Type policy | Info |
| `WouldTakeOverSameValue` (new) | New contributor, same value | Contributor changes, value identical; omitted from the verdict, no export | Secondary |

Panel changes, each small and each benefiting every surface where it applies:

- **Order by consequence, then count.** The summary grid sorts by object count today, which would put 11,950 same-value takeovers above 37 deprovisions. It adopts the verdict's own ordering (tone weight, then count), so the grid and the sentence above it agree.
- **Verdict omissions.** A display entry can mark a transition as having no effect worth stating (same-value takeover); the verdict skips it.
- **"Applies to" for takeovers** reads "person objects, now from New HR System" rather than "in New HR System".
- **Values name their contributor** in the drill-down. The old side is always the system being deleted; the new side is the delta's `ConnectedSystemId`.
- **Adapter-supplied empty copy.** The generic "This change would not change anything" is false for a deletion (the system's own objects go), so the adapter supplies "Deleting this system would change nothing outside it".

**Portal:**

- `ConnectedSystemDangerZoneTab` hosts the panel inline (the pattern every other surface uses), finds and reattaches to the latest deletion preview for the system, and shows staleness.
- `DeleteConnectedSystemDialog` replaces the disabled placeholder with a compact preview summary (state, age, the verdict's top lines, "Open full preview"), passes `previewActivityId` only when the preview is complete and current, and shows the running and immediate-mode lines above.
- `ActivityDetail` shows "Informed by preview" (or "Went ahead without a preview" for deletions) on any change Activity carrying `PreviewActivityId`. This benefits every surface, not just this one.
- **Staleness:** the existing import baseline, plus a new check for any sync-affecting configuration change Activity (the #827 classification already records the class) recorded after the preview started. This is the scenario that matters most here: the administrator fixes precedence and must re-run.

**REST and PowerShell:** a start endpoint on `SynchronisationController`, `previewActivityId` on the delete endpoint and `ConnectedSystemServer.DeleteAsync`, a `-Deletion` switch parameter set on `New-JIMConfigurationChangePreview`, and `-PreviewActivityId` on `Remove-JIMConnectedSystem`, each with tests and help.

## Implementation Phases

### Phase 0: residue pass honours the recall setting (bottom stack layer, discovered work) ✅

The #809 residue pass recalls by provenance with **no** `RemoveContributedAttributesOnObsoletion` filter. For an Object Type with recall switched off, the per-object pass keeps the values (provenance intact) and the residue pass then recalls them anyway. That contradicts PRD decision 3 ("honour the per-Object Type setting unchanged") and the stranded-value sweep, which skips such rules for exactly this reason. No test pins it.

- [x] Failing test: deprovision a system whose Object Type has recall off; assert its contributed values survive and nothing is staged downstream (`ExecuteSynchronisedDeprovisioningAsync_RecallDisabledObjectType_ResiduePassKeepsContributedValuesAsync`).
- [x] Fix: skip residue recall for rules whose Connected System Object Type has recall off, mirroring `StrandedValueSweep`. Unlike the sweep, an unresolvable Object Type fails the run hard (system stays fenced) rather than recalling regardless, since a wrong recall cannot be undone (`..._ResidueRuleWithUnresolvableObjectType_FailsHardAndStaysFencedAsync`).
- [x] Changelog `🐛` entry (user-facing data integrity fix), and the public "Removing a Connected System" docs now state the exception.

Lands first so the preview mirrors correct behaviour rather than encoding the defect. Fixed as part of this work (decision 2).

**Second defect, found designing the Phase 1 harness (decision 4, product owner, 2026-10-05: fix it here).** A synchronisation that withdraws values runs two outbound evaluations for the object: corrective updates for the export rules it is still in scope of, and deprovisioning for any it has left (`SyncTaskProcessorBase.EvaluateOutboundExportsAsync`). Every recall executor ran only the first, so a recall that took an object out of an export rule's scope left its target account provisioned, with nothing to revisit it. That contradicted PRD FR 3 and would have made the preview's "Removed from the target system" row false.

- [x] Failing tests: Connected System deprovisioning (Delete and Disconnect actions) and Synchronisation Rule deletion recall, each with an export rule scoped on the recalled attribute.
- [x] Fix: one shared `DeprovisionRecallScopeExitsAsync`, called after in-scope staging by Synchronised Deprovisioning's per-object pass and by `RecallSyncRuleContributedValuesAsync` (Synchronisation Rule deletion, the residue pass, the stranded-value sweep); each staged deprovisioning is recorded on the object's execution item as synchronisation records it.
- [x] Changelog `🐛` entry; public docs for Synchronisation Rule deletion, Connected System removal and Attribute Priority updated.

### Phase 1: read-only deprovisioning harness

- [x] Preview-owned clones of each page's Connected System Objects and Metaverse Objects (one Metaverse clone per page, so two of the system's objects joined to one identity act on one working copy).
- [x] End-state remaining connectors: the evaluate-only deletion delegate strips every one of the deleted system's objects before asking the engine.
- [x] Harness over the obsoletion core with evaluate-only deletion and no generated-value resolver (`ConnectedSystemServer.PreviewSynchronisedDeprovisioningAsync`).
- [x] Recall-aware outbound staging (`EvaluateOutboundPreviewForMaterialisedMvosAsync` with recall semantics), export-scope exits, immediate-deletion cascades, last-connector marking after a scope-exit disconnect.
- [x] Read-only residue pass, skipping recall-off types as the fixed executor does.
- [x] **Equivalence (the guarantee):** `ConnectedSystemDeletionPreviewWorkflowTests` previews, then runs the real #809 execution on the same state, and asserts the two describe identical facts (values cleared and taken over with their new source, eligibility, values kept, export updates and deletions, disconnections) across sole contributor, takeover with a different and the same value, recall-off types, export-scope exits with Delete and Disconnect, last connector immediate and with grace, the authoritative-source cascade, stranded values, and Manager references both taken over and cleared. These run on the in-memory harness, where both sides share one store.
- [x] Zero side effects on PostgreSQL (`ConnectedSystemDeletionPreviewDatabaseTests`): every adapter stage on one context, then the caller's save, leaves the integrity tables plus everything the deletion writes byte-identical (content digests, not only counts), under tracking and NoTracking. Checked red: working on the tracked originals instead of clones fails it. The same fixture runs the real executor and proves the scope-exit Delete is staged and the run completes.

### Phase 2: adapter and transitions

- [x] Surface `ConnectedSystemDeletion = 10`, `ConnectedSystemDeletionProposal`, `ConnectedSystemDeletionPreviewAdapter`, registration, `BuildActivity` case, surface tests (`ConnectedSystemDeletionPreviewAdapterTests`, server and model surface tests).
- [x] New transitions with ordinal pins, display entries and vocabulary tests.
- [x] Classification tests per transition, including the same-value takeover and frozen-value cases.

### Phase 3: portal

- [ ] Danger Zone panel hosting, reattachment, staleness (bUnit).
- [ ] Dialog summary block, running and immediate-mode lines, `previewActivityId` passed only when current (bUnit).
- [ ] Panel changes listed under Transitions: consequence-then-count order, verdict omissions, takeover "Applies to", contributor-named values, adapter empty copy (bUnit for the ordering and omission logic; copy verified by eye).
- [ ] "Informed by preview" on Activity detail.
- [ ] Copy (text-only, verified by eye):
  - Delete dialog, "Joined" note: "related objects; deprovisioned per the choice above" reads as though every joined Metaverse Object will be deprovisioned. Becomes "disconnected from this system; they stay in the Metaverse unless this was their last connector, below".
  - Delete dialog, "Eligible for deletion rules" note: "their own rules decide, when deprovisioning" wrongly implies the immediate mode skips them. Becomes "this system is their last connector; their Object Type's deletion rule decides".
  - Shared preview panel staleness line: "An import since then may have moved the answer." becomes "An import since then may change the preview results."
- [ ] Runtime validation on the sandbox stack against a two-HR-source fixture; screenshots against the artefact.

### Phase 4: REST and PowerShell

- [ ] Start endpoint, `previewActivityId` on delete, controller tests.
- [ ] `New-JIMConfigurationChangePreview -Deletion`, `Remove-JIMConnectedSystem -PreviewActivityId`, Pester tests, help examples.

### Phase 5: integration, docs, close-out

- [ ] Integration scenario (extending the Attribute Priority fixture): preview, assert verdict and groups; delete with the preview; assert execution matches.
- [ ] Docs: deleting a Connected System, reading the deletion impact preview; changelog `✨` entry.
- [ ] Correct the stale references: #134's `docs/CONNECTED_SYSTEM_DELETION_DESIGN.md` path, and the `plans/doing/` paths in `DeleteConnectedSystemDialog` and `ConsequenceConfirmationDialog` comments.
- [ ] Remove or populate `ConnectedSystemDeletionPreview.MvosWithOtherConnectorsCount` and `MvosWithGracePeriodCount`, which are never set.
- [ ] Move this plan to `done/` in the PR that closes #134.

## Success Criteria

- Every scenario above passes as an automated test at its layer.
- The equivalence test proves preview and execution agree on the reference fixture.
- A preview changes no row in the database.
- A deletion Activity always says whether a preview informed it, from all three surfaces.
- A 100,000-object system previews in the worker without exhausting memory, at a cost in the same class as a full synchronisation of that system.

## Benefits

- Closes the most dangerous blind spot left after #827: the one deletion that fans out to every downstream system.
- One engine for preview and execution, enforced by a test rather than by convention.
- "Informed by preview" on Activity detail improves every existing preview surface.

## Dependencies

- #827 framework and #288 read-only machinery (shipped).
- #809 execution and its obsoletion core (shipped).
- Phase 0 fix before Phase 1.
- No new packages.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Preview and execution drift as either evolves | Equivalence test on a shared fixture; both call one core |
| A write leaks from the dry run | Guarded repository throws on any write; rollback-only transaction; zero-side-effects test |
| Cost at large scale | Worker dispatch above the threshold, paged clones, sampling prompt, cancellation honoured per page |
| Administrators read a stale answer | Import and configuration-change staleness; dialog links only a current preview |
| Same-value takeovers drown the real changes | Separate Info transition, excluded from the verdict, sorted last |
| Reference-recall exports have no preview path anywhere yet | Covered in Phase 1 and proven equal to execution for a reference taken over and a reference cleared |
| Phase 0 changes shipped behaviour | It aligns execution with the decided PRD behaviour; changelog states it |

## Decisions

1. **Placement** (decided, product owner, 2026-10-05): the preview lives on the **Danger Zone tab**, with a compact summary in the delete dialog (option A). It matches every other surface and survives long runs. Rejected: widening the dialog to host the panel beside the consent controls (option B), which puts evidence and consent side by side but leaves a modal to wait in for minutes, stacks the size prompt on a dialog, and cramps the drill-down. Both are mocked in the UX artefact.
2. **Phase 0** (decided, product owner, 2026-10-05): the residue pass honours the recall setting, fixed as part of this work as the bottom stack layer.
3. **Same-value takeovers** (decided, product owner, 2026-10-05): a quiet group, omitted from the verdict and sorted last; the summary grid sorts by consequence then count on every surface.
