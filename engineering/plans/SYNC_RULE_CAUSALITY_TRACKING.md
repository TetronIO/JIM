# Synchronisation Rule Causality Tracking

- **Status:** Planned
- **Issue:** [#399](https://github.com/TetronIO/JIM/issues/399)

> Track and display which Synchronisation Rule caused each MVO projection, MVO attribute change, CSO provisioning, and Pending Export attribute change; surfaced in the UI as icon links on causality trees and attribute change tables.

## Overview

Currently, JIM records that a Synchronisation Rule was involved in a change at the `MetaverseObjectChange` level, but does not record causality at the per-attribute level, nor for Pending Export attribute changes. This makes it impossible for administrators to determine, when looking at an Attribute Flow table, which specific Synchronisation Rule drove each individual attribute value. This plan closes that gap across both inbound (import/projection) and outbound (export/provisioning) flows.

## Business Value

Administrators managing complex environments with multiple Synchronisation Rules flowing different attributes to the same object type have no visibility into which rule is responsible for each attribute value in the current UI. This hinders:

- Troubleshooting incorrect attribute values (which rule is setting the wrong value?)
- Auditing Attribute Flows for compliance purposes
- Understanding the impact of editing or disabling a specific Synchronisation Rule

## Current State

| Record | Synchronisation Rule Field | Populated? |
|--------|----------------|------------|
| `MetaverseObjectChange` | `SyncRuleId`, `SyncRuleName` (exist) | No; field exists from a prior migration but is never populated by the worker |
| `MetaverseObjectChangeAttribute` | None | N/A |
| `PendingExport` | None | N/A |
| `PendingExportAttributeValueChange` | None | N/A |

The Synchronisation Rule context is available in the worker and application code at the point each record is created; it is simply not being persisted.

### Already Delivered by Issue #1085 (Outcome-Level Attribution)

Issue [#1085](https://github.com/TetronIO/JIM/issues/1085) delivered outcome-level Synchronisation Rule attribution ahead of this plan: `ActivityRunProfileExecutionItemSyncOutcome` now carries nullable `SyncRuleId` and `SyncRuleName` (name snapshot) columns, populated by the worker for:

- `DisconnectedOutOfScope` outcomes: the scoping rule the Connected System Object fell out of scope of (the deterministic first import rule with scoping criteria; the same rule whose `InboundOutOfScopeAction` governs the disconnect).
- `Projected` outcomes: the projecting rule, threaded through `ProjectionDecision`.
- `Provisioned` outcomes: the export rule that caused the provisioning, threaded through `ExportEvaluationResult.ProvisioningSyncRulesByCsoId`.

Unlike the FK pattern above, the outcome columns are plain snapshot scalars (no FK), matching the table's existing `TargetEntityId`/`TargetEntityDescription` approach. This plan's remaining scope is the per-attribute attribution (`MetaverseObjectChangeAttribute`, `PendingExportAttributeValueChange`), the `MetaverseObjectChange`/`PendingExport` population, and the UI surfacing.

## Goals

1. Know which Synchronisation Rule caused an MVO to be projected (one rule is responsible).
2. Know which Synchronisation Rule caused each MVO attribute value to be created, updated, or removed.
3. Know which Synchronisation Rule caused a CSO to be provisioned (one rule is responsible).
4. Know which Synchronisation Rule caused each Pending Export attribute value change.
5. Display these causing Synchronisation Rules as icon links (with tooltip) on the causality tree and attribute change tables in the UI.

## Non-Goals

- Tracking Synchronisation Rule causality for non-sync-initiated changes (direct user edits, workflow-initiated changes).
- Retroactively populating causality data for historical records.

## Technical Architecture

### Pattern

All new Synchronisation Rule references follow the existing pattern established on `MetaverseObjectChange`:
- `SyncRuleId`: nullable FK to `SyncRule`, becomes null if the Synchronisation Rule is deleted
- `SyncRuleName`: snapshot string, preserved for audit trail even after Synchronisation Rule deletion

### Changes Required

#### Models (`JIM.Models`)

**`MetaverseObjectChangeAttribute`**: add:
```csharp
public int? SyncRuleId { get; set; }
public SyncRule? SyncRule { get; set; }
public string? SyncRuleName { get; set; }
```

**`PendingExport`**: add (represents the rule that triggered provisioning, for Create-type exports):
```csharp
public int? SyncRuleId { get; set; }
public SyncRule? SyncRule { get; set; }
public string? SyncRuleName { get; set; }
```

**`PendingExportAttributeValueChange`**: add:
```csharp
public int? SyncRuleId { get; set; }
public SyncRule? SyncRule { get; set; }
public string? SyncRuleName { get; set; }
```

#### Database (`JIM.PostgresData`)

One migration covering all three model changes above.

#### Worker (`JIM.Worker`)

**`SyncTaskProcessorBase`**:
- Populate `SyncRuleId` / `SyncRuleName` on `MetaverseObjectChange` when the change type is Projection, using the `projectionSyncRule` local variable already identified in `AttemptProjection()`.
- Populate `SyncRuleId` / `SyncRuleName` on each `MetaverseObjectChangeAttribute` as it is constructed during inbound Attribute Flow processing, recording which inbound Synchronisation Rule drove that specific attribute.

#### Application (`JIM.Application`)

**`ExportEvaluationServer`**:
- Populate `SyncRuleId` / `SyncRuleName` on `PendingExport` when it is created for a provisioning (Create) operation; the responsible `exportRule` is already a parameter at that point.
- Populate `SyncRuleId` / `SyncRuleName` on each `PendingExportAttributeValueChange` as it is constructed in `CreateAttributeValueChanges`: the `exportRule` parameter is already in scope.

#### UI (`JIM.Web`)

**`AttributeChangeTable.razor`**: add a rightmost icon-button column:
- When `SyncRuleId` is present: render a `MudIconButton` linking to `/sync-rules/{SyncRuleId}` with `SyncRuleName` as the tooltip.
- When `SyncRuleId` is null but `SyncRuleName` is present (rule deleted): render a disabled icon button with the rule name and "(deleted)" in the tooltip.
- When both are null: render nothing in that cell.

**`PendingExportDetail.razor`**: apply the same icon-button pattern to the attribute change rows in the Pending Export attribute table.

**Causality tree (projection/provisioning rows)**: for the top-level projection and provisioning rows (not just attribute rows), display the same icon button referencing the Synchronisation Rule on `MetaverseObjectChange` and `PendingExport` respectively.

## Implementation Phases

### Phase 1: Model and Migration

- Add `SyncRuleId`, `SyncRule`, `SyncRuleName` to `MetaverseObjectChangeAttribute`
- Add `SyncRuleId`, `SyncRule`, `SyncRuleName` to `PendingExport`
- Add `SyncRuleId`, `SyncRule`, `SyncRuleName` to `PendingExportAttributeValueChange`
- Create and review EF Core migration
- Write failing tests for the new fields (TDD)

### Phase 2: Worker; Inbound (Import/Projection)

- Populate Synchronisation Rule on `MetaverseObjectChange` for projection changes
- Populate Synchronisation Rule on `MetaverseObjectChangeAttribute` per-attribute during inbound Attribute Flow
- Tests must pass (red → green)

### Phase 3: Application; Outbound (Export/Provisioning)

- Populate Synchronisation Rule on `PendingExport` at provisioning creation time
- Populate Synchronisation Rule on `PendingExportAttributeValueChange` per-attribute in `CreateAttributeValueChanges`
- Tests must pass (red → green)

### Phase 4: UI

- Add Synchronisation Rule icon button column to `AttributeChangeTable.razor`
- Add Synchronisation Rule icon button to `PendingExportDetail.razor` attribute rows
- Add Synchronisation Rule icon button to causality tree projection/provisioning rows
- Verify with end-to-end smoke test against the Docker stack

## Success Criteria

- Every `MetaverseObjectChangeAttribute` record produced by a sync run has a non-null `SyncRuleId` (or at minimum `SyncRuleName` if the Synchronisation Rule was deleted between record creation and query time).
- Every `PendingExportAttributeValueChange` record produced by a sync run has a non-null `SyncRuleId`.
- Every `PendingExport` created by a provisioning Synchronisation Rule has a non-null `SyncRuleId`.
- The attribute change table shows a Synchronisation Rule icon button on each attribute row that navigates correctly to the Synchronisation Rule detail page.
- When a Synchronisation Rule has been deleted, the icon is disabled but the tooltip still shows the rule name.

## Risks and Mitigations

| Risk | Mitigation |
|------|-----------|
| Worker creates one `MetaverseObjectChange` per sync run rather than one per Synchronisation Rule, meaning multiple Synchronisation Rules' attribute changes are merged | Verify current worker behaviour before implementing Phase 2; if merging occurs, per-attribute tracking on `MetaverseObjectChangeAttribute` is the correct resolution |
| Migration on a live database with large `MetaverseObjectChangeAttribute` or `PendingExportAttributeValueChange` tables may be slow (nullable columns) | Nullable columns require no backfill; migration should be fast |
| Export evaluation creates Pending Exports across multiple code paths | Audit all `PendingExport` and `PendingExportAttributeValueChange` creation sites in `ExportEvaluationServer` before Phase 3 |
