# Authoritative Source Trigger Modes

- **Status:** Planned
- **Issue:** [#119](https://github.com/TetronIO/JIM/issues/119)
- **Related design doc:** [`DELETION_RULES_DESIGN.md`](doing/DELETION_RULES_DESIGN.md)

## Overview

Extend the `WhenAuthoritativeSourceDisconnected` deletion rule with a configurable trigger mode so Metaverse Object deletion can require all authoritative sources to disconnect, or only specifically designated ones, rather than any. Also make grace period cancellation mode-aware, so a rejoin only cancels a scheduled deletion when the trigger condition no longer holds.

There is no priority ordering: the original priority/hierarchy concept from issue #119 collapsed into the Specific mode designation (design decision 2026-08-01, UI mockup agreed).

## Business Value

- Prevents deletion when only one of multiple redundant sources has an issue (for example an HR system rebuild), the single biggest deletion-safety gap in the current Any-only behaviour.
- Supports enterprises with multiple identity sources (global plus regional HR) without forcing a single system to be the sole lifecycle authority.
- Fixes an existing over-broad behaviour: today any system rejoining during a grace period cancels the scheduled deletion, even a system that had nothing to do with triggering it.

## Trigger Modes

| Mode | Behaviour |
|------|-----------|
| `AnySourceDisconnects` (default) | Delete when any listed source disconnects, even if others remain connected. Current behaviour; existing configurations migrate here. |
| `AllSourcesDisconnect` | Delete only once no listed source retains a joined Connected System Object. Non-source connectors (targets) do not block deletion. |
| `SpecificSourcesDisconnect` | Each listed source carries a "triggers deletion" designation. Delete when any designated source disconnects, regardless of other sources. Unmarked sources never trigger deletion on their own. |

**Design note (equivalence):** Specific mode is functionally equivalent to Any mode over the designated subset. Its value is UX: the source list remains a complete statement of which systems are authoritative for the type, while the designation makes trigger intent explicit. The evaluator normalises all three modes to one routine (an effective trigger set plus an all-gone check), so no mode-specific branching spreads through the engine.

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
       AnySourceDisconnects = 0,      // default; existing rows land here
       AllSourcesDisconnect = 1,
       SpecificSourcesDisconnect = 2
   }
   ```
2. `MetaverseObjectType`:
   - `AuthoritativeSourceTriggerMode DeletionTriggerMode` (default `AnySourceDisconnects`)
   - `List<int> DeletionTriggerDesignatedSystemIds` (subset of `DeletionTriggerConnectedSystemIds`; meaningful only in Specific mode)
3. `MetaverseObject`:
   - `int? DeletionTriggeredBySystemId` plus `string? DeletionTriggeredBySystemName` (name snapshot survives system deletion). Set when a deletion is scheduled; cleared with the other deletion markers. This makes cancellation precise (see below) and lets the Pending Deletions page show what triggered each scheduled deletion.
4. Validation rules (enforced at API and portal):
   - Specific mode requires at least one designated system id, and every designated id must be in the source list.
   - All modes keep the existing rule: `WhenAuthoritativeSourceDisconnected` requires at least one source.

### Evaluation changes (`JIM.Application` / `JIM.Worker`)

1. `ISyncEngine.EvaluateMvoDeletionRule` gains the identities of remaining joined systems:
   ```csharp
   MvoDeletionDecision EvaluateMvoDeletionRule(
       MetaverseObject mvo,
       int disconnectingSystemId,
       IReadOnlyCollection<int> remainingConnectedSystemIds);
   ```
   `remainingCsoCount` is derived (`remainingConnectedSystemIds.Count`, counting one entry per CSO). Decision logic per mode:
   - Any: trigger if `disconnectingSystemId` is in the source list.
   - All: trigger if `disconnectingSystemId` is in the source list AND no remaining joined CSO belongs to any listed source.
   - Specific: trigger if `disconnectingSystemId` is in the designated list.
   - Empty source list keeps the existing warn-and-fall-back-to-last-connector behaviour.
2. Replace the worker call sites' count query with a new raw SQL repository method `GetJoinedConnectedSystemIdsByMetaverseObjectIdAsync(mvoId)` returning the Connected System id of each joined CSO (one row per CSO). This is one query where two pieces of information are needed, so the hot path (see the #986/#993/#1003 performance work) gains no extra round trip; the existing count-only call sites it replaces are removed. Callers exclude the disconnecting CSO exactly as the count-minus-one logic does today.
3. When a deletion is scheduled or executed, record `DeletionTriggeredBySystemId`/`Name` alongside the existing `DeletionInitiatedBy*` fields.

### Mode-aware grace period cancellation (in scope)

New pure engine method, used by both cancellation paths:

```csharp
bool ShouldCancelScheduledDeletion(MetaverseObject mvo, int rejoiningSystemId);
```

- `WhenLastConnectorDisconnected`: any rejoin cancels (a connector now exists; the condition no longer holds). Current behaviour, still correct.
- `WhenAuthoritativeSourceDisconnected`, Any and Specific modes: cancel only when the rejoining system is the recorded `DeletionTriggeredBySystemId` (the disconnection that caused the scheduling has been undone). Re-deriving "is some source still disconnected" from current state is not possible without join history, which is exactly why the triggering system is recorded.
- All mode: cancel when the rejoining system is any listed source (the "all sources gone" condition is now false).
- Null `DeletionTriggeredBySystemId` (rows marked before this feature ships): fall back to the current cancel-on-any-rejoin behaviour rather than stranding a scheduled deletion.

Wire into `EstablishJoinAsync` and the `FlushPendingMvoDeletionsAsync` same-page reconnect check, replacing the unconditional clears.

### Connected System deletion path

`MetaverseServer.MarkOrphanedMvosForDeletionAsync` (invoked by `ConnectedSystemServer.ExecuteDeletionAsync` when `EvaluateMvoDeletionRules` is set) and `GetMvosOrphanedByConnectedSystemDeletionAsync` must apply the same mode semantics when deciding which MVOs the system's deletion orphans: in All mode, deleting one of two still-connected sources must not mark MVOs whose other source remains; in Specific mode, deleting an unmarked source must not mark at all. `ConnectedSystemDeletionPreview` counts must agree with what execution will do.

### Surfacing decisions

- `MvoDeletionDecision.Reason` strings adopt the mode vocabulary (for example "All sources mode: 1 of 2 sources remains connected (Active Directory)"), flowing through to RPEI outcomes and the Causality view (#1086).
- `ActivityRunProfileExecutionItemDetail.razor` deletion rule context panel shows the mode and, in Specific mode, which sources are designated.
- `PendingDeletionList.razor` gains a "Triggered by" column from the new snapshot fields.
- `ConfigurationSnapshotService` snapshots the mode and designated list so configuration change history diffs them.

### Surface parity

All three surfaces ship in the same PR (per the surface parity rule):

- **Portal** (`MetaverseObjectTypeDetail.razor`): per the agreed mockup; a Deletion Trigger radio group (Any / All / Specific), a per-source "Triggers deletion" checkbox visible only in Specific mode, and a live plain-language summary alert restating trigger, sources by name, and grace period. `SchemaObjectTypeList.razor` deletion rule tooltip includes the mode.
- **REST** (`MetaverseController`, `MetaverseObjectTypeDetailDto` and create/update requests): `DeletionTriggerMode` (string enum, consistent with existing enum serialisation) and `DeletionTriggerDesignatedSystemIds`, with the validation rules above on both create and update paths (extract the currently duplicated validation into a shared helper rather than duplicating a third time).
- **PowerShell** (`New-JIMMetaverseObjectType` / `Set-JIMMetaverseObjectType`): `-DeletionTriggerMode` (`ValidateSet`) and `-DeletionTriggerDesignatedSystemIds int[]`, Pester coverage including `EnumSerialisation.Tests.ps1`, and `docs/powershell/metaverse.md` updates.

### Migration and bulk-write guard

- EF migration adds `DeletionTriggerMode` (default 0) and `DeletionTriggerDesignatedSystemIds` (default empty) to `MetaverseObjectTypes`, and `DeletionTriggeredBySystemId` / `DeletionTriggeredBySystemName` to `MetaverseObjects`.
- The `MetaverseObjects` columns hit the raw SQL bulk writers: extend `MvoBulkInsertColumns` and its writers (values in list order), place the columns consciously in update or exclusion lists, and extend the `RequiresPostgres` round-trip test. `BulkInsertColumnCompletenessTests` will fail until this is done; that is the guard working as designed.

## Implementation Phases

Each phase is red-first TDD: failing tests, minimum implementation, green, refactor.

1. **Model, migration, snapshot, bulk-write guard.** Enum, `MetaverseObjectType` and `MetaverseObject` properties, migration, `MvoBulkInsertColumns` extension plus round-trip test, `ConfigurationSnapshotService` coverage.
2. **Engine evaluation.** New `EvaluateMvoDeletionRule` signature and mode matrix (unit tests: each mode times disconnecting-system-in/out-of-list times remaining-source permutations, empty-list fallback, Internal origin protection unchanged). `ShouldCancelScheduledDeletion` matrix including the null-trigger fallback.
3. **Worker wiring.** `GetJoinedConnectedSystemIdsByMetaverseObjectIdAsync` (raw SQL), both disconnect call sites, trigger recording, mode-aware cancellation in `EstablishJoinAsync` and `FlushPendingMvoDeletionsAsync`. `DeletionRuleWorkflowTests` additions for All and Specific end-to-end marking, plus rejoin cancellation workflows.
4. **Connected System deletion alignment.** Mode-aware orphan marking and deletion preview counts, with unit coverage.
5. **REST API.** DTOs, shared validation helper, controller tests.
6. **PowerShell.** Parameters, Pester tests, cmdlet docs.
7. **Portal UI.** Deletion Rules panel per mockup, list page tooltip, RPEI detail context, Pending Deletions "Triggered by" column.
8. **Integration, docs, changelog.** Extend the deletion rules integration scenario (Scenario 4) with All and Specific mode cases and a mode-aware cancellation case; regression-run Scenario 8. Update `docs/developer/diagrams/MVO_DELETION_AND_GRACE_PERIOD.md`, admin docs under `docs/`, `DELETION_RULES_DESIGN.md`, and `CHANGELOG.md` (user-facing feature entry plus docs).

## Success Criteria

- All three modes behave per the table above at both disconnect call sites, on the Connected System deletion path, and in housekeeping.
- A rejoin during grace cancels a scheduled deletion only when the mode's trigger condition no longer holds; a non-trigger source rejoining no longer cancels.
- Existing configurations behave identically after migration (Any mode, no designated ids), demonstrated by the unchanged pre-existing workflow tests.
- Decision reasons name the mode and the relevant sources on RPEI outcomes, Pending Deletions, and Causality surfaces.
- Portal, REST, and PowerShell parity in one PR; `dotnet build JIM.sln` and `dotnet test JIM.sln` clean; integration scenarios green.

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Hot-path regression from fetching system ids per disconnection | The id-list query replaces the existing count query (one query either way); raw SQL per the worker hot-path rule; validate against the cohort deprovisioning scale scenarios. |
| Cancellation decisions for deletions scheduled before upgrade (no recorded trigger) | Explicit null fallback to current cancel-on-any-rejoin behaviour; covered by a dedicated test. |
| Connected System deletion preview and execution disagreeing under new modes | Preview and execution share the mode-aware predicate; unit tests assert agreement. |
| In-memory provider masking join/tracking bugs in new raw SQL | `RequiresPostgres` round-trip and tracked-instance regression tests per the raw SQL rules in `src/CLAUDE.md`. |
| Admin confusion between Specific mode and simply listing fewer sources in Any mode | Live summary sentence in the portal restates the effective behaviour; docs call out the equivalence. |

## Dependencies

- #115 (shipped). No new packages or services.
