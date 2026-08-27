# Attribute Value Recall Choice - Implementation Plan

- **Status:** Doing
- **Issue:** [#1537](https://github.com/TetronIO/JIM/issues/1537)
- **PRD:** [`engineering/prd/PRD_ATTRIBUTE_VALUE_RECALL_CHOICE.md`](../../prd/doing/PRD_ATTRIBUTE_VALUE_RECALL_CHOICE.md)

## Overview

Deliver the deletion-time recall-or-keep choice for Synchronisation Rules and Attribute Flow mappings, with rule-delete recall running as a queued, Activity-tracked Worker task, plus the disable-copy changes and the create-disabled mapping parity fix. The WHAT and WHY live in the PRD; the UX is fixed by the "Recall Choice UX" artefact (2026-08-27). This document is the HOW.

## Technical Architecture

### Chosen mechanism: recall-then-delete inside the task

The naive shape (delete the rule, then recall) cannot work: `ContributedBySyncRuleId` is `ON DELETE SET NULL` (`JimDbContext.cs:513`), so deletion destroys the very provenance the recall selects on. Capturing affected value ids into the task before deleting was considered and rejected: on a large estate that is millions of ids serialised into a task row, and a stale snapshot by the time the task runs.

Instead the task owns the whole operation, following the `DeleteConnectedSystemWorkerTask` precedent (entity referenced by id, deletion happens in the task):

1. The delete request (any surface), when recall is chosen and contributed values exist:
   - disables the rule immediately with `DisabledReason` = "Deletion in progress: contributed attribute values are being recalled." (the rule stops being evaluated; #1538's dormant-contributor behaviour retains its values in the meantime),
   - queues a `DeleteSyncRuleWorkerTask { SyncRuleId, RecallContributedValues = true }`,
   - returns the queued Activity reference.
2. The Worker task, batched (500-object precedent, `ExecuteSchemaRefreshRemovalAsync`):
   - selects affected Metaverse Objects by intact provenance (`ContributedBySyncRuleId == taskRuleId`),
   - per object: stages `PendingAttributeValueRemovals`, re-elects surviving contributors (the `RemoveContributedAttributesOnObsoletion` recall algorithm, `SyncTaskProcessorBase.cs:954-975`), stages resulting exports, records an RPEI,
   - updates `Activity.ObjectsProcessed/ObjectsToProcess` as it goes,
   - deletes the rule via the existing delete path (configuration snapshot, priority reconciliation) and completes the Activity with summary statistics.
3. Failure mode: fast/hard. If the recall fails partway, the rule remains (disabled, reason intact), completed objects are consistent, and the Activity reports failure with per-object RPEIs; the deletion can be retried. No corrupt intermediate state, no silent partial recall.

**Keep, or no contributed values**: synchronous delete exactly as today (choice recorded on the deletion Activity when keep was offered). Keep needs no severing on rule deletes; the FK produces the same end state.

**Mapping deletes** (per PRD decisions): recall stays the shipped #1536 deferred mechanism (next Full Synchronisation of the contributing system), so nothing queues; **keep** severs provenance (`ContributedBySyncRuleId = null`, `ContributedBySystemId` retained) for values of `(rule id, target attribute id)` **before** the row delete, exempting them from the orphan recall permanently.

### API shape

Existing convention (Connected System deletion, Run Profile execution, Auxiliary Class Discovery): 202 Accepted + tracking DTO when work queues, 200/204 when immediate. So:

- `DELETE sync-rules/{id}?keepContributedValues=` → 202 + `SyncRuleDeletionResult` (activity id, affected counts) when a recall queues; 204 as today otherwise.
- `DELETE sync-rules/{id}/mappings/{mappingId}?keepContributedValues=` → 204 always (nothing queues).
- New `GET sync-rules/{id}/contributed-values-summary` (and `.../mappings/{mappingId}/contributed-values-summary`): attribute names + affected object/value counts, count-query only. Feeds the portal dialogs and lets scripts show impact before deleting.

## Implementation Phases

TDD throughout: every behaviour lands red-first. British English, Title Case domain nouns, changelog + docs in the same PR as the behaviour.

### Phase 1: Application core - impact summary and severing ✅

- `IConnectedSystemRepository` / `ConnectedSystemRepository` (+ in-memory twin): `GetContributedValuesSummaryAsync(syncRuleId, attributeId?)` returning per-attribute value/object counts without materialising rows; `SeverContributedValueProvenanceAsync(syncRuleId, attributeId)` as a set-based update.
- Tests in `JIM.Worker.Tests` (in-memory) + note the EF in-memory `.Include()` caveat; integration coverage via the existing repository test category.

### Phase 2: Rule deletion choice and the recall task ✅

- `DeleteSyncRuleWorkerTask` (SyncRuleId, RecallContributedValues, `[NotMapped]` ChangeReason copied to the Activity, `ForUser`/`ForApiKey` factories) + `JimDbContext` DbSet + migration.
- Wiring: `TaskingRepository` create/display-name/type switches; `TaskingServer.CreateWorkerTaskAsync` Activity branch; `Worker.cs` dispatch case.
- `ConnectedSystemServer.DeleteSyncRuleAsync` (both overloads): new recall/keep parameter; queue-or-delete decision per the architecture above; disable-with-reason on queue.
- Executor `ExecuteSyncRuleDeletionRecallAsync` in `ConnectedSystemServer` (pattern: `ExecuteSchemaRefreshRemovalAsync`). The re-election helper currently lives in `SyncTaskProcessorBase` (JIM.Worker); extract the reusable core into JIM.Application rather than duplicating it - this is the phase's main refactor and gets its own tests proving both call sites behave identically.
- Workflow tests: recall with surviving contributor (takeover), sole contributor (clear + export staging), failure partway (rule survives disabled, Activity failed), keep path (values remain, provenance nulled by FK, Activity records choice).

### Phase 3: Mapping deletion choice ✅

- `DeleteSyncRuleMappingAsync` (both overloads): keep parameter; severing before row delete; recall path unchanged (deferred to next Full Synchronisation; watermark stamp from #1536 already forces re-evaluation).
- Portal staged-removal path: the Attribute Flow editor removes mappings in memory and persists on rule save, so the save path must carry each staged removal's choice (extend the rule update flow; exact carrier decided at implementation, likely alongside `SyncRuleMappingSettingsUpdate`).
- Tests: keep exempts values from the #1536 orphan recall at the next Full Synchronisation (workflow test); recall path regression-pinned.

### Phase 4: REST API ✅

- The two DELETE endpoints gain `keepContributedValues`; rule delete returns 202 + `SyncRuleDeletionResult` when queued.
- New contributed-values-summary GET endpoints.
- `CreateSyncRuleMappingRequest.Enabled` (default true) mapped through creation (parity fix).
- Regenerate the OpenAPI document (`Generate-OpenApiDoc.ps1`); watch nullable entity navigations in new DTOs (`JsonIgnore` where API-reachable).
- Tests in `JIM.Web.Api.Tests` for DTO mapping, response codes and the summary shape.

### Phase 5: PowerShell ✅

- `Remove-JIMSyncRule` / `Remove-JIMSyncRuleMapping`: `-KeepContributedValues` switch; `ShouldProcess` message includes the summary counts; output carries the recall Activity id (documented output shape per `src/JIM.PowerShell/CLAUDE.md`).
- `New-JIMSyncRuleMapping`: `-Enabled` parameter (parity fix).
- Pester tests; cmdlet help includes the keep warning and a destructive example.

### Phase 6: Portal ✅

- Rule delete dialog and mapping removal dialog per the UX artefact (impact summary alert, radio-with-helper pattern from the deprovision choice, keep-selected warning reveal); shown only when the summary reports affected values, otherwise today's message boxes.
- Post-queue snackbar with "View Activity" (`/activity/{id}`); change reason flow unchanged.
- Copy changes: Danger Zone prose, rule Enabled switch helper, mapping Enabled checkbox helper (exact wording in the UX artefact).
- Staged mapping removal captures the choice at dialog time, applies at rule save (info alert states this).
- bUnit coverage where in `test/JIM.Web.Tests` scope; full-stack runtime verification in the devcontainer for the end-to-end flows (delete → task → Activity → values recalled).

### Phase 7: Documentation and changelog

- `docs/concepts/attribute-priority.md`: deletion-time choice and keep consequence.
- Synchronisation Rule how-to/reference pages: dialogs, Activity monitoring, REST/PowerShell parameters.
- Changelog: ✨ deletion-time recall choice with queued recall; 🔄 mapping deletion now offers keep; ✨ create-disabled mapping parity.

## Success Criteria

The PRD's acceptance criteria, all seven, verified at runtime in the devcontainer stack as well as by the suites (the queued task, Activity progress and re-election takeover are exactly the class of behaviour unit tests mock away).

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Re-election logic extraction from `SyncTaskProcessorBase` regresses obsoletion recall | Extract without behaviour change first (tests green before and after), then reuse; both call sites covered by workflow tests |
| Recall task racing a running synchronisation of the same system | Worker processes tasks serially per its existing model; the rule is disabled the moment the task queues, so sync runs no longer evaluate it |
| Export staging from outside a run-profile context misses conventions | Reuse the staging paths the obsoletion recall already exercises; workflow tests assert Pending Exports |
| Portal staged-removal choice adds rule-save complexity | Scope the carrier narrowly to removals; REST/PowerShell paths stay direct and simple |
| Large estates: dialog latency and task duration | Summary is count-only; task batches at 500 with Activity progress counters |

## Dependencies

- #1538 (disable retains contributed values): merged; the disable-with-reason queue step depends on its dormant-contributor behaviour.
- #1533 / PR #1536: shipped; mapping-delete recall reuses it unchanged.
- Downstream consumers: #809 (Connected System deletion synchronised deprovisioning, natively blocked by #1537) reuses the extracted re-election core and the recall-then-delete task pattern; #1549 (clear-stranded values) may reuse the by-provenance recall. Keep both in mind when shaping the Phase 2 extraction: the core should take its recall scope as an input rather than assuming a rule id.
