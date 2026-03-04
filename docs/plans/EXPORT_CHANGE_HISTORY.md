# Export Change History & Pending Export Snapshot Persistence

**Issue:** #TBD
**Status:** In Progress
**Created:** 2026-03-04

## Problem Statement

JIM currently has a gap in its Activity history for export operations and pending export outcomes:

### Gap 1: Export RPEIs Have No Attribute Detail

When an export run profile executes, the RPEI records only `ObjectChangeType.Exported` or `ObjectChangeType.Deprovisioned` with a text summary in `DataSnapshot` (e.g., "Export: Update with 5 attribute change(s)"). The actual attribute names, values, and change types are lost once the `PendingExport` record is deleted post-export. The Causality Tree on the RPEI detail page therefore shows no expandable attribute detail for export outcomes.

By contrast, import and sync RPEIs have full attribute-level change history via `ConnectedSystemObjectChange` and `MetaverseObjectChange` navigation properties, which the Causality Tree renders as expandable `AttributeChangeTable` components.

### Gap 2: Sync RPEIs' PendingExportCreated Nodes Are Ephemeral

During a synchronisation, when outbound attribute flow creates a `PendingExport`, the Causality Tree records a `PendingExportCreated` outcome node with `TargetEntityId = pendingExport.Id`. When the user later expands this node, `LoadPendingExportAsync` fetches the pending export by ID. However, after the export run confirms and reconciles, the `PendingExport` is deleted. The user then sees "Pending export detail not available."

This means the Causality Tree's pending export expansion — which already has UI for rendering attribute names and change types — is only useful for a brief window between sync and export confirmation.

### Synergy

Both gaps share the same root cause: attribute-level change data for outbound operations is ephemeral. The solution should address both gaps with a single persistence mechanism.

## Analysis of Current Architecture

### How Import/Sync Change History Works

1. During import, `ConnectedSystemObjectChange` records are created with attribute-level detail (`ConnectedSystemObjectChangeAttribute` + `ConnectedSystemObjectChangeAttributeValue`).
2. During sync, `MetaverseObjectChange` records are created similarly.
3. Both are linked to the RPEI via `ActivityRunProfileExecutionItem.ConnectedSystemObjectChange` and `.MetaverseObjectChange` navigation properties.
4. The Causality Tree's `OutcomeTreeNode` passes these to `AttributeChangeTable` for expandable rendering.
5. Creation is governed by `ChangeTracking.CsoChanges.Enabled` and `ChangeTracking.MvoChanges.Enabled` service settings.
6. Retention is governed by `History.RetentionPeriod` (default 90 days).

### How Export Processing Works

1. `SyncExportTaskProcessor.ProcessExportResultAsync()` iterates `ExportExecutionResult.ProcessedExportItems`.
2. Each `ProcessedExportItem` has `ChangeType`, `AttributeChangeCount`, `ConnectedSystemObject`, and error info.
3. It does **not** carry the `PendingExportAttributeValueChange` list — those are on the `PendingExport` entity which is deleted after export.
4. The RPEI gets `DataSnapshot = "Export: Update with 5 attribute change(s)"` — text only.
5. A single `Exported` or `Deprovisioned` root outcome is added to the sync outcome graph.

### How PendingExportCreated Outcomes Work

1. During sync, `SyncTaskProcessorBase` creates `PendingExportCreated` outcome nodes with `targetEntityId: pendingExport.Id`.
2. The `OutcomeTreeNode` component recognises `PendingExportCreated` as expandable and lazy-loads the `PendingExport` by ID.
3. If the pending export still exists, it renders a table of attribute names and change types.
4. If deleted (post-export-confirmation), it shows "Pending export detail not available."

### What `DataSnapshot` Is Today

- `string?` field on `ActivityRunProfileExecutionItem`
- Used only for text summaries: "Export: Update with 5 attribute change(s)", "Failed attributes: ...", "Unconfirmed attributes: ..."
- Never used for JSON despite the XML doc comment suggesting it was planned
- Never rendered directly in the UI (error messages are shown instead)
- Effectively dead for its intended purpose

## Options Considered

### Option A: Create ConnectedSystemObjectChange Records for Exports (Recommended)

**Approach:** At export time, before the `PendingExport` is deleted, create a `ConnectedSystemObjectChange` record from the pending export's attribute changes and link it to the RPEI. For sync PendingExportCreated outcomes, snapshot the pending export's attribute changes into a `ConnectedSystemObjectChange` at sync time (when the data is guaranteed to exist).

**Detail:**

For **export RPEIs** (Gap 1):
- In `ProcessExportResultAsync`, carry the `PendingExportAttributeValueChange` data through to RPEI creation via `ProcessedExportItem` (currently only carries `AttributeChangeCount`)
- Create a `ConnectedSystemObjectChange` with `ChangeType = Exported` and populate its `ConnectedSystemObjectChangeAttribute` + `ConnectedSystemObjectChangeAttributeValue` children from the pending export data
- Link it to the RPEI via the existing `ConnectedSystemObjectChange` navigation property
- The Causality Tree's `OutcomeTreeNode` already renders `AttributeChanges` for nodes with changes — zero UI work needed for basic rendering

For **sync PendingExportCreated outcomes** (Gap 2):
- At sync time, when building the `PendingExportCreated` outcome node, also create a `ConnectedSystemObjectChange` record from the `PendingExport.AttributeValueChanges`
- Store a reference from the outcome node to this change record (via `DetailMessage` storing the change record ID, or via a new nullable FK on the outcome)
- The UI then renders from the persisted change record instead of lazy-loading the ephemeral `PendingExport`

**Pros:**
- Reuses the exact same data model (`ConnectedSystemObjectChange*`) and rendering components (`AttributeChangeTable`) already used for import change history
- Consistent mental model: an export/pending export is a change to a CSO — same as an import
- Governed by existing `ChangeTracking.CsoChanges.Enabled` service setting — no new setting
- Cleaned up by existing `History.RetentionPeriod` retention policy
- Queryable and searchable (normalised tables, not JSON blobs)
- Solves both gaps with one mechanism

**Cons:**
- Storage overhead — one row per attribute value change per export
- `ConnectedSystemObjectChange.ChangeType` enum needs extension (e.g., `Exported`, `PendingExport`)
- `ProcessedExportItem` needs to carry attribute change data (currently only carries count)

**DataSnapshot fate:** Remove the property and drop the column. It was never used for its intended purpose and this approach supersedes it entirely.

**Service Settings:** No new setting. Use existing `ChangeTracking.CsoChanges.Enabled`. When CSO change tracking is enabled, export and pending export changes are also recorded. This is intuitive — an export is a CSO change.

### Option B: Store Structured JSON in DataSnapshot

**Approach:** Serialise `PendingExportAttributeValueChange` data as JSON into `DataSnapshot` at export time. For sync PendingExportCreated outcomes, serialise the pending export data into a new field on the outcome node.

**Pros:**
- Minimal schema change — `DataSnapshot` already exists
- Lower per-record storage overhead (single column vs. normalised tables)
- Self-contained on the RPEI

**Cons:**
- Requires new UI rendering — cannot reuse `AttributeChangeTable` without deserialisation and mapping
- Not queryable — cannot search/filter export history by attribute name
- JSON blobs in a relational database — inconsistent with the rest of the architecture
- Schema evolution is harder (JSON format changes require migration logic)
- Not independently covered by retention cleanup (lives on the RPEI itself)
- Need a new Service Setting to control whether to populate it (orthogonal to CSO change tracking)
- Does not solve Gap 2 without additional work on the outcome model
- Two different rendering paths for essentially the same data (CSO changes for import, JSON for export)

### Option C: New Dedicated Export Change History Tables

**Approach:** Create new tables (`ExportChangeRecord`, `ExportChangeAttribute`, `ExportChangeAttributeValue`) specifically for export history.

**Pros:**
- Clean separation of import vs. export history
- Can be tuned independently

**Cons:**
- Significant duplication of the existing `ConnectedSystemObjectChange*` schema
- New repository methods, new API endpoints, new UI components
- New Service Setting needed
- More migration surface area
- Violates DRY — an export is conceptually a CSO change, just outbound
- Does not solve Gap 2 without further duplication for pending export snapshots

## Recommendation

**Option A** is the clear winner. It has the strongest architectural alignment (an export *is* a CSO change), maximum code and UI reuse, no new service settings, and solves both gaps with one mechanism.

## Implementation Plan

### Phase 1: Data Model Changes

1. **Extend `ObjectChangeType` enum** — add `Exported` and `PendingExport` values to distinguish outbound changes from inbound ones in the change history.

2. **Extend `ProcessedExportItem`** — add a `List<PendingExportAttributeValueChange> AttributeValueChanges` property so that the attribute data survives pending export deletion and is available during RPEI creation.

3. **Capture attribute data before deletion** — in the export execution flow (where `ProcessedExportItem` instances are built from `PendingExport` records), copy the `AttributeValueChanges` onto the `ProcessedExportItem` before the pending export is deleted.

### Phase 2: Export RPEI Change History (Gap 1)

4. **Create `ConnectedSystemObjectChange` during export** — in `SyncExportTaskProcessor.ProcessExportResultAsync()`:
   - Check `ChangeTracking.CsoChanges.Enabled` setting (load once at task start, alongside `_syncOutcomeTrackingLevel`)
   - If enabled, build a `ConnectedSystemObjectChange` from the `ProcessedExportItem.AttributeValueChanges`
   - Set `ChangeType = ObjectChangeType.Exported`
   - Populate `ConnectedSystemObjectChangeAttribute` + `ConnectedSystemObjectChangeAttributeValue` children by mapping from `PendingExportAttributeValueChange` (attribute, value columns, and change type)
   - Link to the RPEI via `executionItem.ConnectedSystemObjectChange = change`
   - Set audit fields (initiator info from `_activity`)

5. **Persist change records** — ensure the bulk insert path (`BulkInsertRpeisAsync`) handles the `ConnectedSystemObjectChange` navigation property, or persist change records separately before bulk inserting RPEIs.

6. **UI: Export RPEI Causality Tree** — the `OutcomeTreeNode` already renders `AttributeChanges` when the outcome type is `CsoAdded` or `CsoUpdated`. For `Exported` outcomes, add `Exported` to the `IsAttributeFlowWithChanges` condition so the expand button appears when change data is available. Alternatively, pass export attribute changes as a dedicated parameter if the existing `AttributeChanges` parameter conflicts.

### Phase 3: Sync PendingExportCreated Snapshot (Gap 2)

7. **Snapshot pending export changes at sync time** — in `SyncTaskProcessorBase`, when building `PendingExportCreated` outcome nodes:
   - Check `ChangeTracking.CsoChanges.Enabled` setting
   - If enabled, create a `ConnectedSystemObjectChange` with `ChangeType = ObjectChangeType.PendingExport` from the `PendingExport.AttributeValueChanges`
   - Link the change record to the outcome node. Options:
     - **(a) Use `DetailMessage`** to store the change record's GUID as a string (lightweight, no schema change, but somewhat hacky — `DetailMessage` already stores CS ID for `Provisioned` outcomes)
     - **(b) Add a nullable FK** `ConnectedSystemObjectChangeId` to `ActivityRunProfileExecutionItemSyncOutcome` (cleaner, but requires a migration)
   - Recommendation: **(b)** — a proper FK is more robust, self-documenting, and enables eager loading

8. **Add FK to outcome model** — add `Guid? ConnectedSystemObjectChangeId` and `ConnectedSystemObjectChange? ConnectedSystemObjectChange` to `ActivityRunProfileExecutionItemSyncOutcome`. Create EF Core migration.

9. **Update UI: Replace lazy PendingExport loading with persisted change data** — modify `OutcomeTreeNode`:
   - For `PendingExportCreated` nodes, render from the linked `ConnectedSystemObjectChange` (via the new FK) using `AttributeChangeTable` (same as CSO/MVO changes)
   - Remove the `OnLoadPendingExport` lazy-loading callback, `_pendingExport` field, and associated loading/rendering code — this is no longer needed
   - If tracking is disabled, the node simply has no expandable detail (no expand button shown)

### Phase 4: DataSnapshot Deprecation

10. **Remove `DataSnapshot`** — remove the `DataSnapshot` property from `ActivityRunProfileExecutionItem`, drop the column in the EF Core migration, and remove all code that populates it:
    - `SyncExportTaskProcessor.ProcessExportResultAsync()` — stop setting `DataSnapshot = description`
    - `SyncImportTaskProcessor` reconciliation error paths — move any useful info to `ErrorMessage` (which is already the user-facing field)
    - `BulkInsertRpeisAsync` raw SQL — remove `DataSnapshot` from the insert/update statements

### Phase 5: Tests

12. **Unit tests for export change history creation** — test that `ConnectedSystemObjectChange` records are correctly built from `PendingExportAttributeValueChange` data during export processing, covering:
    - Create export (new CSO) — all attributes are "Add"
    - Update export — mix of Add/Update/Remove changes
    - Delete export (deprovisioned) — no attribute changes expected
    - Setting disabled — no change records created
    - Error export — change record still created (the export was attempted)

13. **Unit tests for sync pending export snapshot** — test that `PendingExportCreated` outcome nodes get a linked `ConnectedSystemObjectChange` when tracking is enabled.

14. **Mapping tests** — test the mapping from `PendingExportAttributeValueChange` to `ConnectedSystemObjectChangeAttributeValue` for all value types (string, int, long, DateTime, Guid, bool, byte[]).

15. **Integration tests (Scenario 1)** — extend the existing Scenario 1 integration test (`Invoke-Scenario1-HRToIdentityDirectory.ps1`) to validate export change history:
    - After the LDAP Export (Joiner) step, fetch RPEI detail via the API and assert that `ConnectedSystemObjectChange` is populated with attribute-level changes
    - Validate that the change record has `ChangeType = Exported` and contains expected attributes (e.g., the attributes provisioned to AD)
    - After the Full Sync (Joiner) step, fetch an RPEI and validate that `PendingExportCreated` outcome nodes have a linked `ConnectedSystemObjectChange` with the pending export attribute snapshot
    - Add a new assertion helper (e.g., `Assert-RpeiHasExportChangeHistory`) to `Test-Helpers.ps1` for reuse

### Phase 6: Migration & Cleanup

16. **EF Core migration** — single migration covering:
    - New `ObjectChangeType` enum values
    - New FK on `ActivityRunProfileExecutionItemSyncOutcome`
    - Drop `DataSnapshot` column from `ActivityRunProfileExecutionItems`

## Service Settings Summary

| Setting | Role in This Feature |
|---------|---------------------|
| `ChangeTracking.CsoChanges.Enabled` | Controls whether export and pending export change records are created. When disabled, no attribute detail is persisted (same as import behaviour). |
| `ChangeTracking.SyncOutcomes.Level` | Controls the outcome tree structure (None/Standard/Detailed). Orthogonal to this feature — the outcome tree records *what happened*, the change records store *what changed*. |
| `History.RetentionPeriod` | Governs cleanup of `ConnectedSystemObjectChange` records, including the new export/PE ones. No change needed. |

**No new service setting is required.** The existing `ChangeTracking.CsoChanges.Enabled` is the natural control. An export is a CSO change. An administrator who enables CSO change tracking expects to see all CSO changes — import and export alike.

## Storage Impact

Each export or pending export operation with N attribute changes creates:
- 1 `ConnectedSystemObjectChange` row
- N `ConnectedSystemObjectChangeAttribute` rows
- N `ConnectedSystemObjectChangeAttributeValue` rows (typically 1:1 with attributes for exports)

This is identical to import change tracking. For environments where storage is a concern, `ChangeTracking.CsoChanges.Enabled = false` disables all of it.

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Increased storage for high-volume export environments | Governed by existing `ChangeTracking.CsoChanges.Enabled` toggle and `History.RetentionPeriod` cleanup |
| Bulk insert performance for export change records | Follow existing pattern — raw SQL bulk insert, same as RPEI bulk insert |
| `ProcessedExportItem` memory increase from carrying attribute data | Attribute data is already in memory on the `PendingExport`; we're just copying references before deletion |
