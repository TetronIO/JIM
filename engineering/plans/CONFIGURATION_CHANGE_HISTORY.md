# Configuration Change History - Implementation Plan

- **Status:** Planned
- **Issue:** [#14](https://github.com/TetronIO/JIM/issues/14)
- **PRD:** [`engineering/prd/PRD_CONFIGURATION_CHANGE_HISTORY.md`](../prd/PRD_CONFIGURATION_CHANGE_HISTORY.md)

## Overview

Extend JIM's change-history capability from business/identity data (Connected System Objects and Metaverse Objects, delivered under #269) to **configuration objects** (Synchronisation Rule, Connected System, and, in later increments, the other `IAuditable` configuration types). For each configuration change, JIM will capture a complete, redaction-aware, versioned snapshot of the object's post-change state, carried with the change's `Activity`. Administrators will see a per-object "Changes" tab with a structured tree diff and version compare; auditors will find configuration changes via enhanced Activities-list filters; and the whole capability has full REST API and PowerShell parity. Rollback is designed for but deferred to a fast-follow.

This is the *how*. The *what and why*, scope decisions, scenarios, and acceptance criteria are in the PRD; this plan does not restate them.

## Business Value

Configuration changes are the highest-leverage actions in JIM (one Synchronisation Rule edit can reshape thousands of identities) yet are currently the least traceable: JIM records that a configuration object changed and who changed it, but not *what* changed. This plan closes that gap, delivering self-service investigation ("what did this rule look like last week, and who changed it"), compliance-grade change history for high-trust deployments, and the foundation for configuration rollback.

## Current State Analysis

### What exists (to reuse, not rebuild)

| Component | Status | Notes |
|-----------|--------|-------|
| `Activity` envelope for config changes | Exists | Every config CRUD creates an `Activity` with the right `TargetType` (`ConnectedSystem`, `SyncRule`, `MetaverseAttribute`, `ServiceSetting`, etc.), `TargetOperationType` (`Create`/`Update`/`Delete`/`Revert`), and initiator triad. The change payload is the missing piece. |
| Uniform capture pattern | Exists | Every config CRUD method follows: create `Activity` → persist entity → `CompleteActivityAsync`. The full object graph is in memory at persist time. |
| `ChangeHistoryTimeline.razor` | Exists | Shared CSO/MVO timeline shell (lazy load, badge, load-more, search/filter, details dialog, Activity link). Reusable shell; needs a config-specific detail renderer. |
| `ChangeHistoryServer` / `ChangeHistoryRepository` | Exists | Cleanup + retrieval for CSO/MVO changes. Extend for configuration. |
| Worker housekeeping cleanup | Exists | `PerformChangeHistoryCleanupAsync` runs every 6h; deletes expired CSO/MVO changes + Activities. Make Activity cleanup target-type-aware. |
| `ChangeTracking.*.Enabled` Service Settings | Exists | `CsoChanges`/`MvoChanges` toggles. Add a `ConfigurationChanges` equivalent. |
| Change-history cmdlets + endpoints | Exists | `Get-JIMConnectedSystemObjectChangeHistory`, `Get-JIMMetaverseObjectChangeHistory`, `GET .../change-history`, `PaginatedResponse<T>`. Mirror for configuration. |
| Redaction marking | Exists | `ConnectedSystemSettingType.StringEncrypted` + `ConnectedSystemSettingValue.StringEncryptedValue` (prefix `$JIM$v1$`); `CredentialProtectionService`. The key for redaction. |
| Snapshot capture for config objects | Missing | New. |
| Config "Changes" tab + tree-diff renderer | Missing | New. |
| Optional reason-for-change | Missing | No reason/comment field exists anywhere (only an isolated certificate `Notes`). New. |

### Key code locations (as of authoring)

| Purpose | File | Reference |
|---------|------|-----------|
| Connected System create/update/delete + Activity | `src/JIM.Application/Servers/ConnectedSystemServer.cs` | `CreateConnectedSystemAsync` (115), `UpdateConnectedSystemAsync` (234), `DeleteAsync` (775, already has `deleteChangeHistory`) |
| Synchronisation Rule create/update/delete + Activity | `src/JIM.Application/Servers/ConnectedSystemServer.cs` | `CreateOrUpdateSyncRuleAsync` (4512, has `parentActivity`), `DeleteSyncRuleAsync` (4668) |
| Simple config pattern (generalisation check) | `src/JIM.Application/Servers/MetaverseServer.cs` | Metaverse Attribute create/update/delete (170/196/225) |
| Redaction key | `src/JIM.Models/Staging/ConnectedSystemSettingValue.cs` (`StringEncryptedValue`, 24); `ConnectedSystemEnums.cs` (`ConnectedSystemSettingType.StringEncrypted`, ~91) | |
| Encryption utility | `src/JIM.Application/Services/CredentialProtectionService.cs` | `Protect`/`Unprotect`, prefix `$JIM$v1$` (21) |
| Snapshot scope - SyncRule | `src/JIM.Models/Logic/SyncRule.cs` | `AttributeFlowRules`, `ObjectMatchingRules`, `ObjectScopingCriteriaGroups` (include); `Activities`, `ConnectedSystem` (exclude: cycles) |
| Snapshot scope - ConnectedSystem | `src/JIM.Models/Staging/ConnectedSystem.cs` | `RunProfiles`, `ObjectTypes`, `Partitions`, `SettingValues` (include, redact secrets); `Objects`, `PendingExports`, `Activities` (exclude: huge/cycles) |
| Activity model + EF | `src/JIM.Models/Activities/Activity.cs`; `src/JIM.PostgresData/JimDbContext.cs` (`Entity<Activity>()`) | Add jsonb + reason + version columns |
| Activity API DTO | `src/JIM.Web/Models/Api/ActivityDtos.cs` | `ActivityDetailDto` (267), `FromEntity` (408) |
| Activities list view | `src/JIM.Web/Pages/ActivityList.razor` | `Type` filter already includes config types; add category/initiator/date filters + URL persistence |
| Config detail pages | `src/JIM.Web/Pages/Admin/ConnectedSystemDetail.razor`, `SyncRuleDetail.razor` | `NavigableMudTabs` with `?t=` deep-link; add "Changes" tab |
| PowerShell module | `src/JIM.PowerShell/` | `Verb-JIM<Entity>`, `Invoke-JIMApi`; write cmdlets `Set-JIMSyncRule`/`Set-JIMConnectedSystem`; tests in `Tests/` (Pester) |

## Technical Architecture

### Storage model (carried on the Activity)

Three nullable columns added to `Activity` (only populated for configuration-change activities; null for the high-volume sync/identity activities, so no bloat on the common path):

- `ConfigurationChangeSnapshot` (`jsonb`, nullable): the scoped, redacted, post-change snapshot document.
- `ChangeReason` (`text`, nullable): the optional reason. Kept general (any Activity may carry a reason), not config-specific.
- `ConfigurationChangeVersion` (`int`, nullable): per-object version number.

No DbContext configuration is needed beyond the migration; `Activity` is a simple top-level entity. This realises the PRD's "store with the Activity" direction: existing Activity retrieval (`GET /api/v1/activities/{id}`, `Get-JIMActivity`) automatically carries the payload, and retention becomes a property of Activity retention (see Phase 6).

### Snapshot and redaction

A `ConfigurationSnapshotService` (JIM.Application) produces a **purpose-built, deliberately-scoped projection** per configuration type, serialised with `System.Text.Json`. It is **not** a naive serialisation of the EF graph (which would pull cycles, huge operational collections, and secrets). Per type:

- **Synchronisation Rule**: scalars + `IAuditable` stamp fields + FK ids/names; child collections `AttributeFlowRules`, `ObjectMatchingRules`, `ObjectScopingCriteriaGroups`. Exclude the `Activities` backlink and the parent `ConnectedSystem` navigation (cycles).
- **Connected System**: scalars + `IAuditable` stamp fields + FK ids/names; child collections `RunProfiles`, `ObjectTypes`, `Partitions`, `SettingValues`. Exclude `Objects` and `PendingExports` (large operational data, not configuration) and `Activities` (cycle).

**Redaction (hard requirement):** for any `ConnectedSystemSettingValue` whose `Setting.Type == StringEncrypted`, the snapshot never stores the encrypted or plaintext value. To still detect and show "secret changed" without disclosing it, the snapshot stores a **keyed hash** (HMAC-SHA-256 with a server-held key) of the transiently-decrypted plaintext. The keyed hash prevents the stored history from being an offline brute-force oracle for weak secrets, while letting the diff engine report that a credential was rotated. `PersistedConnectorData` is treated as opaque and excluded. (Alternative if decrypt-at-snapshot is undesirable: store only a "secret (value not tracked)" sentinel and never report secret changes; recommended default is the keyed hash.)

### Diff engine

A `ConfigurationDiffService` computes a structured diff tree between two snapshots:

- Scalars: field-by-field; equal → unchanged, differ → modified (old, new).
- Child collections: match items by **stable DB id** (`SyncRuleMapping.Id`, `ObjectMatchingRule.Id`, `SyncRuleScopingCriteriaGroup.Id`, `ConnectedSystemRunProfile.Id`, `ConnectedSystemObjectType.Id`, `ConnectedSystemPartition.Id`, `ConnectedSystemSettingValue` by `SettingId`), then recurse into matched pairs; unmatched-in-new = added, unmatched-in-old = removed.
- Output: a tree of nodes (`path`, `changeType` add/remove/modify/unchanged, `oldValue`, `newValue`, friendly label, `children`).

Because these entities carry persistent DB ids, child matching is stable across edits (this resolves the PRD's nested-identity open question). The same diff tree feeds all three surfaces: the UI renders it as a collapsible tree; PowerShell renders it as a git-style coloured unified diff; the API returns it as data.

### Versioning and retrieval

- At capture, `ConfigurationChangeVersion = (max existing version for that object) + 1`, stored on the Activity. Robust against retention removing older entries (the version number does not renumber).
- Retrieval queries config-change Activities for the object (`SyncRuleId` / `ConnectedSystemId` + `TargetType` + snapshot not null), newest first.
  - **Summary**: paged headers (version, who, when, reason, one-line change summary).
  - **Single change**: the chosen snapshot plus a diff against its immediate predecessor.
  - **Compare**: any two versions; diff their snapshots.

### Capture data flow

On a configuration save (per CRUD method), after the entity is persisted and before `CompleteActivityAsync`:

1. If `ChangeTracking.ConfigurationChanges.Enabled` is false, skip.
2. Build the scoped, redacted snapshot for the just-persisted object.
3. Set `ConfigurationChangeSnapshot`, `ConfigurationChangeVersion`, and (if supplied) `ChangeReason` on the in-flight `Activity`.
4. Complete the Activity (single existing persistence point; no extra round trip).

The optional reason is threaded into the existing CRUD methods as an optional trailing parameter (`string? changeReason = null`), consistent with their existing optional parameters (`parentActivity = null`). If call-site sprawl becomes unwieldy, a small `ChangeContext` value object is the fallback.

### UI

Reuse the `ChangeHistoryTimeline` shell (lazy load, badge, load-more, version list) with a **pluggable detail renderer**: the existing flat attribute table for CSO/MVO, and a new configuration tree-diff renderer for configuration objects. Add a "Changes" tab to `ConnectedSystemDetail.razor` and `SyncRuleDetail.razor` (`NavigableMudTabs`, `?t=changes` deep-link, count badge, lazy load), with version list and compare-two-versions. A comment-on-save dialog on these pages captures the optional reason and passes it to the save handler.

### REST API

- Optional `ChangeReason` added to `CreateSyncRuleRequest`, `UpdateSyncRuleRequest`, `UpdateConnectedSystemRequest` (and create), and supplied on delete via a `?changeReason=` query parameter (resolves PRD open question: query parameter, since HTTP DELETE-with-body is awkward for clients).
- New per-object endpoints mirroring CSO/MVO: `GET /api/v1/synchronisation/sync-rules/{id}/change-history` (paged summary) and `GET .../sync-rules/{id}/change-history/{version}` (single change + diff); same shape for `connected-systems/{id}`.
- `ActivityDetailDto` gains the snapshot, reason, and version fields.
- The API returns structured diff data; it does **not** pre-render ANSI (resolves PRD open question: colourised rendering is PowerShell-side). A pre-rendered unified-diff text representation for non-PowerShell clients is a possible later addition, not in scope.

### PowerShell

- `-ChangeReason` added to `Set-JIMSyncRule`, `New-JIMSyncRule`, `Remove-JIMSyncRule`, `Set-JIMConnectedSystem`, `New-JIMConnectedSystem` (and Connected System remove).
- New `Get-JIMConfigurationChangeHistory` cmdlet, targeting an object by id or name (like siblings), with: summary/outline mode (capped or `-Page`/`-PageSize`/`-All`); and single-change mode (`-ChangeId`/`-Version`) returning raw structured data (`-Raw`) or a git-style colour-coded diff (`-AsDiff`) rendered via PowerShell 7 `$PSStyle`.
- Pester tests under `src/JIM.PowerShell/Tests/`.

### Retention (type-aware Activity retention)

Because the payload lives on the Activity, "keep configuration history longer than identity data" becomes target-type-aware Activity retention. Add a configuration-change Activity retention Service Setting (default notably longer than the identity default; see Open Questions), and make `PerformChangeHistoryCleanupAsync` retain configuration-change Activities for their own period while continuing to flush sync/identity Activities on the existing schedule. Cleanup remains recorded via an Activity.

## UI Mockups

Illustrative ASCII mocks of each impacted surface. They show intent and information hierarchy, not pixel-exact MudBlazor layout. Colour is described in words: in the web UI it maps to MudBlazor `Success` (green) / `Error` (red) / `Warning` (amber); in PowerShell it maps to `$PSStyle` ANSI, git-style.

### 1. Changes tab: version timeline (per-object entry view)

Added as a new tab on `SyncRuleDetail.razor` and `ConnectedSystemDetail.razor`, with a count badge and lazy load. The `AuditInfo` chips (Created / Updated) already sit in the page header.

```
Synchronisation Rule: HR Inbound          [Created 2 Jun by A. Mehta · Updated 3h ago by J. Doe]
┌───────────────────────────────────────────────────────────────────────────────────────────┐
│ Details │ Matching │ Scope │ Attribute Flow │ Changes (7) │ Danger Zone                      │
├───────────────────────────────────────────────────────────────────────────────────────────┤
│  [ Search changes… ]   [ Change type ▾ ]   [ Initiator ▾ ]            [ Compare versions ]   │
│                                                                                              │
│  ●  v7 · Updated      👤 J. Doe (User)            3 hours ago                                 │
│  │     “Tighten scope to exclude contractors (CHG0098)”                                      │
│  │     Scope: +1 criterion · Attribute Flow ‘mail’: source expression changed               │
│  │                                                          [ View diff ]   [ Compare ]      │
│  │                                                                                           │
│  ●  v6 · Updated      ⚙ System (Sync)             yesterday 02:14                            │
│  │     Attribute Flow ‘employeeId’: added                                                    │
│  │                                                          [ View diff ]   [ Compare ]      │
│  │                                                                                           │
│  ●  v5 · Updated      🔑 prov-api (API key)        14 Jun 09:31                               │
│  │     2 Attribute Flows changed                                                             │
│  │                                                          [ View diff ]   [ Compare ]      │
│  ⋮                                                                                           │
│                              [ Load more (2 remaining) ]                                     │
└───────────────────────────────────────────────────────────────────────────────────────────┘
```

### 2. Changes tab: single-version tree diff (the centrepiece)

Opens from “View diff”. Renders the object in its natural tree; unchanged branches collapsed (`▸`); additions green, removals red, modifications amber with old/new lines. `v6 → v7` selectors allow re-pointing the comparison.

```
┌─ Change v7 · Synchronisation Rule “HR Inbound” ─────────────────────────────────────────────┐
│ Updated by 👤 J. Doe (User) · 25 Jun 2026 14:30 · comparing  [ v6 ▾ ] → [ v7 ▾ ]            │
│ Reason: “Tighten scope to exclude contractors (CHG0098)”                      [ View raw ]    │
├─────────────────────────────────────────────────────────────────────────────────────────────┤
│  Synchronisation Rule                                                                         │
│    Details                                                              (no changes)   ▸      │
│    Scope                                                                                ▾      │
│      Group 1 (All of)                                                                          │
│  +     Criterion   employeeType  Is not  “Contractor”                          added          │
│    Attribute Flow                                                                       ▾      │
│  ~     mail                                                                                    │
│  -        Source expression   Trim([mail])                                                     │
│  +        Source expression   Trim(ToLower([mail]))                                            │
│        displayName                                                      (no changes)   ▸       │
│    Object Matching Rules                                                (no changes)   ▸       │
└─────────────────────────────────────────────────────────────────────────────────────────────┘
   Legend:  + added (green)    - removed (red)    ~ modified (amber)    ▸ collapsed, click to expand
```

### 3. Tree diff with a redacted secret (Connected System)

Demonstrates the redaction requirement: a changed credential is shown as changed, never revealing the value (detected via the keyed hash).

```
┌─ Change v4 · Connected System “Active Directory” ───────────────────────────────────────────┐
│ Updated by 👤 A. Mehta (User) · 25 Jun 2026 11:02 · comparing  [ v3 ▾ ] → [ v4 ▾ ]          │
├─────────────────────────────────────────────────────────────────────────────────────────────┤
│  Connected System                                                                             │
│    Settings                                                                             ▾      │
│  ~     Bind password         ●●●●●●●●  →  ●●●●●●●●        secret changed · value hidden 🔒    │
│  ~     Server                                                                                  │
│  -        ldaps://dc1.corp.local                                                               │
│  +        ldaps://dc1.corp.local, ldaps://dc2.corp.local                                       │
│    Run Profiles                                                         (no changes)   ▸       │
│    Object Types                                                         (no changes)   ▸       │
└─────────────────────────────────────────────────────────────────────────────────────────────┘
```

### 4. Comment-on-save dialog (optional reason)

Shown on saving a configuration change in the UI; skippable. Becomes the version’s “commit message”.

```
┌─ Save changes to “HR Inbound”? ───────────────────────────────────┐
│                                                                    │
│  You are updating this Synchronisation Rule.                       │
│                                                                    │
│  Reason for change (optional)                                      │
│  ┌──────────────────────────────────────────────────────────────┐ │
│  │ Tighten scope to exclude contractors (CHG0098)               │ │
│  └──────────────────────────────────────────────────────────────┘ │
│  Shown in the change history and Activities.                       │
│                                                                    │
│                                      [ Cancel ]   [ Save changes ] │
└────────────────────────────────────────────────────────────────────┘
```

### 5. Activities list: category quick-filter, new filters, and a configuration row

No new page (per the agreed scope); the existing `ActivityList.razor` gains a category quick-filter, an initiator-type filter, a date range, and URL-persisted state. A configuration-change row links through to the object’s Changes tab at that version.

```
Activity
┌─────────────────────────────────────────────────────────────────────────────────────────────┐
│ Category:  ( All )  [ Configuration ]  ( Business data )  ( Sync runs )  ( System )           │
│ [ Operation ▾ ] [ Outcome ▾ ] [ Type ▾ ] [ Status ▾ ] [ Initiator ▾ ]  [ From 📅 ] [ To 📅 ] │
│ [ Search target or initiator… ]                                    🔗 filters saved in the URL │
├──────────┬──────────────────────────────┬───────────────────────┬──────────────┬─────────────┤
│ Operation│ Target                       │ Type                  │ Initiated by │ When        │
├──────────┼──────────────────────────────┼───────────────────────┼──────────────┼─────────────┤
│ Updated  │ AD → HR Inbound              │ Synchronisation Rule  │ 👤 J. Doe    │ 3 hours ago │
│          │ “Tighten scope (CHG0098)”    │                       │              │ → View changes
│ Updated  │ Active Directory             │ Connected System      │ 👤 A. Mehta  │ today 11:02 │
│ Created  │ employeeId                   │ Metaverse Attribute   │ 🔑 prov-api  │ 14 Jun      │
└──────────┴──────────────────────────────┴───────────────────────┴──────────────┴─────────────┘
```

### 6. Activity detail: configuration change renders the same diff

The same renderer as the Changes tab, embedded on `ActivityDetail.razor`, so the list → activity → diff path and the object → Changes tab → diff path share one component.

```
Activity: Update Synchronisation Rule “HR Inbound”
┌─────────────────────────────────────────────────────────────────────────────────┐
│ Status: Complete   Initiated by: 👤 J. Doe (User)   When: 3 hours ago            │
│ Reason: “Tighten scope to exclude contractors (CHG0098)”                         │
│ Target: AD → HR Inbound · v6 → v7                          [ Open on object ↗ ]   │
├─────────────────────────────────────────────────────────────────────────────────┤
│  ‹ the same tree diff component as mock 2, rendered inline ›                     │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### 7. Service Settings: enable toggle and split retention

Extends the existing `History` / `ChangeTracking` settings on `Settings.razor`.

```
History & Change Tracking
┌────────────────────────────────────────────────────────────────────────────────┐
│ Track configuration changes               [ On ]                                 │
│ Track Connected System Object changes     [ On ]                                 │
│ Track Metaverse Object changes            [ On ]                                 │
│                                                                                  │
│ Configuration change retention            [  3650  ] days   (≈ 10 years)         │
│ Business-data change retention            [    90  ] days                         │
│ Activity retention (sync / business)      [    90  ] days                         │
└────────────────────────────────────────────────────────────────────────────────┘
```

### 8. PowerShell: summary table and git-style diff

`Get-JIMConfigurationChangeHistory` returns objects (formatted as a table by default) for the summary, and a colourised git-style diff for a single change. Pipeline-friendly.

```
PS> Get-JIMConfigurationChangeHistory -SyncRule "HR Inbound"

Version Operation InitiatedBy        When              Reason                     Summary
------- --------- -----------        ----              ------                     -------
      7 Updated   J. Doe (User)      2026-06-25 14:30  Tighten scope (CHG0098)    Scope +1; flow 'mail' changed
      6 Updated   System (Sync)      2026-06-24 02:14                             flow 'employeeId' added
      5 Updated   prov-api (ApiKey)  2026-06-14 09:31                             2 Attribute Flows changed

PS> Get-JIMSyncRule -Name "HR Inbound" | Get-JIMConfigurationChangeHistory -Version 7 -AsDiff

  Synchronisation Rule "HR Inbound"   (v6 -> v7)
  Updated by J. Doe · 2026-06-25 14:30 · Reason: Tighten scope (CHG0098)

    Scope > Group 1 (All of)
  + Criterion: employeeType Is not "Contractor"
    Attribute Flow > mail
  -   Source: Trim([mail])
  +   Source: Trim(ToLower([mail]))
```

In `-AsDiff`, `+` lines render green and `-` lines red via `$PSStyle` (git-style); headers are dim. `-Raw` instead returns the structured change object for further processing. Secret changes render as `~ Bind password  (secret changed; value hidden)`, never the value.

## Implementation Phases

### Phase 1: Capture foundation, storage, and redaction (generic; SyncRule + Connected System enabled)
1. Migration: add `ConfigurationChangeSnapshot` (jsonb), `ChangeReason` (text), `ConfigurationChangeVersion` (int) to `Activity`.
2. `ConfigurationSnapshotService`: scoped, redacted snapshot per type (SyncRule, Connected System first; generic interface for later types). Keyed-hash redaction for `StringEncrypted` settings.
3. `ChangeTracking.ConfigurationChanges.Enabled` Service Setting (default true) + `ServiceSettingsServer` getter.
4. Thread optional `changeReason` through the config CRUD methods; capture snapshot + version after persist, before `CompleteActivityAsync`.
5. Unit tests: snapshot scope, redaction (no secret material ever serialised), version increment, feature-flag off.

**Files:** `JimDbContext` migration; `JIM.Application/Servers/ConfigurationSnapshotService.cs` (new); `ConnectedSystemServer.cs`; `MetaverseServer.cs` (later types); `ServiceSettingsServer.cs`; `JIM.Models/Activities/Activity.cs`; `JIM.Models/Core/Constants.cs`.

### Phase 2: Retrieval and diff engine (shared backend)
1. `ConfigurationDiffService`: structured diff between two snapshots (stable child-id matching).
2. `ChangeHistoryServer` / `ChangeHistoryRepository`: `GetConfigurationChangeHistoryAsync(targetType, objectId, page, pageSize)` (summary) and single-change/compare detail returning snapshot + diff.
3. Unit tests for the diff engine (add/remove/modify, nested collections, redacted-field change shown without value).

**Files:** `JIM.Application/Servers/ConfigurationDiffService.cs` (new); `ChangeHistoryServer.cs`; `JIM.PostgresData/Repositories/ChangeHistoryRepository.cs`; DTOs under `JIM.Models/.../DTOs/`.

### Phase 3: Per-object Changes tab UI (SyncRule + Connected System)
1. Configuration tree-diff renderer; integrate as the pluggable detail renderer in the `ChangeHistoryTimeline` shell.
2. "Changes" tab on `ConnectedSystemDetail.razor` and `SyncRuleDetail.razor` (deep-link, badge, lazy load, load-more, version list, compare).
3. Comment-on-save dialog wired into the existing save handlers (`Helpers.GetUserAsync` already provides the principal).

**Files:** `JIM.Web/Shared/ConfigurationChangeDiff.razor` (new) + `ChangeHistoryTimeline.razor`; the two detail pages.

### Phase 4: Activities list integration
1. Category quick-filter (All / Configuration / Identity data / Sync runs / System) mapping `ActivityTargetType` groups; initiator-type filter; date-range filter.
2. URL-persisted filter state.
3. Config-change row links to the object's Changes tab at the version, and the Activity detail page renders the same diff.

**Files:** `JIM.Web/Pages/ActivityList.razor`; `ActivityDetail.razor`; application-layer Activity query filters.

### Phase 5: REST API
1. Optional reason on the write request DTOs + delete `?changeReason=`; pass through to the CRUD methods.
2. `GET .../sync-rules/{id}/change-history` and `.../{version}`; same for `connected-systems/{id}`; `PaginatedResponse<ConfigurationChangeHistoryDto>`.
3. Surface snapshot/reason/version on `ActivityDetailDto`. OpenAPI docs.

**Files:** `JIM.Web/Controllers/Api/SynchronisationController.cs`; `JIM.Web/Models/Api/*` DTOs; `ActivityDtos.cs`; API tests under `test/JIM.Web.Api.Tests/`.

### Phase 6: PowerShell
1. `-ChangeReason` on the write cmdlets.
2. `Get-JIMConfigurationChangeHistory` (summary + `-Raw`/`-AsDiff` git-style coloured diff via `$PSStyle`).
3. Pester tests (parameter sets, connection requirement, help, summary vs detail behaviour).

**Files:** `src/JIM.PowerShell/Public/...`; `Tests/`; bump `JIM.psd1` exported functions.

### Phase 7: Retention (type-aware Activity retention)
1. Configuration-change Activity retention Service Setting (separate, longer).
2. Make `PerformChangeHistoryCleanupAsync` target-type-aware for Activity cleanup; keep cleanup recorded.
3. Settings UI for the new retention period and the enable toggle.

**Files:** `JIM.Worker/Worker.cs`; `ChangeHistoryServer.cs` / repository; `ServiceSettingsServer.cs`; `JIM.Web/Pages/Admin/Settings.razor`.

### Phase 8: Rollback (Future)
Out of scope here; designed for. Full snapshots make restore tractable; `ActivityTargetOperationType.Revert` already exists. When delivered, ships with UI, REST API, and a `Restore-JIM...` cmdlet, plus careful handling of references to since-deleted dependencies.

## Design Decisions

- **Q: Store on the Activity, or a parallel configuration-change table?** On the Activity (columns), per the user's preference. Low volume makes this cheap; it makes "all Activity information includes change history" automatic; and it aligns retention with Activity retention. The CSO/MVO relational model is untouched (different volume profile).
- **Q: Full snapshot per version, or stored diff?** Full snapshot per version. Enables compare-any-two and later restore, and lets the diff algorithm improve later without data migration. Storage is negligible at configuration volumes.
- **Q: How to show a secret changed without storing it?** Keyed HMAC of the transiently-decrypted plaintext; never the value, and not brute-forceable from the log. Decryption is transient and the plaintext is discarded.
- **Q: Nested-collection diff stability?** Match child items by persistent DB id; no heuristic matching needed.
- **Q: Reason on DELETE over REST?** Query parameter `?changeReason=`.
- **Q: Git-style colour rendering location?** PowerShell-side (`$PSStyle`) from structured API data; the API stays a data API.

## Success Criteria

Maps to the PRD acceptance criteria. In brief: configuration create/update/delete records a redacted, versioned snapshot on its Activity; SyncRule and Connected System have a Changes tab with tree diff and compare; no secret material is ever stored or rendered; an optional reason is supported on every surface; the Activities list has the configuration category/initiator/date filters with URL persistence; full REST API and PowerShell parity (reason on write, summary + single-change `-Raw`/`-AsDiff` retrieval, full Activity retrieval); history is immutable (no update/delete) and cleaned only by target-type-aware housekeeping; Pester and API tests cover the new surfaces.

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| A secret leaks into a snapshot | High (security) | Redaction keyed off `StringEncrypted`; keyed-hash only; explicit unit test asserting no plaintext/ciphertext present; exclude `PersistedConnectorData`. |
| Naive serialisation pulls cycles / huge collections | High | Purpose-built per-type projection; exclude backlinks, `Objects`, `PendingExports`. |
| Method-signature sprawl from threading the reason | Medium | Optional trailing parameter first; `ChangeContext` value object if it spreads. |
| Snapshot column bloats the high-volume `Activity` table | Medium | Column is null for non-config activities (the common path); jsonb null is cheap. |
| Diff noise from unstable child ordering | Medium | Match by DB id, not ordinal. |
| Two change-history mechanisms confuse maintainers | Low | Documented divergence by volume profile; shared timeline shell keeps the UX consistent. |

## Dependencies

- Builds on #269 (CSO/MVO change history), delivered.
- No new external or third-party dependencies (`System.Text.Json`, PowerShell 7 `$PSStyle`, existing `CredentialProtectionService`).

## Open Questions

1. Default retention period for configuration-change Activities (proposal: notably longer than the 90-day identity default, possibly effectively indefinite given low volume).
2. Order of remaining configuration types after Synchronisation Rule and Connected System.
3. Confirm the keyed-hash redaction approach for secret-change detection versus the simpler "value not tracked" sentinel (recommended: keyed hash).
