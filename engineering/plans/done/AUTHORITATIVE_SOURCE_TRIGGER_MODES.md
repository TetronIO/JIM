# Authoritative Source Trigger Modes

- **Status:** Done
- **Issue:** [#119](https://github.com/TetronIO/JIM/issues/119)
- **Related design doc:** [`DELETION_RULES_DESIGN.md`](../doing/DELETION_RULES_DESIGN.md)

## Overview

Extend the `WhenAuthoritativeSourceDisconnected` deletion rule with a configurable trigger mode so Metaverse Object deletion can require **all** selected authoritative sources to disconnect rather than **any**. Also make grace period cancellation mode-aware, so a rejoin only cancels a scheduled deletion when the trigger condition no longer holds.

The design was settled on 2026-08-01 (UI mockup agreed): the original priority ordering / hierarchy concept from issue #119 was dropped, and a proposed three-mode variant was collapsed to two modes over one selection list. All sources disconnect is the default; Specific source(s) disconnect carries the pre-existing behaviour (any selected source disconnecting triggers deletion).

## Business Value

- Prevents deletion when only one of multiple redundant sources has an issue (for example an HR system rebuild), the single biggest deletion-safety gap in the current any-source-triggers behaviour.
- Supports enterprises with multiple identity sources (global plus regional HR) without forcing a single system to be the sole lifecycle authority.
- Fixes an existing over-broad behaviour: today any system rejoining during a grace period cancels the scheduled deletion, even a system that had nothing to do with triggering it.

## Trigger Modes

| Mode | Behaviour |
|------|-----------|
| `AllSourcesDisconnect` (default for new configurations) | Delete only once no selected source retains a joined Connected System Object. Non-source connectors (targets) do not block deletion. |
| `SpecificSourcesDisconnect` | Delete when any one of the selected sources disconnects, even if others remain connected. Current behaviour; existing configurations keep this mode. |

UI labels: "All sources disconnect" and "Specific source(s) disconnect". Because the Specific label alone does not state the OR semantics, its helper text always reads "delete when any one of the selected sources disconnects".

**Defaults:** the safe mode (All) is the default for newly configured object types. Existing configurations created under #115 must keep Specific semantics, so the migration is behaviour-preserving (see Migration below). Source selection remains opt-in: the selection list starts empty for a newly configured rule and the existing "at least one source required" validation stands; a contributing system is never a deletion trigger unless an administrator selects it.

## Technical Architecture

### Current state

- `MetaverseObjectType.DeletionTriggerConnectedSystemIds` (`List<int>`) with fixed any-source semantics evaluated in `SyncEngine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId, remainingCsoCount)`.
- Two worker call sites (`SyncTaskProcessorBase.ProcessObsoleteConnectedSystemObjectAsync` and `HandleCsoOutOfScopeAsync`) query the remaining CSO count via `GetConnectedSystemObjectCountByMetaverseObjectIdAsync` and subtract one for the disconnecting CSO.
- Grace period cancellation: `EstablishJoinAsync` clears `LastConnectorDisconnectedDate` when **any** system rejoins; `FlushPendingMvoDeletionsAsync` skips deletion when the same system reconnects within the page.
- Connected System deletion (`MetaverseServer.MarkOrphanedMvosForDeletionAsync`) marks MVOs orphaned by the system's removal for housekeeping deletion.
- Configuration change capture snapshots the trigger list via `ConfigurationSnapshotService.BuildDeletionTriggerSystems`.

### Model changes (`JIM.Models`)

1. New enum in `CoreEnums.cs`:
   ```csharp
   public enum AuthoritativeSourceTriggerMode
   {
       SpecificSourcesDisconnect = 0, // matches pre-existing rows, which read the column default
       AllSourcesDisconnect = 1
   }
   ```
2. `MetaverseObjectType`:
   ```csharp
   public AuthoritativeSourceTriggerMode DeletionTriggerMode { get; set; }
       = AuthoritativeSourceTriggerMode.AllSourcesDisconnect;
   ```
   The split between enum numeric default and property initialiser is deliberate: existing rows read the added column's default value `0` (`SpecificSourcesDisconnect`), preserving #115 behaviour with no backfill, while new entities constructed in code, the portal, or the API start at the safe default (`AllSourcesDisconnect`). A unit test pins each side of this.
3. `MetaverseObject`:
   - `int? DeletionTriggeredBySystemId` plus `string? DeletionTriggeredBySystemName` (name snapshot survives system deletion). Set when a deletion is scheduled; cleared with the other deletion markers. This makes cancellation precise (see below) and lets the Pending Deletions page show what triggered each scheduled deletion.
   - `string? DeletionPolicySnapshotJson`: the decision-time policy snapshot (see below), captured at mark-time so housekeeping can carry it onto the final deletion record after the grace period.
4. New model class `MvoDeletionPolicySnapshot` (`JIM.Models/Sync/`), serialised to the JSON columns: deletion rule, trigger mode, selected sources (ids and names), grace period, triggering system (id and name), and the source systems still connected at decision time.
5. Validation (unchanged from #115, enforced at API and portal): `WhenAuthoritativeSourceDisconnected` requires at least one selected source.

### Evaluation changes (`JIM.Application` / `JIM.Worker`)

1. `ISyncEngine.EvaluateMvoDeletionRule` gains the identities of remaining joined systems:
   ```csharp
   MvoDeletionDecision EvaluateMvoDeletionRule(
       MetaverseObject mvo,
       int disconnectingSystemId,
       IReadOnlyCollection<int> remainingConnectedSystemIds);
   ```
   `remainingCsoCount` is derived (`remainingConnectedSystemIds.Count`, counting one entry per CSO). Decision logic per mode:
   - Specific: trigger if `disconnectingSystemId` is in the source list.
   - All: trigger if `disconnectingSystemId` is in the source list AND no remaining joined CSO belongs to any listed source.
   - Empty source list keeps the existing warn-and-fall-back-to-last-connector behaviour.
2. Replace the worker call sites' count query with a new raw SQL repository method `GetJoinedConnectedSystemIdsByMetaverseObjectIdAsync(mvoId)` returning the Connected System id of each joined CSO (one row per CSO). This is one query where two pieces of information are needed, so the hot path (see the #986/#993/#1003 performance work) gains no extra round trip; the existing count-only call sites it replaces are removed. Callers exclude the disconnecting CSO exactly as the count-minus-one logic does today.
3. When a deletion is scheduled or executed, record `DeletionTriggeredBySystemId`/`Name` alongside the existing `DeletionInitiatedBy*` fields.

### Mode-aware grace period cancellation (in scope)

New pure engine method, used by both cancellation paths:

```csharp
bool ShouldCancelScheduledDeletion(MetaverseObject mvo, int rejoiningSystemId);
```

- `WhenLastConnectorDisconnected`: any rejoin cancels (a connector now exists; the condition no longer holds). Current behaviour, still correct.
- `WhenAuthoritativeSourceDisconnected`, Specific mode: cancel only when the rejoining system is the recorded `DeletionTriggeredBySystemId` (the disconnection that caused the scheduling has been undone). Re-deriving "is some source still disconnected" from current state is not possible without join history, which is exactly why the triggering system is recorded.
- All mode: cancel when the rejoining system is any listed source (the "all sources gone" condition is now false).
- Null `DeletionTriggeredBySystemId` (rows marked before this feature ships): fall back to the current cancel-on-any-rejoin behaviour rather than stranding a scheduled deletion.

Wire into `EstablishJoinAsync` and the `FlushPendingMvoDeletionsAsync` same-page reconnect check, replacing the unconditional clears.

### Connected System deletion path

`MetaverseServer.MarkOrphanedMvosForDeletionAsync` (invoked by `ConnectedSystemServer.ExecuteDeletionAsync` when `EvaluateMvoDeletionRules` is set) and `GetMvosOrphanedByConnectedSystemDeletionAsync` must apply the same mode semantics when deciding which MVOs the system's deletion orphans: in All mode, deleting one of two still-connected sources must not mark MVOs whose other source remains. `ConnectedSystemDeletionPreview` counts must agree with what execution will do.

### Decision-time policy snapshot (causality integrity)

Deletion decisions must remain explainable after the configuration changes. Today the RPEI detail page renders deletion rule context from the object type's *current* configuration, which silently misrepresents historic decisions once an admin edits the rule (an interim caveat labelling the display as current configuration shipped ahead of this work). The durable fix: capture the facts that produced each decision, at decision time, on the decision record itself. This follows the established event-time denormalisation pattern (`DeletionInitiatedByName`, `CreatedByName`, the pre-deletion display name capture for #1086).

- `ActivityRunProfileExecutionItems` gains `DeletionPolicySnapshotJson` (`MvoDeletionPolicySnapshot`, above), written whenever a deletion rule evaluation records an outcome: scheduled, deleted, or evaluated-but-not-triggered. For grace period deletions the snapshot is captured at mark-time (on the MVO) and copied onto the housekeeping deletion record at execution, so the final record reflects the policy that scheduled it, not the policy at execution time.
- The snapshot is the source of truth for rendering: `MvoDeletionDecision.Reason` strings become a rendering of the snapshot in the mode vocabulary (for example "All sources mode: 1 of 2 sources remains connected (Active Directory)"), and the refined Causality view (#1087) reads the same structured facts.
- No configuration version pointer is stored: timestamp correlation against configuration change history covers deep audit without coupling the causality view to snapshot reconstruction.
- The RPEI columns hit the raw SQL bulk writers: `RpeiBulkColumns` and its writers must be extended, with the completeness and `RequiresPostgres` round-trip tests updated (same guard as the `MetaverseObjects` columns).

### Surfacing decisions

- `ActivityRunProfileExecutionItemDetail.razor` renders deletion rule context from the decision-time snapshot when present; legacy records without a snapshot fall back to current configuration with the already-shipped "current configuration" caveat.
- `PendingDeletionList.razor` gains a "Triggered by" column from the new snapshot fields.
- `ConfigurationSnapshotService` snapshots the trigger mode so configuration change history diffs it.

### Surface parity

All three surfaces ship in the same PR (per the surface parity rule):

- **Portal** (`MetaverseObjectTypeDetail.razor`): per the agreed mockup; the existing per-system selection checkboxes are retained, with a Deletion Trigger radio group above them ("All sources disconnect" / "Specific source(s) disconnect", All pre-selected for new configurations) and a live plain-language summary alert restating trigger, sources by name, and grace period. `SchemaObjectTypeList.razor` deletion rule tooltip includes the mode.
- **REST** (`MetaverseController`, `MetaverseObjectTypeDetailDto` and create/update requests): `DeletionTriggerMode` (string enum, consistent with existing enum serialisation). Omitted on create means `AllSourcesDisconnect`; omitted on update means unchanged. Extract the currently duplicated create/update validation into a shared helper rather than duplicating it further.
- **PowerShell** (`New-JIMMetaverseObjectType` / `Set-JIMMetaverseObjectType`): `-DeletionTriggerMode` (`ValidateSet`), Pester coverage including `EnumSerialisation.Tests.ps1`, and `docs/powershell/metaverse.md` updates.

### Migration and bulk-write guard

- EF migration adds `DeletionTriggerMode` to `MetaverseObjectTypes` with column default `0` (`SpecificSourcesDisconnect`) so existing rows keep #115 behaviour; `DeletionTriggeredBySystemId`, `DeletionTriggeredBySystemName` and `DeletionPolicySnapshotJson` to `MetaverseObjects`; and `DeletionPolicySnapshotJson` to `ActivityRunProfileExecutionItems`.
- The `MetaverseObjects` and `ActivityRunProfileExecutionItems` columns hit the raw SQL bulk writers: extend `MvoBulkInsertColumns` and `RpeiBulkColumns` plus their writers (values in list order), place the columns consciously in update or exclusion lists, and extend the `RequiresPostgres` round-trip tests. `BulkInsertColumnCompletenessTests` will fail until this is done; that is the guard working as designed.

## Implementation Phases

Each phase is red-first TDD: failing tests, minimum implementation, green, refactor.

1. **Model, migration, snapshot, bulk-write guard.** Enum, `MetaverseObjectType`, `MetaverseObject` and RPEI properties, `MvoDeletionPolicySnapshot` model, migration (including the existing-rows-keep-Specific / new-entities-default-All split, pinned by tests), `MvoBulkInsertColumns` and `RpeiBulkColumns` extensions plus round-trip tests, `ConfigurationSnapshotService` coverage.
2. **Engine evaluation.** New `EvaluateMvoDeletionRule` signature and mode matrix (unit tests: each mode times disconnecting-system-in/out-of-list times remaining-source permutations, empty-list fallback, Internal origin protection unchanged). `ShouldCancelScheduledDeletion` matrix including the null-trigger fallback.
3. **Worker wiring.** `GetJoinedConnectedSystemIdsByMetaverseObjectIdAsync` (raw SQL), both disconnect call sites, trigger recording, policy snapshot capture on evaluation outcomes (including the mark-time capture and housekeeping carry-through), mode-aware cancellation in `EstablishJoinAsync` and `FlushPendingMvoDeletionsAsync`. `DeletionRuleWorkflowTests` additions for All-mode end-to-end marking, snapshot persistence, plus rejoin cancellation workflows.
4. **Connected System deletion alignment.** Mode-aware orphan marking and deletion preview counts, with unit coverage.
5. **REST API.** DTOs, shared validation helper, controller tests.
6. **PowerShell.** Parameter, Pester tests, cmdlet docs.
7. **Portal UI.** Deletion Rules panel per mockup, list page tooltip, RPEI detail context rendered from the decision-time snapshot (current-configuration fallback for legacy records), Pending Deletions "Triggered by" column.
8. **Integration, docs, changelog.** Extend the deletion rules integration scenario (Scenario 4) with an All mode case and a mode-aware cancellation case; regression-run Scenario 8. Update `docs/developer/diagrams/MVO_DELETION_AND_GRACE_PERIOD.md`, admin docs under `docs/`, `DELETION_RULES_DESIGN.md`, and `CHANGELOG.md` (user-facing feature entry plus docs).

## Success Criteria

- Both modes behave per the table above at both disconnect call sites, on the Connected System deletion path, and in housekeeping.
- A rejoin during grace cancels a scheduled deletion only when the mode's trigger condition no longer holds; a non-trigger system rejoining no longer cancels.
- Existing configurations behave identically after migration (Specific mode), demonstrated by the unchanged pre-existing workflow tests; newly configured object types default to All mode on every surface.
- Decision reasons name the mode and the relevant sources on RPEI outcomes, Pending Deletions, and Causality surfaces, rendered from the decision-time policy snapshot so they remain accurate after configuration changes.
- Portal, REST, and PowerShell parity in one PR; `dotnet build JIM.sln` and `dotnet test JIM.sln` clean; integration scenarios green.

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Existing configurations silently flipping to the new All default | Column default `0` maps existing rows to Specific; property initialiser gives new entities All; both sides pinned by dedicated tests. |
| Hot-path regression from fetching system ids per disconnection | The id-list query replaces the existing count query (one query either way); raw SQL per the worker hot-path rule; validate against the cohort deprovisioning scale scenarios. |
| Cancellation decisions for deletions scheduled before upgrade (no recorded trigger) | Explicit null fallback to current cancel-on-any-rejoin behaviour; covered by a dedicated test. |
| Connected System deletion preview and execution disagreeing under new modes | Preview and execution share the mode-aware predicate; unit tests assert agreement. |
| In-memory provider masking join/tracking bugs in new raw SQL | `RequiresPostgres` round-trip and tracked-instance regression tests per the raw SQL rules in `src/CLAUDE.md`. |
| RPEI row growth from policy snapshots at leaver-cohort scale | Compact JSON written only on deletion-evaluation outcomes (a small fraction of RPEIs); a 100k-leaver cohort adds roughly 30 MB, small relative to the attribute change payloads those RPEIs already carry. |

## Dependencies

- #115 (shipped). No new packages or services.
