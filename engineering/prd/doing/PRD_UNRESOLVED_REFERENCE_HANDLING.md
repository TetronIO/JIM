# Unresolved Reference Handling per Connected System

- **Status:** Doing
- **Created:** 2026-07-16
- **Author:** Claude (from issue #873, authored by Jay)
- **Issue:** [#873](https://github.com/TetronIO/JIM/issues/873)

## Problem Statement

When an import stages a reference attribute value (for example an LDAP group member DN) that cannot be resolved to a Connected System Object, JIM always marks the object's Run Profile Execution Item with `ErrorType = UnresolvedReference`. Because any errored item pushes the parent Activity to a warning (or failed) completion status, deployments where unresolved references are expected and benign, most commonly because the referenced objects sit outside the configured Container Scope, see persistent noise on every import: Activities that never complete cleanly, error counts that mask genuine problems, and administrators trained to ignore warnings.

The behaviour on unresolved references should be a per-Connected System choice: keep erroring (the safe default), downgrade to a warning, or ignore entirely; while keeping enough logging and reporting that genuine data-quality issues remain discoverable.

## Goals

- An administrator can configure, per Connected System, how import-time unresolved references are treated: Error (default), Warn, or Ignore.
- Existing deployments keep the current behaviour without any action (Error remains the default; no data backfill required).
- On Warn, affected imports complete with a warning status carrying a human-readable summary, without marking individual Run Profile Execution Items as errored.
- On Ignore, affected imports complete successfully, with unresolved references still visible in logs and on the affected Connected System Objects.
- The setting is configurable from the admin UI, the REST API, and PowerShell, and is captured in the configuration change history.

## Non-Goals

- No per-Run Profile or per-attribute granularity; the setting applies to the whole Connected System.
- No new warning severity tier on Run Profile Execution Items; warning reporting uses the existing Activity-level `WarningMessage` mechanism.
- No change to export-side reference handling (Pending Export unresolved reference semantics are unaffected).
- No retroactive reclassification of historical Activities or Run Profile Execution Items.
- No automatic detection of "expected" unresolved references (for example scope analysis); the administrator makes the call.

## User Stories

1. As an identity administrator whose Container Scope deliberately excludes some referenced objects (for example foreign-domain group members), I want imports to stop flagging those references as errors, so that Activity outcomes reflect genuine problems only.
2. As an identity administrator diagnosing data quality, I want unresolved references to remain discoverable in logs and on Connected System Objects even when downgraded, so that I can still investigate when something looks wrong.
3. As an auditor, I want changes to this setting captured in the configuration change history, so that a decision to suppress errors is attributable.

## Requirements

### Functional Requirements

1. A new per-Connected System setting, Unresolved Reference Handling, with exactly three values: Error, Warn, Ignore. Default: Error.
2. **Error** (current behaviour): each affected object's Run Profile Execution Item gets `ErrorType = UnresolvedReference` and the existing error message; Activity completion logic is unchanged.
3. **Warn**: Run Profile Execution Items are NOT marked as errored. Each unresolved reference is logged at Warning level. If any references were left unresolved, the Activity's `WarningMessage` is set (appended if a connector warning is already present) with a summary including the count, causing the Activity to complete with warning status.
4. **Ignore**: Run Profile Execution Items are NOT marked as errored and no Activity warning is set. Each unresolved reference is logged at Debug level, and a summary count is logged at Information level at the end of reference resolution (batch summary statistics are always emitted).
5. In all three modes, the unresolved reference string value remains stored on the Connected System Object (existing behaviour), so the existing unresolved reference count surfaces (CSO detail page, `Get-JIMConnectedSystemUnresolvedReferenceCount`) are unaffected.
6. The setting is editable on the Connected System settings page in the admin UI, exposed on the Connected System REST API (read and update), and settable via `Set-JIMConnectedSystem` in PowerShell.
7. Changes to the setting are recorded in the Connected System's configuration change history snapshot.

### Non-Functional Requirements

- No measurable impact on import hot-path performance; the setting is read once per run, not per object.
- The stored value must be schema-defaulted so existing rows adopt Error without a data migration.

## Examples and Scenarios

### Scenario 1: Default behaviour unchanged

**Given**: an existing Connected System created before this feature, with group members outside Container Scope
**When**: a full import runs and 40 member references cannot be resolved
**Then**: each affected object's Run Profile Execution Item is marked `UnresolvedReference` as today, and the Activity completes with warning status showing the errored items.

### Scenario 2: Warn

**Given**: the same Connected System with Unresolved Reference Handling set to Warn
**When**: a full import runs and 40 member references cannot be resolved
**Then**: no Run Profile Execution Items are marked as errored; the Activity completes with warning status and its warning message reads along the lines of "40 reference value(s) could not be resolved to Connected System Objects. The referenced objects may be outside the configured Container Scope."; each reference is logged at Warning level.

### Scenario 3: Ignore

**Given**: the same Connected System with Unresolved Reference Handling set to Ignore
**When**: a full import runs and 40 member references cannot be resolved
**Then**: no Run Profile Execution Items are marked as errored, no Activity warning is set, and the import completes successfully; a summary ("40 unresolved reference(s) ignored per Connected System setting") appears in the logs; the unresolved values remain visible on the affected Connected System Objects.

### Scenario 4: Audit trail

**Given**: an administrator changes the setting from Error to Ignore
**When**: the Connected System is saved
**Then**: the configuration change history records the change from "Error" to "Ignore", attributed to the administrator.

## Constraints

- Must not change the meaning or counting of existing `ErrorType` values (dashboards and stats depend on them).
- Must respect the Synchronisation Integrity rules: no silent failures; Warn/Ignore modes must still log every unresolved reference and emit summary statistics.
- British English throughout (UI labels, messages, docs).

## Affected Areas

| Area | Impact |
|------|--------|
| Database | New non-nullable enum column on `ConnectedSystems` with default 0 (Error); EF migration |
| Models | New `UnresolvedReferenceHandling` enum; new property on `ConnectedSystem` |
| Worker | `SyncImportTaskProcessor.ResolveReferencesAsync` branches on the setting (closes the `todo (#873)` at the Phase 3 error site) |
| Application | `ConfigurationSnapshotService.CreateSnapshot(ConnectedSystem)` records the new property |
| API | `ConnectedSystemDto` exposes it; `UpdateConnectedSystemRequest` accepts it |
| UI | New field on the Connected System settings tab (bespoke panel, alongside the Export Performance precedent) |
| PowerShell | `Set-JIMConnectedSystem -UnresolvedReferenceHandling`; `Get-JIMConnectedSystem` output includes it |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/configuration/connected-systems.md` | New "Unresolved reference handling" section describing the three modes and when to use each |
| `CHANGELOG.md` | ✨ entry under `[Unreleased]` |

## Open Questions

1. ~~Should this use the connector-declared settings mechanism (`ConnectorDefinitionSetting` / `ConnectedSystem.SettingValues`) suggested in the issue?~~ Resolved: no. Connector-declared settings are per-connector-definition; a cross-cutting behavioural toggle would have to be declared by every connector (current and future) and seeded per system. The established pattern for connector-independent, per-Connected System behaviour is a first-class model property (`ObjectMatchingRuleMode`, `MaxExportParallelism`), rendered in a bespoke panel on the settings tab. This PRD follows that pattern.
2. ~~Should Warn introduce a per-item warning severity on Run Profile Execution Items?~~ Resolved: no. There is no warning tier on Run Profile Execution Items today, and adding one is a much larger change touching error counting, stats, and UI. The Activity-level `WarningMessage` mechanism (used for the delta-import fallback warning) plus per-reference log entries meets the discoverability requirement.

## Acceptance Criteria

- [ ] Enum + `ConnectedSystem` property + EF migration exist; existing rows read as Error with no backfill script.
- [ ] Worker honours all three modes exactly as specified in Functional Requirements 2 to 4, covered by red-first unit tests in `JIM.Worker.Tests` (one per mode plus the default).
- [ ] Configuration change history snapshot includes the property (Scenario 4).
- [ ] REST API exposes and updates the property; PowerShell `Set-JIMConnectedSystem` supports it with a validated parameter and Pester coverage.
- [ ] Admin UI offers the three options with helper text on the Connected System settings tab.
- [ ] `docs/configuration/connected-systems.md` documents the modes; changelog entry added.
- [ ] `dotnet build JIM.sln` and `dotnet test JIM.sln` pass with zero errors and warnings.

## Additional Context

- Source TODO: `src/JIM.Worker/Processors/SyncImportTaskProcessor.cs` `ResolveReferencesAsync` Phase 3 (`todo (#873)`).
- Precedents: `ConnectedSystem.MaxExportParallelism` (first-class behavioural property with bespoke settings-tab panel, REST + PowerShell surface), `Activity.WarningMessage` (delta-import fallback warning), `ActivityStatus.CompleteWithWarning`.

---

# Implementation Plan

## Overview

Add an `UnresolvedReferenceHandling` enum property to `ConnectedSystem`, branch on it at the single site where `ErrorType = UnresolvedReference` is set, and surface the property through snapshot auditing, REST, PowerShell, and the admin UI. Follow the `MaxExportParallelism` precedent end to end.

## Phases

### Phase 1: Model, migration, snapshot (foundation)

- `src/JIM.Models/Staging/ConnectedSystemEnums.cs`: add `UnresolvedReferenceHandling` enum: `Error = 0`, `Warn = 1`, `Ignore = 2`, with XML docs.
- `src/JIM.Models/Staging/ConnectedSystem.cs`: add non-nullable property defaulting to `UnresolvedReferenceHandling.Error`.
- EF migration `AddUnresolvedReferenceHandling` (integer column, default 0).
- `src/JIM.Application/Services/ConfigurationSnapshotService.cs`: `AddEnum(children, "unresolvedReferenceHandling", ..., "Unresolved reference handling")` beside `objectMatchingRuleMode`.
- Tests: red-first snapshot coverage (existing configuration snapshot test patterns).

### Phase 2: Worker behaviour

- `src/JIM.Worker/Processors/SyncImportTaskProcessor.cs` `ResolveReferencesAsync` Phase 3: replace the unconditional error assignment with a switch on `_connectedSystem.UnresolvedReferenceHandling`; count unresolved references; on Warn set/append `_activity.WarningMessage` before the final `UpdateActivityAsync`; remove the `todo (#873)` comment.
- Summary statistics logged in all modes.
- Tests: red-first in `test/JIM.Worker.Tests` for all three modes plus default-is-Error.

### Phase 3: API + PowerShell

- `ConnectedSystemDto`, `UpdateConnectedSystemRequest` (+ controller mapping and validation) following `MaxExportParallelism`.
- `Set-JIMConnectedSystem`: `-UnresolvedReferenceHandling` with `ValidateSet('Error','Warn','Ignore')`; include in `Get-JIMConnectedSystem` output object if field mapping is explicit.
- Tests: Pester tests beside existing `ConnectedSystems.Tests.ps1` coverage; API mapping unit tests if a pattern exists.

### Phase 4: UI + docs + changelog

- `ConnectedSystemSettingsTab.razor`: new bespoke "Import Behaviour" expansion panel (visible when the connector supports import), `MudSelect` over the three values with helper text, saved by the existing Save Settings flow.
- `docs/configuration/connected-systems.md`: new section.
- `CHANGELOG.md`: ✨ entry under `[Unreleased]`.

## Risks & Mitigations

- **Warn colliding with a connector warning**: `WarningMessage` may already be set by the connector (delta fallback). Mitigation: append on a new line rather than overwrite.
- **In-memory EF provider masking migration issues**: unit tests will not exercise the column default. Mitigation: migration uses `defaultValue: 0` so existing rows materialise as Error; verified by migration review.
- **Behavioural regression in Error mode**: mitigated by a default-mode regression test asserting byte-for-byte the current error message and type.
