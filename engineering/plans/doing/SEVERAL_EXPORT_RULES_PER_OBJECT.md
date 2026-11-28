# Several Export Synchronisation Rules per Connected System Object

- **Status:** Doing (layer 2 of 3)
- **Issue:** [#2826](https://github.com/TetronIO/JIM/issues/2826)
- **PRD:** [PRD_SEVERAL_EXPORT_RULES_PER_OBJECT](../../prd/doing/PRD_SEVERAL_EXPORT_RULES_PER_OBJECT.md)
- **Last Updated:** 2026-11-25

## Overview

The PRD decides what happens when several export Synchronisation Rules cover one account. This plan says how to build it: three pull requests, stacked, each landing a behaviour that is complete and tested on its own.

| Layer | Pull request | PRD sections |
|---|---|---|
| 1 (bottom) | The managing rule decides the account's existence | E |
| 2 | Several rules on one account: one Pending Export, clashes per attribute, full send on coverage start, Enforce State per rule | A, B, C, F, G (staging and previews) |
| 3 (top) | The export Out-of-Scope Action and Recall, with Recall/Keep on delete | D, G (setting on every surface) |

Layer 1 is the bottom because it is the smallest change to shipped behaviour, and because layer 2's grouping depends on knowing which rule manages an account. Layer 3 depends on layer 2's coverage record.

## Business Value

See the PRD's Problem Statement. In short: administrators can split one large export rule into a base rule plus small scoped add-on rules, without the account freezing, and without an add-on rule's settings deleting accounts or keeping leavers alive.

## Technical Architecture

### Current state (verified 2026-11-25)

- **One Pending Export per Connected System Object is enforced by the database:** `IX_PendingExports_ConnectedSystemObjectId_Unique`. Two in one page fail the whole page, which is why #2820 holds back every rule in an overlap (`SyncEngine.FindExportRuleOverlaps` / `StandaloneExportRules`, `SyncEngine.ExportStaging.cs:206-301`).
- **Rule B cannot see rule A's Pending Export within one evaluation.** `EvaluateExportRulesWithNoNetChangeDetectionAsync` (`ExportEvaluationServer.cs:539`) stages rule by rule. The worker passes `_pendingExportsToCreate` as `existingPendingExports` and appends the call's result only after it returns (`SyncTaskProcessorBase.cs:4066`). So a second rule creates a second Pending Export, as a second Create or as an Update after `UpdateExportedProvisioningCso`.
- **A rule that does not provision stages nothing while the account is absent or PendingProvisioning** (`DecideOutboundStaging`, `ProvisioningDeclined`, `SyncEngine.ExportStaging.cs:90`).
- **An Update sends only changed attributes for direct mappings** (`SelectWholeAttributeValue`, `SyncEngine.ExportStaging.cs:1014`). Nothing sends every attribute when a rule starts covering an account; today that relies on Drift Detection when Enforce State is on.
- **Drift Detection returns early on an overlap** (`DriftDetectionService.cs:140-152`) and builds one corrective Pending Export per rule (`CreateCorrectiveExports`, 1317-1390).
- **Hand-over and covering ignore provisioning.** `FindCoveringExportRule` (`SyncEngine.ExportStaging.cs:241`) accepts any in-scope rule on the same system and type. `FindExportRuleHandOver` (253) requires the join to be an export rule's, but not that the covering rule provisions. The preview adapters' sibling checks are the same (`SyncRuleScopingPreviewAdapter.cs:397-460`, `SyncRuleDestructiveTogglePreviewAdapter.cs:251-344`).
- **Several rules leaving at once:** each departing rule runs its own action. A Delete staged earlier in the call stands through `ExportEvaluationWorkingSet` (`ExportEvaluationServer.cs:799-802`, `1302-1303`).
- **Metaverse Object deletion** chooses Delete, then Disable, then Disconnect across every rule matching the account's system and type, with no regard to which rule manages it (`SyncEngine.DecideMvoDeletionExport`, `SyncEngine.ExportEvaluation.cs:44-60`).
- **Rule and mapping deletion recall (#1537) is import-only in practice.** It counts Metaverse values the rule contributed, which an export rule never has (`ConnectedSystemServer.cs:11158-11276`, `8737-8759`).
- **Deleting an export rule** nulls `JoinSyncRuleId` on the accounts it managed (SetNull) and deprovisions nothing.
- **The import Out-of-Scope Action** (`SyncRule.InboundOutOfScopeAction`, enum `InboundOutOfScopeAction` RemainJoined/Disconnect) is on the update DTO and `Set-JIMSyncRule`, but not on `CreateSyncRuleRequest` or `New-JIMSyncRule`.

### Proposed solution

**Terms.** The *managing rule* is the export rule named by `ConnectedSystemObject.JoinSyncRuleId` when the join method is Provisioning or ExportMatching (`SyncEngine.IsJoinMadeByExportRule`). Every other covering export rule is an *add-on* for that account. An account joined by an import rule has no managing rule.

**One staging unit per account.** Export evaluation groups the in-scope rules by (Connected System, Connected System Object Type), the same key that today defines an overlap, and stages each group into one Pending Export:

```
in-scope rules for the Metaverse Object
  -> group by (Connected System, Object Type)
  -> per group:
       order: the rule that provisions (or the managing rule) first, then the rest by id
       ownership: target attribute -> writing rules; any attribute with two writers is a clash
       stage the first rule as today (Create or Update), clashed attributes removed
       each later rule: compute its changes, clashed attributes removed, merge into the same Pending Export
  -> one Pending Export per account, every change stamped with its SyncRuleId
```

A pure engine function, `SyncEngine.PlanExportGroup(rules, existingCso, coverage)`, returns the ordering, the ownership map and the clash list, so the decision is unit-testable without I/O, as the other #288 extractions are.

**Coverage record.** A new table, `ExportRuleCoverages` (`ConnectedSystemObjectId`, `SyncRuleId`, composite primary key), lists the add-on rules covering each account. The managing rule is never stored there: `JoinSyncRuleId` already records it. For an account joined by an import rule, every covering export rule is recorded. It is live data, not history, so it takes real foreign keys that cascade from both the Connected System Object and the Synchronisation Rule.

- **Read:** preloaded per page in `RefreshExportEvaluationCacheAsync` (`ExportEvaluationServer.cs:154-215`) with one raw query (`= ANY(@csoIds)`), into `ExportEvaluationCache.CoverageByCsoId`, following the `GetMetaverseObjectConnectionsAsync` pattern.
- **Diff:** export evaluation compares the in-scope add-on rules with the record and returns `CoverageStarted` and `CoverageEnded` (pairs of account and rule) on `ExportEvaluationResult`.
- **Write:** in `FlushPendingExportOperationsAsync`, after provisioning CSOs are created: COPY for starts (`ExportRuleCoverageBulkColumns`), `DELETE ... = ANY` for ends. Nothing is written when nothing changed, so a steady-state page writes no rows.

**Full send on coverage start.** For an add-on rule in `CoverageStarted`, and for a covering rule that has just taken an account over, staging runs with `stageEveryMapping: true`. This selects mappings as a Create would, but keeps the Update change type and no-net-change detection. So only values the account does not already hold are sent. `ComputeAttributeValueChanges` gains the flag; the `SelectWholeAttributeValue` and multi-valued paths treat it as `isCreateOperation` for selection only.

**Lifecycle (layer 1).** `FindCoveringExportRule` and `FindExportRuleHandOver` accept only a rule with `ProvisionToConnectedSystem == true`. For an account with a managing rule, out-of-scope evaluation applies only the managing rule's Deprovisioning Action, and add-on rules leaving scope stage no deprovisioning. Metaverse Object deletion does the same: with a managing rule, that rule's action decides. Accounts with no managing rule keep today's strongest-action-wins.

**Recall (layer 3).** A new `SyncRule.OutboundOutOfScopeAction` (`RecallValues` = 0, the default, `LeaveValues` = 1). On `CoverageEnded` for a rule set to Recall, while the account stays, staging adds a clearing change for each attribute the rule wrote. It skips attributes another covering rule writes, naming and anchor attributes, `WritableOnCreate` attributes and Initial Export Only mappings, and merges into the account's one Pending Export. The managing rule's own departure on a hand-over is a coverage end too: its join record is the coverage.

## Implementation Phases

Every phase is red-first: the failing test lands with the code that makes it pass. Each layer ends with `dotnet build JIM.sln`, `dotnet test JIM.sln` (zero warnings), the `RequiresPostgres` suite against a test database, runtime verification on the sandbox stack, documentation, changelog, and the integration scenarios named.

### Layer 1: the managing rule decides the account's existence

**Phase 1.1: covering requires provisioning**
- `FindCoveringExportRule` and `FindExportRuleHandOver` (`SyncEngine.ExportStaging.cs`) require `ProvisionToConnectedSystem == true` on the covering rule.
- Preview adapters: `SyncRuleScopingPreviewAdapter.EvaluateOutboundAsync` (sibling filter ~397) and `SyncRuleDestructiveTogglePreviewAdapter.ClassifyOutboundBatchAsync` (~251, ~341) count only provisioning siblings as keeping the account.
- Tests: new engine tests for both helpers (provisioning covering rule takes over; non-provisioning does not). `StalePendingDeleteCancellationTests.FullSync_SamePageScopeOutWithSecondInScopeRuleOnSameSystem_HandsTheAccountOverAndStagesNoDeleteAsync` (line 79) uses a non-provisioning covering rule and asserts the old behaviour: change it to a provisioning rule, and add its counterpart asserting the account is deprovisioned when the covering rule does not provision. Same for the two adapter tests whose sibling leaves `ProvisionToConnectedSystem` unset (`SyncRuleScopingPreviewAdapterTests.cs:512`, `SyncRuleDestructiveTogglePreviewAdapterTests.cs:584`). `ExportScopeHandOverWorkflowTests` (both rules provision) must pass unchanged.

**Phase 1.2: only the managing rule's action, when several rules leave**
- In both `EvaluateOutOfScopeExportsAsync` overloads (`ExportEvaluationServer.cs:387`, `691`): when the account has a managing rule, an out-of-scope add-on rule does nothing (no Disconnect, no Delete), and the managing rule, if out of scope with no provisioning rule covering, deprovisions by its own action. Accounts without a managing rule keep the current per-rule loop and working-set behaviour.
- The preview's out-of-scope entries (`EvaluateOutboundPreviewForMaterialisedMvosAsync`, ~3763-3801) follow the same rule, which also removes today's two-entries-for-one-account preview.
- Tests: the missing case the exploration found (two rules leaving at once with different actions), for a managed account and an import-joined account.

**Phase 1.3: Metaverse Object deletion**
- `SyncEngine.DecideMvoDeletionExport` takes the account's managing rule: when it is among the matching rules, its action decides; otherwise today's Delete, then Disable, then Disconnect. Callers pass the CSO's `JoinSyncRuleId` and join method (they already hold the CSO).
- Tests: `SyncEngineExportEvaluationTests` gains managed-account cases beside `DecideMvoDeletionExport_RulesConflict_DeleteWinsAndTheConflictIsVisible` (which stays, for an import-joined account). Synchronised deprovisioning preview (`ConnectedSystemServer.SynchronisedDeprovisioningPreview.cs`) follows automatically and gets one test.

**Layer 1 deliverables**
- Docs: `docs/configuration/deprovisioning.md` (managing rule, add-on rules, which action applies, on scope exit and on deletion); `docs/configuration/scoping-criteria.md` (hand-over needs a provisioning rule); `docs/configuration/activity-execution-items.md` (Handed To Another Export Rule).
- Changelog: one 🔄 entry for the narrowing (#2826).
- Integration: no scenario exercises hand-over or two export rules on one account (verified), so none needs updating. Scenario 004 (deletion rules) and Scenario 008 run as regression.

### Layer 2: several rules on one account

**Phase 2.1: the group plan (pure engine)**
- `SyncEngine.PlanExportGroup`: ordering (the managing rule or a provisioning rule first, then by id), ownership (target attribute id from `ResolveTargetConnectedSystemAttributeId()` over enabled mappings), clashes (any attribute with more than one writer, multi-valued included; class-membership adds from #492 excluded, since they are not a mapping).
- `ExportRuleOverlap` gains `AttributeNames`; `DescribeForAdministrator` names the attributes. The error type and its ordinal (41) are unchanged.
- Tests: ordering, ownership, clashes on single-valued, multi-valued and expression mappings, a disabled mapping not counting, two rules with disjoint attributes.

**Phase 2.2: staging one Pending Export per account**
- `EvaluateExportRulesWithNoNetChangeDetectionAsync` replaces the standalone/overlap split with the group plan. The first rule stages through `CreateOrUpdatePendingExportWithNoNetChangeAsync` as today. Each later rule's changes are computed and merged into the Pending Export the group has produced (the in-memory merge, `MergeAttributeChangesIntoPendingExport`, extended to a Create in the same call), with the group's clashed attributes filtered out of every rule.
- `DecideOutboundStaging` gains one input: a group partner is provisioning in this evaluation, or a never-sent Create is queued. An add-on rule then contributes to the Create instead of `ProvisioningDeclined`. A sent but unconfirmed Create keeps the existing append-onto-the-same-row branch (`ExportEvaluationServer.cs:3071-3133`).
- `AddClassMembershipChanges` runs once per Pending Export over the union of the group's attributes, not per rule.
- `ProvisioningSyncRulesByCsoId` and the join record name the provisioning rule.
- `InScopeJoinedCsoIds`, hand-over adoption and Generated Value resolution keep working off the group's single result.
- The two cache-less overloads (`EvaluateExportRulesAsync`, lines 331 and 475) are used only by tests: move them onto the group plan as well, so tests and production share one path. Add no new production callers.
- Recall executors (`SyncRuleDeletionRecall.cs:303`, `SynchronisedDeprovisioning.cs:476`) and reference recall's fallback use the same method, so they inherit the grouping. The reference recall fast path already keys by (system, target attribute) and needs only a test.
- Tests (unit and workflow): Scenarios 1, 2, 3 and 5 of the PRD as `Workflows/SeveralExportRulesPerObjectWorkflowTests.cs`; the Create join; the sent-unconfirmed Create; the cross-page reference pass (`CrossPageReferenceProvisioningTests` gains a two-rule case). `OverlappingExportRulesWorkflowTests` is rewritten: different attributes now succeed, and the same attribute is a clash.

**Phase 2.3: coverage record and full send on start**
- Model `ExportRuleCoverage` (JIM.Models/Staging); `JimDbContext` configuration with cascades from `ConnectedSystemObjects` and `SyncRules`; migration; `ExportRuleCoverageBulkColumns` with `BulkInsertColumnCompletenessTests`; repository methods on `ISyncRepository` (PostgreSQL, in-memory, and `ReadOnlySyncRepositoryGuard`: reads pass through, writes throw).
- Guards to extend: `AuditReferenceCatalogueTests.LiveDataForeignKeys` (the new foreign key into `ConnectedSystemObjects`, with its reason); `DeletePathForeignKeyCoverageTests` (cascade from Synchronisation Rule and Connected System deletion); `DeletionTrackerForeignKeyCoverageTests` picks it up automatically; a `RequiresPostgres` round-trip test.
- Cache preload, the result diff, and the flush write as described above. The cross-page pass writes through the same flush.
- `ComputeAttributeValueChanges(..., stageEveryMapping)`, set for coverage starts and for hand-overs.
- **Upgrade (see Open Decisions):** the record starts empty, so every add-on coverage looks like a start on its first evaluation after the upgrade.
- Tests: promotion sends every add-on attribute (PRD Scenario 1); no-net-change drops values already held; a steady-state page writes no coverage rows; hand-over sends the covering rule's attributes; an import-joined account with one export rule now gets a full send when the rule starts covering it (PRD requirement 16).

**Phase 2.4: Drift Detection and Enforce State per writing rule**
- `DriftDetectionService.EvaluateDrift`: replace the overlap early return with the group plan; check each attribute only against its owning rule, and only if that rule has Enforce State on; skip clashed attributes.
- `CreateCorrectiveExports`: one Pending Export per Connected System Object, merging the rules' corrective changes, not one per rule.
- `PlanHeldReferenceQuestions` and `SynchronisationValueLoadPlanner.cs:82` get the same scope and ownership filter, so they never load for an attribute no rule will check.
- Worker `EvaluateDriftAndEnforceState` (`SyncTaskProcessorBase.cs:8931-9084`) and preview `ProposeDriftCorrectionsAsync` (`SyncPreviewServer.cs:1466`): report clashes through `_exportRuleOverlapsReported`, as now, naming the attributes.
- Tests: PRD Scenario 9; a clashed attribute is never corrected; two enforcing rules produce one corrective Pending Export.

**Phase 2.5: previews and save warnings**
- `SyncPreviewServer.ComposeOutbound` (~2902) builds one proposed Pending Export per account from the group's entries; `BuildOutboundOutcomeNodes` is unchanged apart from attributing each change to its rule. `AddExportRuleOverlapError` names the attributes.
- `StampRuleSaveWarningsAsync` (`ConnectedSystemServer.cs:10604`): warn only for shared target attributes, using `GetDataFlowHeadersAsync` filtered to export, this system and Object Type. Extend its projection with the mapping's `Enabled` flag, so a disabled mapping does not warn. The single-mapping overload follows the `StampConnectorDefinedAttributeOverlapWarningsAsync` pattern, so adding a mapping in the editor warns at once.
- Resource text: `SaveWarningExportRulesOverlap` rewritten to name the attributes ("... also writes {4} on ..."), in en-GB, en-US, French and German, through `Import-PortalText.ps1`; `DisplayText.ErrorHeadingExportRulesOverlap` and the `EnumText` entry reworded the same way.
- Tests: `OverlappingExportRulesWorkflowTests` save-warning cases (lines 228-374) rewritten; preview cases (164-214) now expect a merged proposal; localisation tests pass.

**Layer 2 deliverables**
- Docs: `scoping-criteria.md` "Several rules for the same objects" (the base-plus-add-on pattern replaces the one-export-rule guidance); `synchronisation-rules.md` (save warnings); `troubleshooting-synchronisation.md` (`ExportRuleOverlap` now means a shared attribute); `docs/powershell/activities.md`, `execution-items.md`, `pending-exports.md` (error type description); `concepts/attribute-priority.md` (pointer to #2827); `engineering/DEVELOPER_GUIDE.md` (staging groups, coverage record); `engineering/CAUSALITY_REFERENCE.md` regenerated.
- Changelog: ✨ several export rules per account; 🐛 a rule starting to cover an existing account now sends every attribute.
- Integration: a new scenario (the next free number) with a base rule and an add-on rule against the OpenLDAP directory, covering PRD Scenarios 1 to 5 and 9. Scenarios 008 (Medium) and 032 (OpenLDAP, Medium), as `src/JIM.Application/CLAUDE.md` requires for staging changes.

### Layer 3: the export Out-of-Scope Action and Recall

**Phase 3.1: the setting on every surface**
- Model: `SyncRule.OutboundOutOfScopeAction` and enum `OutboundOutOfScopeAction` (`RecallValues` = 0, `LeaveValues` = 1) in `CoreEnums.cs`; `SyncRuleHeader` and its projection; migration with column default 0, so every existing rule is Recall (PRD decision).
- REST: `CreateSyncRuleRequest` and `UpdateSyncRuleRequest`; validation refuses it on an import rule, as the API should for any direction-specific field.
- PowerShell: `New-JIMSyncRule` and `Set-JIMSyncRule` `-OutboundOutOfScopeAction` (`ValidateSet('RecallValues','LeaveValues')`), with Pester tests; reference regenerated with `scripts/Update-PowerShellReference.ps1`.
- Portal: `SyncRuleScopeTab.razor` shows the setting for an export rule, beside where the import setting sits for import rules, with the same select-plus-explanation pattern; strings in `SyncRuleText` in four languages. On `SyncRuleDetailsTab.razor`, a rule that does not provision shows that its Deprovisioning Action is not used for accounts another rule manages. A mock-up artefact goes to the product owner before the Razor is written.
- Change history and preview: `ConfigurationSnapshotService`, `ConfigurationChangeClassifier` (class A, as the inbound one), `ConfigurationChangeConsequences`, `PreviewPropertyText` in four languages.
- Tests: API create/update tests, model header tests, change capture tests.

**Phase 3.2: Recall on coverage end**
- In export evaluation, for each `CoverageEnded` pair (and the managing rule's departure on a hand-over) whose rule is set to `RecallValues` and whose account stays: stage clearing changes per the solution above, merged into the account's Pending Export, attributed to the departing rule.
- Save warning when a rule set to Recall maps an attribute Recall will never clear (naming, anchor, create-only, Initial Export Only), naming it; four languages.
- An execution item records the recall on the object: reuse the scope-exit outcome family, so the causality views show "left scope, values recalled", naming the rule.
- Previews: `SyncRuleScopingPreviewAdapter` and the synchronisation preview show the clearing changes a scope change would stage.
- Tests: PRD Scenario 4 (Recall and Leave), Bob never touched (no record), a non-clearable attribute skipped, an attribute another rule still writes kept, hand-over recall (Scenario 8), disabling recalls nothing.

**Phase 3.3: Recall/Keep on deletion**
- Rule deletion: `DeleteSyncRuleInternalAsync` counts, for an export rule, the accounts in its coverage record that another rule still covers (the import path's count is unchanged). With accounts affected and Recall chosen, it disables the rule and queues the existing `DeleteSyncRuleWorkerTask`, whose executor gains an export branch: stage the clearing changes for each recorded account, then delete the rule.
- Mapping removal: `DeleteSyncRuleMappingCoreAsync` and the staged `SyncRuleMappingRemovalChoice` path accept export mappings. Recall clears that attribute on the recorded accounts at their next synchronisation, as the inbound mapping recall does.
- Surfaces: the REST delete endpoints' remarks drop "export mappings contribute nothing"; `Remove-JIMSyncRule` and `Remove-JIMSyncRuleMapping` impact text covers accounts; the portal's `ContributedValuesChoiceDialog` is offered for export rules and mappings (`SyncRuleDetail.razor:996-1059`, `SyncRuleAttributeFlowTab.razor:1980-2045`); four languages.
- Tests: PRD Scenario 10; Keep leaves values; a recall Activity with per-object outcomes; Pester for both cmdlets.

**Layer 3 deliverables**
- Docs: `synchronisation-rules.md` (the setting; deleting an export rule or mapping); `scoping-criteria.md` (the setting beside the import one); `deprovisioning.md` (Recall versus deprovisioning); `docs/powershell/synchronisation-rules.md` (regenerated); `docs/reference/glossary.md` if the setting name is new vocabulary (plus `docs/.conformance/vocabulary.psd1`).
- Changelog: ✨ the export Out-of-Scope Action and Recall/Keep for export rules; 🔄 Recall on a hand-over between two provisioning rules.
- Integration: the new scenario gains PRD Scenarios 4, 6, 7, 8 and 10.

## Success Criteria

The PRD's acceptance criteria, plus:

- No page in Scenario 008 Medium or the new scenario writes coverage rows except where coverage changed.
- Export evaluation time per page at Scenario 008 Medium within 5% of `main`.
- No integration scenario needed an assertion loosened to pass.

## Benefits

- Configuration that matches how administrators think about accounts: one base rule, small scoped add-ons.
- An account no longer freezes because of one wrong mapping.
- Add-on attributes reach accounts the moment they apply, not when Drift Detection next runs.
- The import and export sides offer the same Out-of-Scope Action and the same Recall/Keep choice on delete.

## Dependencies

- None external, and no new packages.
- Two migrations (layer 2: coverage table; layer 3: the setting column). Each must be regenerated if `main` gains migrations first (`MigrationDesignerChainTests`).

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Merging a second rule into a Create or a sent-unconfirmed Create breaks Generated Value resolution or the Initial Password hold | Reuse the existing in-memory merge and append branches; workflow tests with a generated mapping and an Initial Password in each rule |
| Coverage writes add load to every page | Written only on change; preload is one indexed query per page; measured in Scenario 008 |
| A first evaluation after upgrade treats every add-on coverage as new | Settled by the upgrade decision below before Phase 2.3 starts |
| Ordering makes results depend on rule ids | The plan is deterministic, and clashes and merges are order-independent except for which rule's Create carries the join, which is always the provisioning or managing rule |
| Stack landed without the native link | Bottom-up landing with the rebase procedure in the root `CLAUDE.md`; layers branch as `claude/fervent-hawking-xly0y5` (layer 1) and `-stack-` names above it |
| Deleting a managing rule leaves its accounts unmanaged (pre-existing: `JoinSyncRuleId` is nulled and nothing deprovisions) | Unchanged here and stated in the docs; those accounts fall under "no managing rule" |

## Decisions (2026-11-26)

1. **The first evaluation after upgrade:** a rule that existed before the upgrade records the accounts it already covers silently, with no full send and no recall. Mechanism (Phase 2.3): a nullable `SyncRule.CoverageRecordedAt`, null for every rule in the migration and set at creation for new rules. While it is null, a coverage start is recorded without staging. A one-off baseline task, queued at the first Worker start after the upgrade, evaluates scope for every Metaverse Object of the rule's type, writes the record, and sets the column. From then on the rule behaves as a new one. **As built (layer 2):** not a queued Worker Task but an idle-time step, `Worker.PerformExportRuleCoverageBaselineAsync`, a page of 500 joined accounts of the rule's Object Type per idle tick (keyset over the Connected System Object id, cursor in memory; a restart repeats harmlessly). It runs only while no task runs, so no synchronisation overlaps a page, and it needed no new task type, Activity type or migration discriminator. It records nothing in the rule's change history.
2. **Metaverse Object deletion:** included in layer 1 (Phase 1.3); PRD requirement 28a.
3. **The import Out-of-Scope Action on create:** added to `CreateSyncRuleRequest` and `New-JIMSyncRule` in Phase 3.1.
