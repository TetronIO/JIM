# Initial Export Only Attribute Flows

- **Status:** Doing
- **Issue:** [#223](https://github.com/TetronIO/JIM/issues/223)

## Overview

Add an "Initial Export Only" option to individual Attribute Flow mappings on export Synchronisation Rules. When enabled, the attribute flows to the Connected System only during initial provisioning (the Create export). Once the object exists in the Connected System, JIM treats the attribute as unmanaged on that Connected System Object: future Metaverse Object changes are not exported for it, and Drift Correction does not re-assert it.

## Business Value

- **Temporary credentials or tokens**: set an initial password or API token once at provisioning, then let the target system own future changes
- **One-time setup attributes**: values that should only be configured when the object is created
- **External system ownership**: allow a Connected System to take ownership of specific attributes after initial provisioning, without Drift Correction reverting local changes

## Semantics

The flow is evaluated **only for Create (provisioning) exports**:

1. When JIM provisions a new Connected System Object, Initial Export Only mappings contribute to the Create Pending Export exactly like any other mapping.
2. If part of the Create export is not confirmed by the confirming import, the standard Create-to-Update Pending Export transition preserves the unconfirmed attribute changes; the initial value keeps retrying until confirmed. This is deliberate: "applied once, successfully" is the contract.
3. Once the Connected System Object is past provisioning (Status `Normal`), the attribute is unmanaged: Update export evaluation skips the mapping, and Drift Detection never stages corrective exports for it.
4. Connected System Objects that **join** to pre-existing objects in the target system never receive the value; the external system already owns it. (Provisioning-only semantics; agreed 2026-07-18.)
5. Import mappings are unaffected; the attribute can still be imported and contribute to the Metaverse.
6. The flag is honoured live: enabling it on an existing rule stops future exports of that attribute to already-provisioned objects; disabling it resumes normal management (the next sync and Drift Correction re-assert the Metaverse value).

### Why no per-Connected-System-Object state is persisted

"Unmanaged" is fully derivable: a mapping-level flag plus the Connected System Object's provisioning status. Persisting per-object/per-attribute markers would add a write on the import reconciliation hot path, a new table, and staleness risks (orphaned markers on rule changes, factory reset, Connected System deletion) while enabling no behaviour the derived model cannot express. If per-object re-management is ever needed, a marker table can be added then.

## Technical Architecture

### Current state

- `SyncRuleMapping` (`src/JIM.Models/Logic/SyncRuleMapping.cs`) carries per-mapping import-only tuning (`InboundValueProcessing`, `CaseNormalisation`, `Priority`, `NullIsValue`); there are no export-only mapping flags yet.
- All export attribute-flow computation funnels through `ExportEvaluationServer.CreateAttributeValueChanges` (single choke point; three callers, all passing `PendingExportChangeType`).
- `DriftDetectionService.EvaluateDrift` iterates `exportRule.AttributeFlowRules` to stage corrective exports for rules with `EnforceState` enabled, and already skips Connected System Objects in `PendingProvisioning` status.

### Proposed changes

1. **Model**: `SyncRuleMapping.InitialExportOnly` (bool, default `false`). Export-only; ignored for import mappings, mirroring how the import-only flags are ignored for export mappings. EF Core default value configured in `JimDbContext` alongside the existing mapping defaults; append-only migration.
2. **Export evaluation**: in `CreateAttributeValueChanges`, skip `InitialExportOnly` mappings when the change type is `Update`. This single gate covers full/delta sync evaluation, EnforceState re-evaluation, and reference recall.
3. **Drift Detection**: in `EvaluateDrift`, skip `InitialExportOnly` mappings entirely.
4. **API**: expose the flag on the Synchronisation Rule mapping DTOs (read and write) via `SynchronisationController`.
5. **UI**:
   - `SyncRuleAttributeFlowTab.razor`: an "Initial Export Only" checkbox in the Add/Edit Attribute Flow dialog, shown only for export Synchronisation Rules, with an explanatory hint. The flag is surfaced in the mappings table.
   - Connected System Object detail page: attributes targeted by an Initial Export Only mapping on an applicable export rule are indicated as unmanaged once the object is past provisioning.
6. **Docs**: public documentation for the option under `docs/`, plus changelog entry.

## Implementation Phases

### Phase 1: Model and migration

- Add `InitialExportOnly` to `SyncRuleMapping` with XML documentation
- `JimDbContext` fluent config default (`false`) and EF migration `AddInitialExportOnlyToSyncRuleMappings`

### Phase 2: Export evaluation gate

- Red-first tests in `test/JIM.Worker.Tests/OutboundSync/`: Create exports include the mapping; Update exports skip it (direct and expression sources; single and multi-valued); disabled flag behaves as today
- Gate in `CreateAttributeValueChanges`

### Phase 3: Drift Detection skip

- Red-first tests in `test/JIM.Worker.Tests/OutboundSync/DriftDetectionTests.cs`: drifted value on an Initial Export Only attribute stages no corrective export; other attributes on the same rule still corrected
- Skip in `DriftDetectionService.EvaluateDrift`

### Phase 4: API exposure

- Mapping DTOs and `SynchronisationController` create/update paths carry the flag; tests in `test/JIM.Web.Api.Tests/`

### Phase 5: UI

- Attribute Flow dialog checkbox (export rules only) and table indicator in `SyncRuleAttributeFlowTab.razor`
- Unmanaged indicator on the Connected System Object detail page (derived; no schema change)

### Phase 6: Documentation and changelog

- `docs/` Synchronisation Rule documentation update; `CHANGELOG.md` entry under `[Unreleased]`

## Success Criteria

- "Initial Export Only" is configurable per Attribute Flow mapping on export Synchronisation Rules (UI and API)
- The attribute flows on Create exports and is retried until confirmed, exactly as today
- No Update export or Drift Correction ever carries the attribute once the Connected System Object is past provisioning
- Joined Connected System Objects never receive the value
- Import mappings for the same attribute continue to work unchanged
- The Connected System Object detail page indicates unmanaged attributes
- Full solution build and test pass with zero warnings

## Risks and Mitigations

- **Admin expects the value to flow to already-provisioned objects after enabling the flag**: documentation states the provisioning-only semantics explicitly; the UI hint mirrors it.
- **A pending, unconfirmed initial value could retry indefinitely against a rejecting target**: unchanged from existing Pending Export retry behaviour; errors surface via the standard export error reporting.
- **Confusion with `EnforceState`**: `EnforceState` is rule-level drift remediation; `InitialExportOnly` is a per-mapping carve-out that wins over `EnforceState` for its attribute. Documented in both places.
