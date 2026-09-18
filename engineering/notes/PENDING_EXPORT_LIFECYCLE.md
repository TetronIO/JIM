# Pending Export Lifecycle

> How Pending Exports are created, merged, consumed, and traced back to Synchronisation Rules.

## Model Structure

**PendingExport**:
- `Id` (Guid), `ConnectedSystemId` (int), `ConnectedSystemObjectId` (Guid?; null for Create before CSO exists)
- `ChangeType`: Create, Update, or Delete
- `Status`: Pending, ExportNotConfirmed, Executing, Failed, or Exported
- `SourceMetaverseObjectId` (Guid?): the MVO that triggered the export
- `HasUnresolvedReferences` (bool): deferred export tracking for reference attributes
- `AttributeValueChanges` (list): attribute-level changes
- Retry fields: `ErrorCount`, `LastAttemptedAt`, `NextRetryAt`, `LastErrorMessage`, `LastErrorStackTrace`

**PendingExportAttributeValueChange**:
- `PendingExportId` (Guid?), `AttributeId` (int): target CS attribute
- Value columns: `StringValue`, `DateTimeValue`, `IntValue`, `LongValue`, `ByteValue`, `GuidValue`, `BoolValue`
- `UnresolvedReferenceValue` (string): for reference attributes awaiting resolution
- `ChangeType`: Add, Update, Remove, or RemoveAll
- Confirmation fields: `Status`, `ExportAttemptCount`, `LastExportedAt`, `LastImportedValue`

## Creation (Two Paths)

### Path 1: Export Evaluation (sync-rule-driven)

Location: `ExportEvaluationServer.CreateOrUpdatePendingExport*()`.

When an MVO changes and export rules target it:
1. Determine change type (Create/Update/Delete) based on CSO existence and PendingProvisioning status
2. Create PendingProvisioning CSO if needed (`ProvisionToConnectedSystem = true`)
3. Call `CreateAttributeValueChanges()` to build attribute-level changes
4. Persist PE to database (or stage in-memory for batch save)

### Path 2: Drift Detection

Location: `DriftDetectionService.EvaluateDrift()`.

After an import sync on a joined CSO with `EnforceState = true`:
1. Compare CSO actual attribute values vs expected values from export rule mappings
2. Stage corrective PEs in memory (not immediately persisted)
3. Merged with export evaluation PEs in `CreateOrUpdatePendingExportWithNoNetChangeAsync()`

## Attribute Change Creation (`CreateAttributeValueChanges`)

For each `AttributeFlowRule` in the export rule:

- **Expression mappings**: evaluate the expression against MVO attributes
- **Direct mappings**: use the source MVO attribute value
- **Create operations**: include ALL mapped MVO attribute values (not just changed ones)
- **Update operations**: only include the changed attribute values
- **No-net-change detection**: if cache is available, skip changes where the CSO already has the target value
- **Reference attributes**: store target MVO reference ID in `UnresolvedReferenceValue`; PE marked for deferred resolution
- **Removals**: multi-valued → `ChangeType = Remove`; single-valued → null-clearing change

## Merge Logic

### Merge Key Algorithm (`GetAttributeChangeMergeKey`)

- **Single-valued attributes**: key = `AttributeId` (newest value always replaces)
- **Multi-valued attributes**: key = `AttributeId:ValueIdentifier` (preserves distinct values)

Value identifier is resolved from: `UnresolvedReferenceValue ?? StringValue ?? GuidValue ?? IntValue ?? LongValue ?? DateTimeValue ?? BoolValue ?? ByteValue ?? ""`.

### Scenario A: In-Memory Merge (same batch)

When multiple syncs in the same batch affect the same CSO (`deferSave = true`):
- For each new attribute change, if the merge key matches an existing change:
  - Remove old change, add new (export eval wins on conflict)
- Otherwise add the new change

### Scenario B: Database Merge (previous PE exists)

When a drift-detection PE exists and a new export-eval PE is created for the same CSO:
1. Load existing PE from database
2. Build export-eval change keys
3. Clone drift-only changes not covered by export eval
4. Combine: new export eval changes + cloned drift-only changes
5. Delete old PE, create new merged PE

**Export eval always wins on conflict**: newer MVO state takes precedence over stale drift detection.

## Synchronisation Rule / RPEI Traceability

**PE attribute changes have no sync-rule provenance field.** There is no `SyncRuleId` or `RpeiId` on `PendingExportAttributeValueChange`. The PE itself only tracks:
- `SourceMetaverseObjectId`: the MVO that triggered the export
- `ConnectedSystemId`: the target system

The association to a Synchronisation Rule is **indirect**, via the causality tree:
- `ActivityRunProfileExecutionItemSyncOutcome` records a `PendingExportCreated` outcome as a child of the Synchronisation Rule evaluation that produced it
- A `ConnectedSystemObjectChange` snapshot (attached to the outcome) captures the attribute values at creation time
- But the PE attribute changes themselves are "rule-agnostic"; just the final computed values

**Implication**: when a second sync merges into an existing PE, the original Synchronisation Rule attribution is overwritten. The causality tree outcome is the only durable record of which Synchronisation Rule originally created or modified the PE. This is why the `HasRelevantChangedAttributes` guard (added March 2026) is important; without it, the causality tree incorrectly claims ownership of PEs it didn't cause.

## Consumption During Export

1. **Load**: PEs with status `Pending`/`ExportNotConfirmed` batch-loaded (`AsNoTracking`)
2. **Filter**: `IsReadyForExecution()` checks at least one pending or not-confirmed attribute change
3. **Execute**: connector's `ExportAsync()` called; PE status → `Executing`
4. **Capture**: `ProcessedExportItem` snapshots taken BEFORE deletion (enables RPEI creation after PE is gone)
5. **Success**: PE status → `Exported`; attribute changes → `ExportedPendingConfirmation`
6. **Create exports**: system-assigned external ID stored on CSO
7. **RPEI creation**: `SyncExportTaskProcessor.ProcessExportResultAsync()` creates `ActivityRunProfileExecutionItem` and `ConnectedSystemObjectChange` from the captured snapshot

## Confirmation and Retry

During confirming import:
- If CSO attribute values match expected → PE and attribute changes deleted
- If values don't match → attribute status → `ExportedNotConfirmed`, PE status → `Pending` for retry
- Retry uses exponential backoff (`NextRetryAt`), max retries tracked via `ErrorCount`
- After max retries → PE status → `Failed` (requires manual intervention)

### Attribute change against an unconfirmed-provisioning CSO

A CSO is created `PendingProvisioning` alongside a Create PE. If a further Metaverse Object attribute
change arrives while that Create is still unconfirmed (sent, or auto-confirmed away by a file-style
export, or attempted), staging (`SyncEngine.DecideOutboundStaging`) must never restage a second Create:
most Connectors reject a Create for an object they already hold. `SyncEngine.IsProvisioningNeverExported`
is the exact test - Status `Pending`, unattempted, zero errors - and only that case still absorbs the
change into the unsent Create in place. Everything else stages an `UpdateExportedProvisioningCso` verdict
(`ChangeType = Update`) instead.

Persistence follows the same rule, not "merge and replace": `ExportEvaluationServer` never deletes a
Create PE that has already been sent. Instead it appends the newly evaluated change onto the SAME row via
`ISyncRepository.AppendAttributeChangesToPendingExportAsync`, using the same merge-key semantics as
`MergeAttributeChangesIntoPendingExport` (a change for the same attribute supersedes the existing staged
one). The row's `Id`, `ChangeType` (still `Create`) and `Status` (still `Exported`/`ExportNotConfirmed`,
or `Pending` for a retrying attempt) are left untouched; the appended change is queued `Pending`.
`ExportExecutionServer`'s exportability check refuses to re-execute a Create with Status `Exported` (the
same guard that already refused a re-exported Delete), so the queued change is never sent while the
Create itself is still awaiting confirmation.

Once a confirming import confirms the Create's own exported changes, `SyncEngine.Reconciliation`'s
`TransitionCreateToUpdateIfSecondaryExternalIdConfirmed` flips the PE's `ChangeType` from `Create` to
`Update` (generalised beyond its original Secondary External ID trigger to also fire once every exported
change is confirmed and a queued `Pending` change remains), and the ordinary `UpdatePendingExportStatus`
sets `Status` back to `Pending`. The next export then sends exactly one Update carrying the queued
change - never a second Create, and never before the Create is confirmed.

`IsProvisioningNeverExported` still returns `false` for an exported Create carrying an appended `Pending`
change (its `Status` is `Exported`, not `Pending`), so a withdrawal in this window still stages a Delete
against the CSO, never a cancellation.

### Delete against an unconfirmed-provisioning CSO

A CSO is created `PendingProvisioning` alongside a Create PE. If the Create is exported but the MVO is
withdrawn before any confirming import, JIM stages a Delete PE for the still-`PendingProvisioning` CSO.
A successful export of that Delete does **not** follow the ordinary "PE status → Exported, CSO cleaned
up by the confirming import" path above: import deletion detection deliberately excludes
`PendingProvisioning` CSOs (`ConnectedSystemRepository.BuildDeletionDetectionQuery`), so no confirming
import would ever obsolete or delete it, and the CSO would otherwise be stranded forever with Status
`PendingProvisioning`, JoinType `NotJoined`, and no PE to retry. `ExportExecutionServer` treats a
successful Delete export of such a CSO as the only confirmation it will ever get: it removes the CSO and
its PE immediately, across all three success paths (batched calls, file-based, single-export), by id via
`SyncRepository.DeleteConnectedSystemObjectsByIdsAsync` rather than the tracked-graph delete. CSOs with
Status `Normal` are unaffected; a failed Delete export never reaches this rule.

## Reference Resolution

1. During PE creation, reference attributes store target MVO ID in `UnresolvedReferenceValue`
2. PE marked with `HasUnresolvedReferences = true`; skipped during initial export pass
3. After initial exports complete, `ExecuteDeferredReferencesAsync()` resolves MVO IDs → target CSO external IDs
4. Updates `UnresolvedReferenceValue` with resolved external ID
5. PE re-exported in second pass

## Status Transitions

```
PE Status:
  Pending --> Executing --> Exported --> [Deleted on confirm]
                                     --> ExportNotConfirmed --> Pending (retry)
                         --> Failed (max retries)

Attribute Change Status:
  Pending --> ExportedPendingConfirmation --> [Deleted on confirm]
                                          --> ExportedNotConfirmed --> Pending (retry)
          --> Failed (max retries)
```

## Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| Merge by key, not ID | Prevents duplicate single-valued attribute errors; preserves distinct multi-valued entries |
| Export eval wins on conflict | Newer MVO state takes precedence over stale drift detection |
| Clone drift-only changes | Drift PE deleted via cascade; prevents tracked EF instances from becoming unusable |
| Capture before deletion | `ProcessedExportItem` enables RPEI creation after PE deletion |
| PendingProvisioning CSO status | Allows confirming import to find CSO by secondary ID before system assigns primary ID |
| No Synchronisation Rule field on PE attribute changes | PEs are the final computed state; rule attribution lives in the causality tree |
| Batch saves with raw SQL | Avoids EF change tracker overhead and identity resolution conflicts at scale |
