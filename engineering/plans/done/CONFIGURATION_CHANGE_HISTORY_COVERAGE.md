# Configuration Change History Coverage - Implementation Plan

- **Status:** Done
- **Note:** All ten phases complete. Post-close-out (2026-07-06): two seeded-baseline gaps were closed after review flagged the affected types missing from the System Initialisation children list. (1) The built-in Metaverse Object Types and Attributes (Phase 3) were seeded without Create Activities or version-1 baselines; `MetaverseServer.RecordSeededMetaverseObjectTypeBaselineAsync` / `RecordSeededMetaverseAttributeBaselineAsync` now record a System-attributed Create + v1 baseline for each after the seed batch. (2) The built-in Service Settings (Phase 2) were likewise missing: their seed/drift path (`SyncServiceSettingsAsync` -> `CreateOrUpdateSettingAsync`) recorded no Activity at all (Phase 2 only wired the admin edit/revert paths); `ServiceSettingsServer.RecordSeededServiceSettingBaselineAsync` now records a baseline for each setting newly created in the sync pass, recorded after the full sync loop so the toggle and hash-key settings the capture depends on already exist (no bootstrap-ordering issue; the encrypted SSO.Secret baseline redacts correctly). Together these take the System Initialisation children from 24 to 136 (2 Object Types + 86 Attributes + 24 Service Settings). Both mirror the Predefined Search / Connector Definition post-batch pattern, are runtime-verified on a fresh seed, and are idempotent on restart. (3) Two startup drift-sync paths mutated configuration repository-direct without capture: the rendering-hint upgrade migration (`SyncBuiltInAttributeRenderingHintsAsync`) and the environment-sourced read-only Service Setting refresh (`CreateOrUpdateSettingAsync`); both now record System-attributed Updates under the seeding parent (`RecordSeededMetaverseAttributeUpdateAsync`; `RecordSeededServiceSettingUpdateIfChangedAsync`, which is churn-free via a snapshot pre-check so an unchanged restart records nothing). (4) The cross-type factory-reset baseline gap is now closed: `SeedingServer.RebaselineBuiltInConfigurationAsync` re-records a v1 baseline for every preserved built-in object (Metaverse schema, Predefined Searches, Connector Definitions, Example Data, Roles via the new `SecurityServer.RecordSeededRoleBaselineAsync`, Service Settings) after the reset wipe, under a fresh System Initialisation parent (Schedules are handled by the existing `SeedBuiltInSchedulesAsync` re-seed); runtime-verified against a real factory reset (137 children restored) and idempotent on a post-reset restart. Remaining follow-up: #916's proposal to replace the bespoke post-reset repairs with a full re-run of the seeding pipeline is still worthwhile as a simplification, but the provenance loss it was tracking is now fixed.
- **Issue:** [#14](https://github.com/TetronIO/JIM/issues/14) *(sub-task of the parent change-history issue)*
- **PRD:** [`engineering/prd/PRD_CONFIGURATION_CHANGE_HISTORY_COVERAGE.md`](../../prd/PRD_CONFIGURATION_CHANGE_HISTORY_COVERAGE.md)
- **Note (2026-07-05):** Phase 1 verification disproved the presumed Schedule step capture gap: there are no step REST endpoints (`Add-JIMScheduleStep` performs a whole-Schedule PUT), and both step-mutation surfaces (the editor dialog and the REST update endpoint) reconcile steps and then call the audited `UpdateScheduleAsync`, which captures the step changes in exactly one version per save. Making the bare step methods capture unconditionally would have double-recorded on every editor/REST save, so Phase 1 instead documented the caller contract on those methods. The durable fix (consolidating step reconciliation into `UpdateScheduleAsync` and making the bare methods private, which also removes the duplicated reconcile logic in the dialog and controller) is proposed as a follow-up slice.

## Overview

Extend the delivered Configuration Change History capability (versioned, redaction-aware snapshots carried on the change's `Activity`, delivered for Connected System, Synchronisation Rule, and Schedule) to **every remaining admin-mutable configuration object type**: Service Setting, Metaverse Attribute, Metaverse Object Type, Trusted Certificate, API Key, Role (definitions and assignments), Predefined Search, Connector Definition, and Example Data Template/Set, plus the Schedule step direct-CRUD gap.

The storage model, diff engine, retrieval/compare pipeline, UI diff renderer, retention, and reason capture are all type-agnostic and are reused as-is. The per-type work is: a snapshot builder, capture wiring in the owning server (with Activity plumbing first for the Tier 2 types that record nothing today), a retrieval routing case, REST/PowerShell/UI surfaces, and tests. Two structural fixes ride along: API Key mutations move out of the Blazor page into the application layer (restoring N-tier), and the silent Metaverse Object Type update path gains its missing Activity.

This is the *how*; scope and acceptance criteria live in the PRD.

## Business Value

Closes the two-tier audit posture: today an auditor can reconstruct what changed for 3 of ~13 configuration types; for the rest they get either a bare "something changed" Activity or, for security-critical objects (API Keys, Role assignments), nothing at all. Full coverage makes "who changed this, when, and what was it before" answerable for every administrative action, which is the compliance bar JIM's high-trust deployments expect.

## Current State Analysis

### Coverage audit (2026-07-05)

| Type | Key | Activity today | Snapshot today | Mutation paths (file:line) | UI surface |
|------|-----|----------------|----------------|---------------------------|------------|
| Connected System | int | ✅ | ✅ | `ConnectedSystemServer.cs:343/437/1053` | Detail page + Changes tab |
| Synchronisation Rule | int | ✅ | ✅ | `ConnectedSystemServer.cs` (create/update 4512, delete 4668) | Detail page + Changes tab |
| Schedule (whole) | Guid | ✅ | ✅ | `SchedulerServer.cs:62/79/93` | Editor dialog + History tab |
| Schedule step (direct CRUD) | - | ⚠️ via owning save | ⚠️ via owning save | `SchedulerServer.cs` step methods are bare, but both callers reconcile then call the audited `UpdateScheduleAsync` (captures once per save) | Schedule editor |
| Service Setting | **string** (`Key`) | ✅ (update 271/284, revert 296/308 in `ServiceSettingsServer.cs`) | ❌ | as left | `Settings.razor` list + `EditSettingDialog` |
| Metaverse Attribute | int | ✅ (6 methods, `MetaverseServer.cs:170-307`) | ❌ | as left | Read-only lists only (no edit UI; #377 will add it) |
| Metaverse Object Type | int | ⚠️ create only; **update at `MetaverseServer.cs:118` records no Activity**; no delete method exists | ❌ | as left | `MetaverseObjectTypeDetail.razor` (deletion-rules save at :372) |
| Trusted Certificate | Guid | ✅ (`CertificateServer.cs:53/108/168/221`) | ❌ | as left | `CertificateList.razor` list + inline dialogs |
| API Key | Guid | ❌ | ❌ | **No application server**; `ApiKeyList.razor:410/479/509` and `ApiKeysController.cs` call `Repository.ApiKeys.*` directly | List + inline dialogs; `ApiKeyDetail.razor` exists (flat, no tabs) |
| Role definition | int | ❌ | ❌ | No mutation path exists (seed-only until #612) | None |
| Role assignment | int (Role) | ❌ | ❌ | `SecurityServer.cs:53/58/63` pass-through; first-admin path `AuthServer.cs:75/148` | None |
| Predefined Search | int | ❌ | ❌ | `SearchServer.cs:74` (update) + criteria/groups :98-148; no root create/delete (seeded) | `PredefinedSearchList.razor` (save :172) + `PredefinedSearchDetail.razor` |
| Connector Definition | int | ✅ | ✅ | `ConnectedSystemServer.cs` connector-definition methods (create/update/delete + files) | Detail page + Changes tab |
| Example Data Template / Set | int | ✅ | ✅ | `ExampleDataServer.cs` Set + Template create/update/delete | Detail pages + Changes tab |

### What exists (reuse, not rebuild)

| Component | Notes |
|-----------|-------|
| `ConfigurationSnapshotService` (`JIM.Application/Services/`) | Three builders today (`CreateSnapshot` for SyncRule :69, ConnectedSystem :211, Schedule :406). Add one builder per new type; `Serialise`/`Deserialise` shared. |
| `ConfigurationDiffService` | Fully type-agnostic (stable-id child matching, `Summarise`). No changes. |
| `ChangeHistoryServer` retrieval | `GetConfigurationChangeHistoryAsync` / `GetConfigurationChangeAsync` / `CompareConfigurationChangesAsync` in int/Guid overload pairs; type-agnostic. Needs a string-keyed overload set for Service Settings only. |
| `ActivitiesRepository.ConfigurationChangeQuery` (:471 int, :493 Guid) | The **only** place type routes to an Activity FK column. One `case` per new type. |
| `ActivityServer` version helpers | `GetNextConfigurationChangeVersionAsync` (int :84 / Guid :94) + latest-snapshot getters. Add string-keyed siblings. |
| Capture guard | The toggle check + semantic no-change dedupe + version allocation is **hand-rolled per type** (`ConnectedSystemServer.cs:47`, `:102`, `SchedulerServer.cs:120`). Three copies today; twelve would be unmaintainable, so Phase 1 centralises it (see Architecture). |
| `ConfigurationChangesTab.razor` | Takes `ActivityTargetType` + `int ObjectId` or `Guid? ObjectGuidId`. Add a string key parameter for Service Settings. |
| `ChangeReasonDialog.razor` | Shared prompt; 5 call sites today. Reuse verbatim. |
| REST pattern | Three GET routes per type (list / `{changeVersion:int}` / `compare`) on the owning controller; controllers already exist for every new type (`ServiceSettingsController`, `CertificatesController`, `ApiKeysController`, `PredefinedSearchesController`, `MetaverseController`, `ExampleDataController`). `ChangeReason` as `string?` on write DTOs. |
| PowerShell | `Get-JIMConfigurationChangeHistory` with `[ValidateSet('SynchronisationRule','ConnectedSystem','Schedule')]` and per-type endpoint mapping; write cmdlets exist for most new types but lack `-ChangeReason`. |
| Test patterns | `test/JIM.Worker.Tests/Servers/ConfigurationChangeCaptureCoverageTests.cs` (int-keyed, Moq'd repos, activity captured via callback) and `ScheduleConfigurationChangeCaptureTests.cs` (Guid-keyed, tombstone, toggle-off). Mirror per type. |

### What is missing (beyond builders)

- `ActivityTargetType` has **no members** for ApiKey, Role, PredefinedSearch, ConnectorDefinition, or ExampleDataSet (`ExampleDataTemplate = 1` exists). New members needed plus `ActivityTargetTypeCategories` classification as Configuration.
- `Activity` has **no FK column** for any new type except `ExampleDataTemplateId` (:225). New nullable columns + one migration.
- `ExampleDataSet` does not implement `IAuditable` (no Created/LastUpdated stamps); `ExampleDataTemplate` does.
- No application-layer server exists for API Keys at all.

## Technical Architecture

### 1. Centralised capture helper (Phase 1 refactor)

Extract the per-type copy-paste (`CaptureConfigurationChangeAsync`) into one shared component, e.g. `ConfigurationChangeCapture` in `JIM.Application/Services`, owning:

1. `ChangeTracking.ConfigurationChanges.Enabled` toggle check (skip early, before building the snapshot);
2. semantic no-change dedupe (deserialise latest stored snapshot, `ConfigurationDiffs.Diff(...).HasChanges`);
3. version allocation via the `ActivityServer` next-version helpers (keyed int/Guid/string);
4. assignment of `ConfigurationChangeSnapshot`/`ConfigurationChangeVersion`/`ChangeReason` onto the in-flight `Activity`;
5. best-effort semantics (a capture failure logs and never rolls back the mutation);
6. a deletion-tombstone variant (snapshot without version increment, matching the Synchronisation Rule and Schedule delete behaviour).

Per-type server code then reduces to: build the snapshot, set the Activity FK, call the helper. The three existing capture paths (Connected System, Synchronisation Rule, Schedule) are refactored onto the helper with **no behaviour change**, protected by the existing capture test fixtures.

### 2. Enum, FK columns, and routing (Phase 1 plumbing)

- **`ActivityTargetType`**: add `ApiKey`, `Role`, `PredefinedSearch`, `ConnectorDefinition`, `ExampleDataSet`; classify all new members (and audit the existing config members) as Configuration in `ActivityTargetTypeCategories`.
- **`Activity` FK columns** (all nullable; one migration): `ServiceSettingKey` (string, MaxLength 100), `MetaverseAttributeId`, `MetaverseObjectTypeId`, `PredefinedSearchId`, `RoleId`, `ConnectorDefinitionId`, `ExampleDataSetId` (int), `TrustedCertificateId`, `ApiKeyId` (Guid). Reuse the existing `ExampleDataTemplateId`. Configure each in `JimDbContext` to match `SyncRuleId`'s delete semantics exactly (that configuration already keeps tombstone history retrievable after the target row is deleted; replicate, do not reinvent).
- **`ActivitiesRepository.ConfigurationChangeQuery`**: add the `case` per type to the int and Guid overloads, plus a new string-keyed overload trio (query, count, page, by-version, before-version, max-version, latest-snapshot) for Service Settings. `ChangeHistoryServer` and `ActivityServer` gain matching string-keyed overloads; the existing int/Guid overloads serve everything else unchanged.
- **Granular child edits follow the established precedent**: a sub-entity mutation (Predefined Search criterion, Schedule step) records its own or its parent's `TargetType` but always sets the **parent's** FK, so all versions of a parent roll up into one history (see the membership comment at `ActivitiesRepository.cs:473`).

### 3. Snapshot builders (scope per type)

All builders are deliberately scoped projections (never naive EF serialisation), added to `ConfigurationSnapshotService`:

| Type | Snapshot contents | Redaction / exclusions |
|------|-------------------|------------------------|
| Service Setting | Key, display name, category, value type, current value, default value, override-vs-default flag | `StringEncrypted` values stored as the established keyed HMAC only (both current and default); never plaintext or ciphertext |
| Metaverse Attribute | Name, type, plurality, built-in flag, object-type associations (ids + names) | - |
| Metaverse Object Type | Name, built-in flag, attribute associations (ids + names), deletion-rule and grace-period configuration | Exclude object instances |
| Trusted Certificate | Name, subject, thumbprint, validity window, enabled flag, notes, file path/size | **Never** `CertificateData` (key material) |
| API Key | Name, enabled flag, expiry, role assignments (ids + names), audit stamps | **The secret is never stored in any form, not even hashed** (it is shown once at creation and hashed at rest; history records only metadata) |
| Role | Name, built-in flag, definition fields, member list (MVO ids + display names) | Membership changes snapshot the owning Role |
| Predefined Search | Name, Uri, object type, attribute selections; criteria groups and criteria as nested children (stable DB ids) | Mirrors Synchronisation Rule scoping-criteria roll-up |
| Connector Definition | Name, description, capabilities, file list as name/size/SHA-256 hash | **Never** file binary content |
| Example Data Template / Set | Template: name, configuration, referenced data sets. Set: name, values summary (count), metadata | Large value collections summarised, not embedded |

### 4. Per-type surfaces

- **REST**: the three GET routes (`.../change-history`, `.../change-history/{changeVersion:int}`, `.../change-history/compare`) on each type's existing controller, keyed to match the entity (int route param, `{id:guid}`, or `{key}` for Service Settings). `string? ChangeReason` added to each type's write DTOs; delete endpoints take `?changeReason=` per the established convention.
- **PowerShell**: extend the `-Type` `ValidateSet` in `Get-JIMConfigurationChangeHistory` (`ServiceSetting`, `MetaverseAttribute`, `MetaverseObjectType`, `TrustedCertificate`, `ApiKey`, `Role`, `PredefinedSearch`, `ConnectorDefinition`, `ExampleDataTemplate`, `ExampleDataSet`) with per-type key-shape validation (`-Id` already a string; Guid-parse for certificate/API key, verbatim key string for Service Setting) and endpoint mapping. Add `-ChangeReason` to the existing write cmdlets (`Set-JIMServiceSetting`, `Set-JIMCertificate`, `New-JIMApiKey`/`Set-JIMApiKey`, `Set-JIMPredefinedSearch` and criteria variants, the Metaverse `Set-*` cmdlets, `Set-JIMExampleDataSet`).
- **UI** (per PRD decision 2):
  - **Changes tab where a detail page exists**: `MetaverseObjectTypeDetail`, `ApiKeyDetail`, `PredefinedSearchDetail`, `ConnectorDetail`, `ExampleDataTemplateDetail`, `ExampleDataSetDetail`. Pages that are currently flat (API Key, Metaverse Object Type) are converted to `NavigableMudTabs` (`?t=` deep link) following `ConnectedSystemDetail`.
  - **Per-row history affordance on list-plus-dialog pages**: Service Settings (`Settings.razor`) and Trusted Certificates (`CertificateList.razor`), plus the read-only Metaverse Attribute lists. A new shared `ConfigurationChangeHistoryDialog` (a `MudDialog` hosting `ConfigurationChangesTab`, static `ShowAsync` like `ChangeReasonDialog`) opened from a history icon button per row; follows the Schedule editor History-tab precedent for dialog-hosted history.
  - **Reason prompts**: `ChangeReasonDialog.ShowAsync` wired into every newly covered UI create/update/delete path (Settings edit + revert, Metaverse Object Type deletion-rules save, Certificate upload/add/edit/delete, API Key create/edit/delete, Predefined Search main saves). Granular Predefined Search criteria/group micro-edits capture snapshots but stay promptless to avoid dialog fatigue (same reasoning as the intentionally promptless Schedules toggle); the reason remains available via `-ChangeReason`/REST.

### 5. UI mockups

Per-row history affordance on a list-plus-dialog page (Service Settings shown; Certificates identical):

```
Service Settings
┌──────────────────────────────────────────────────────────────────────────────┐
│ History                                                                      │
│   History.RetentionPeriod          90 days        (override)   [ ✎ ] [ 🕘 ] │
│   History.ConfigurationChange…     3650 days      (default)    [ ✎ ] [ 🕘 ] │
└──────────────────────────────────────────────────────────────────────────────┘
        🕘 opens:
┌─ Change history · History.RetentionPeriod ──────────────────────────────────┐
│  ● v3 · Updated   👤 A. Mehta    yesterday    “Reduce per CHG0102”          │
│  │    Value: 90.00:00:00 → 30.00:00:00        [ View diff ] [ Compare ]     │
│  ● v2 · Reverted  👤 J. Doe      12 Jun                                      │
│  ⋮                                                    [ Load more ]          │
└──────────────────────────────────────────────────────────────────────────────┘
```

API Key detail page converted to tabs with a Changes tab:

```
API Key: prov-api
┌──────────────────────────────────────────────────────────────────┐
│ Details │ Activity │ Changes (4)                                 │
├──────────────────────────────────────────────────────────────────┤
│  ● v4 · Updated  👤 A. Mehta   3h ago   “Narrow to read-only”    │
│  │    Roles: - Administrator  + Reader                           │
│  ● v3 · Updated  …                                               │
└──────────────────────────────────────────────────────────────────┘
```

## Implementation Phases

Each phase is a shippable vertical slice (TDD per capture path, docs and changelog in the same PR). Order follows the PRD's tiering: Tier 1 first, then the security-critical Tier 2 items, then the remainder.

### Phase 1: Capture foundation refactor and shared plumbing ✅
1. Extract the shared capture service (toggle, dedupe, version, assignment, best-effort, tombstone variant); refactor the five existing capture paths onto it, existing fixtures green throughout. ✅ *(Delivered as `ConfigurationChangeCaptureService`; `Activity.SetConfigurationTargetId` owns the target-type-to-column mapping, mirrored by the repository's `ConfigurationChangeQuery`.)*
2. New `ActivityTargetType` members + `ActivityTargetTypeCategories` classification. ✅ *(`ApiKey`, `Role`, `PredefinedSearch`, `ConnectorDefinition`, `ExampleDataSet`; all Configuration. `ExampleDataTemplate` stays under System until Phase 9, because its existing activities are generation runs.)*
3. `Activity` target columns (+ `ServiceSettingKey` string) and single migration. ✅ *(Plain scalar columns following the `ScheduleId` precedent: no FK constraint or navigation, so tombstone history survives target deletion; the older `SyncRuleId`-style constrained FK was deliberately not replicated.)*
4. `ConfigurationChangeQuery` cases for all new types; string-keyed overload set through `ActivitiesRepository` → `ActivityServer` → `ChangeHistoryServer`. ✅
5. ~~Close the Schedule step gap~~ **Disproved during implementation** (see the Note in the header): every existing step mutation path is already captured exactly once via the audited whole-Schedule update, and there are no step-level REST endpoints. Unconditional capture in the bare step methods would have double-recorded. Delivered instead: the caller contract is documented on the three step methods; consolidating step reconciliation into `UpdateScheduleAsync` (removing the duplicated reconcile logic in the editor dialog and the REST controller, and making the bare methods private) is the durable fix, proposed as a follow-up slice.

**Files:** `JIM.Application/Services/ConfigurationChangeCaptureService.cs` (new); `ConnectedSystemServer.cs`; `SchedulerServer.cs`; `ActivityServer.cs`; `ChangeHistoryServer.cs`; `JIM.Models/Activities/*`; `IActivityRepository.cs`; `ActivitiesRepository.cs`; migration `AddConfigurationChangeTargetColumnsToActivity`; model and retrieval tests.

### Phase 2: Service Settings (Tier 1) ✅
1. Snapshot builder with keyed-HMAC redaction for `StringEncrypted` values and override-vs-default representation. ✅ *(Snapshot carries key, name, category, value type, value, default value and the overridden flag; a revert diffs as the override value being removed and the flag flipping. `ConfigurationSnapshot` gained a nullable `ObjectKey` for string-keyed objects.)*
2. Capture in `UpdateSettingValueAsync` (both principal overloads) and `RevertSettingToDefaultAsync`. ✅ *(Via the shared capture service's new string-keyed overload; optional `changeReason` threaded through all four mutation methods.)*
3. REST routes on `ServiceSettingsController` (`{key}`-keyed) + `ChangeReason` on its write DTO and `?changeReason=` on the revert; `-ChangeReason` on `Set-JIMServiceSetting` and `Reset-JIMServiceSetting`; `Get-JIMConfigurationChangeHistory -Type ServiceSetting`. ✅ *(Routes validated at app startup via OpenAPI generation.)*
4. UI: shared `ConfigurationChangeHistoryDialog` (new, hosts `ConfigurationChangesTab`, which gained an `ObjectKey` string path) + per-row history button on `Settings.razor`; `ChangeReasonDialog` on edit and revert. ✅
5. Tests: capture, redaction (no plaintext/ciphertext ever serialised; keyed hash asserted via the deserialised node), rotation detection, dedupe, toggle-off, revert semantics, API endpoint tests, Pester coverage. ✅

### Phase 3: Metaverse Object Types and Attributes (Tier 1) ✅
1. **Fix the silent update**: `UpdateMetaverseObjectTypeAsync` gains principal-attributed overloads recording an Activity (failing test first; it currently records nothing). ✅ *(The unaudited single-argument overload was removed entirely, so a silent update path can no longer be called; both callers, the REST endpoint and the deletion-rules page, now attribute to the current user or API key.)*
2. Snapshot builders (object type incl. attribute associations and deletion/grace-period config; attribute incl. object-type associations); capture across all `MetaverseServer` mutators; attribute delete records a tombstone. ✅ *(Associations are captured as id-valued reference scalars with the name as display form, so binding changes diff cleanly by `ItemId`; capture lambdas reload the persisted entity with its associations before snapshotting.)*
3. REST routes on `MetaverseController` (object types and attributes); `-ChangeReason` on the Metaverse `Set-*` cmdlets; both `-Type` values. ✅ *(`ChangeReason` also added to the create request DTOs, `?changeReason=` on the attribute delete, and `-ChangeReason` on `New-JIMMetaverseObjectType`, `New-JIMMetaverseAttribute` and `Remove-JIMMetaverseAttribute`; routes validated at app startup via OpenAPI generation. `docs/powershell/history.md` also caught up with the Phase 2 `ServiceSetting` type, which it had missed.)*
4. UI: `MetaverseObjectTypeDetail` converted to `NavigableMudTabs` with a Changes tab; per-row history on the attribute lists (`SchemaObjectTypeList`, object-type detail attribute table); reason prompt on the deletion-rules save. ✅ *(Tabs: Details, incl. deletion rules; Attributes; Changes with a count badge.)*
5. Coordinate with #377 (attribute CRUD UI): whichever lands second wires its paths into the other's capture/prompt pattern. *(Still open on #377's side; all application-layer attribute mutators now capture, so a future attribute CRUD UI only needs the reason prompt.)*

### Phase 4: Trusted Certificates (Tier 1) ✅
1. Metadata-only snapshot builder (never key material); capture in the four `CertificateServer` mutators; delete tombstone. ✅ *(The four mutators were refactored onto shared private cores taking initiator delegates, gaining ApiKey overloads in the process; previously every REST certificate mutation was attributed to System because the controller never resolved a principal. Redaction asserted by test: the DER bytes' Base64 never appears in a stored snapshot.)*
2. REST routes on `CertificatesController` (Guid) + `ChangeReason` on writes; `-ChangeReason` on `Set-JIMCertificate`; `-Type TrustedCertificate`. ✅ *(`CertificatesController` moved onto `ApiControllerBase` so mutations attribute to the calling user or API key; `-ChangeReason` also added to `Add-JIMCertificate` and `Remove-JIMCertificate`.)*
3. UI: per-row history button on `CertificateList.razor`; reason prompts on upload, add-from-path, edit, and delete. ✅ *(History surfaced as a Change History action in the row menu, opening the shared `ConfigurationChangeHistoryDialog`.)*

### Interim slice: N-tier enforcement (added 2026-07-05) ✅

Added after Phase 4, prompted by the Phase 5 finding that API key CRUD bypassed the application layer entirely: a sweep found 15 JIM.Web files (~35 call sites) plus 3 Worker/Scheduler sites calling `Repository.*` directly, which is how unaudited mutations happen.

1. All API key data access relocated onto `SecurityServer` as plain pass-throughs (no capture yet; that is Phase 5), including the middleware authentication reads and the bootstrap key creation. ✅
2. Read pass-through methods added to the owning servers (Metaverse, ConnectedSystems, Scheduler, Activities, Tasking, ChangeHistory) for every remaining JIM.Web bypass; all call sites switched. ✅
3. Worker/Scheduler: switched to existing or new facade methods, including `UpdateScheduleRunTimesAsync` (deliberately unaudited run-time bookkeeping, now a named contract instead of a raw repository write). ✅
4. **Enforcement**: `JimApplication.Repository` is now `internal`, so any future bypass is a compile error. The sync engine's raw-SQL hot path gets a deliberate, documented door instead: `IRepository.Sync` (`ISyncRepository`) exposed as `JimApplication.SyncRepository`, replacing four concrete-type casts. The compiler immediately proved its worth by catching two Worker call sites the grep sweeps had missed (they passed `jim.Repository` as a constructor argument, with no trailing `.`). ✅

Pure relocation throughout: no behaviour change, REST contracts unchanged, existing test suite green. Phase 5 now builds capture onto an already-clean `SecurityServer` surface.

### Phase 5: API Keys (Tier 2, security-critical) ✅
1. **N-tier fix first**: API key operations move onto `SecurityServer` (decided 2026-07-05: API keys are security principals carrying Role assignments, and Phase 6 routes Role capture through `SecurityServer` anyway, so both security-principal types share one audited home; a separate `ApiKeyServer` was considered and rejected). `ApiKeyList.razor`, `ApiKeyDetail.razor`, and `ApiKeysController` refactored onto it with unchanged REST contracts. ✅ (delivered by the interim N-tier enforcement slice)
2. Activity plumbing (`ActivityTargetType.ApiKey`, `ApiKeyId` FK) + snapshot capture; the secret never appears in any snapshot in any form (explicit test); delete tombstone. ✅
3. REST routes + `ChangeReason` on write DTOs; `-ChangeReason` on `New-JIMApiKey`/`Set-JIMApiKey`/`Remove-JIMApiKey`; `-Type ApiKey` (Guid-keyed). ✅
4. UI: `ApiKeyDetail` converted to `NavigableMudTabs` (Details / Activity / Changes); reason prompts on the list page's create/edit/delete dialogs. ✅

Delivery notes (2026-07-05): the snapshot stores metadata plus Role assignments as reference nodes (ItemId-matched, so re-binding diffs but renaming a Role does not); `KeyHash` is excluded in every form (not even a keyed hash, unlike Connected System credentials, since a key is replaced rather than edited; asserted by test) and `LastUsedAt`/`LastUsedFromIp` are excluded as operational state so authentication traffic cannot churn the dedupe. `RecordApiKeyUsageAsync` deliberately records no Activity. The null-initiator path (JIM.Web bootstrap infrastructure key) falls back to a System-attributed Activity because `ActivityServer.CreateActivityAsync(activity, (MetaverseObject?)null)` would otherwise fail validation; that doc/implementation mismatch in `ActivityServer` is worth a central follow-up. Two adjacent bugs fixed in passing: `ApiKeyRepository.UpdateAsync` and `TrustedCertificateRepository.UpdateAsync` re-load the tracked row and copied only functional fields, silently dropping the `LastUpdated*` audit stamp under JIM.Web's NoTracking default; both now copy the audit fields.

### Phase 6: Roles (Tier 2, security-critical) ✅
1. Role snapshot builder (definition fields + member list); `RoleId` FK routing. ✅
2. Assignment capture: `SecurityServer.AddObjectToRoleAsync`/`AddObjectToRoleByIdAsync`/`RemoveObjectFromRoleAsync` gain principal-attributed overloads recording an Activity (Role target) and a snapshot of the Role's post-change membership; the automatic first-admin assignment (`AuthServer`) captures as System-initiated. ✅
3. Definition capture path built and tested now (per PRD decision 1) so #612's admin-editable roles only add mutators onto an existing pattern; seed-time role creation captures a System-initiated v1 baseline. ✅
4. REST retrieval routes (`SecurityController`); `-Type Role`. No role management UI exists, so history surfaces via the Activity list and the retrieval APIs until #612 delivers one. ✅

Delivery notes (2026-07-06): `SecurityServer.CreateRoleAsync` added (with a new `ISecurityRepository.CreateRoleAsync`); the built-in Administrator Role's seeding relocated from `SeedAsync`'s repository-direct batch to a dedicated `SeedBuiltInRolesAsync` step creating through the audited path (System-attributed v1 baseline, mirroring the Temporal Scope Reconciliation Schedule precedent); the now-dead `roles` parameter was removed from `SeedDataAsync`. Member snapshot nodes are ItemGuidId-matched references, so renaming a member does not register in the Role's history. Membership changes are now attributed to the actual REST caller (previously anonymous); `AuthServer`'s untouched call sites become System-attributed via the null-initiator fallback, which means the initial-admin bootstrap records both its pre-existing MetaverseObject-targeted provisioning Activity and the new Role-targeted configuration change Activity (intentional: different targets, different audiences). The Role's `LastUpdated` audit stamp is deliberately not set on membership changes: the membership repository methods are id-keyed and load their own tracked Role, so a stamp applied to a separately-loaded instance has no reliable persistence path; revisit with #612's definition mutators.

### Interim slice: seeding Activity grouping (added 2026-07-06) ✅

Added after Phase 6, prompted by a user sanity check: audited seed-time creates (built-in Schedule, Administrator Role, and ~15 more coming in Phases 8-9) were each surfacing as a top-level System Activity, cluttering a fresh deployment's Activity list. Decision: group, don't suppress; the version-1 baselines and provenance are load-bearing (dropping them would reintroduce the misattributed-origin bug fixed for the Temporal Scope Reconciliation Schedule) and factory-state provenance is an audit feature in JIM's target market.

1. New `ActivityTargetType.SystemInitialisation` (category: System); `SeedingServer` lazily creates one System-attributed parent Activity per seeding pass that actually creates something, and every audited seed create becomes its child via `ParentActivityId`. A restart where seeding no-ops records nothing. ✅
2. `JimApplication.InitialiseDatabaseAsync` and the factory reset's reseed path (`SystemServer.ResetSystemAsync`) both complete the parent on success and fail it on error, so it can never sit permanently InProgress (which would also block subsequent resets via the in-progress guard). ✅
3. Out of scope by decision: the infrastructure API Key creation stays a standalone Activity (operator-triggered via environment variable, security-notable), and AuthServer's first-admin activities are unchanged. ✅

**Requirement for every phase that adds a configuration type, not only Phases 8-9: if the type is seeded, its built-in objects MUST appear as System Initialisation children** (a System-attributed Create Activity plus a version-1 baseline snapshot, grouped via `SeedingServer.GetOrCreateSeedingActivityAsync`). Two shapes:
- Types seeded individually pass the parent id through their server's audited create path (`SeedBuiltInSchedulesAsync`, `SeedBuiltInRolesAsync`).
- Types seeded in a shared cross-referencing batch (where re-routing each object through an individual audited create would break the reference resolution) record the baseline *after* the batch persists, over the "created this pass" list (`SearchServer.RecordSeededPredefinedSearchBaselineAsync`, Phase 7).

This was originally written as "Phases 8-9" and was consequently missed for Phase 7's Predefined Searches (which are seeded but were not in that list); the gap was caught in review and fixed. Do not re-scope this to a subset of phases: every seeded configuration type gets a baseline child, or the omission is called out explicitly with a reason. **Known still-open by decision:** seeded Metaverse Object Types and Attributes (Phase 3) have this same gap and were scoped out; close them if/when that scope is picked up.

### Phase 7: Predefined Searches ✅
1. Snapshot builder with criteria groups/criteria as nested children; every `SearchServer` mutator (root update + the six criteria/group methods) sets the parent `PredefinedSearchId` FK and captures the owning search's snapshot (granular roll-up precedent). ✅
2. Activity plumbing (`ActivityTargetType.PredefinedSearch`) with principal-attributed overloads. ✅
3. REST routes on `PredefinedSearchesController` + `ChangeReason` on writes; `-ChangeReason` on `Set-JIMPredefinedSearch` and criteria variants; `-Type PredefinedSearch`. ✅
4. UI: Changes tab on `PredefinedSearchDetail` (a detail page exists, so the tab wins over per-row); reason prompt on the main saves; criteria micro-edits promptless by design. ✅

Delivery notes (2026-07-06): the owning-search FK a granular criteria/group edit rolls up to is resolved by walking the parent-group chain (`GetOwningPredefinedSearchIdFor{Group,Criterion}Async`), reading the `PredefinedSearchId`/`ParentGroupId`/`PredefinedSearchCriteriaGroupId` FK scalars (newly exposed on the two criteria models; they map onto pre-existing shadow-FK columns, so no migration). Delete paths resolve the owning id before the row is removed. A not-found group/criterion skips Activity creation entirely (pass-through to the repository's null/false result), so a missing target does not spam the Activity list. The parent-chain walk is proven against real PostgreSQL (`PredefinedSearchCriteriaDatabaseTests`), since the shadow-FK mapping cannot be reproduced on the EF in-memory provider. The only UI mutation path that today calls `UpdatePredefinedSearchAsync` is the enable/disable toggle, so that is where the detail and list pages gained the reason prompt (which also fixed their previously-absent principal attribution); the criteria editor stays promptless by design. All seven write cmdlets gained `-ChangeReason` (not just the two named in the plan). The plan originally overlooked that the six built-in Predefined Searches are seeded too (like the Administrator Role and Temporal Scope Reconciliation Schedule), so they gained no System Initialisation child; this was caught in review and fixed: each newly-seeded built-in search now records a System-attributed Create Activity and version-1 baseline under the seeding parent. Because the built-in searches are persisted in one cross-referencing repository batch (sharing the just-seeded Object Types and Attributes), the baseline is recorded after the batch (`SearchServer.RecordSeededPredefinedSearchBaselineAsync`) rather than by re-routing the batch through individual audited creates, which would have risked the reference resolution; idempotency is via the "created this pass" list, so it is safe with change tracking off and does not re-baseline on restart (both proven at runtime on a fresh-seeded stack). The identical gap for seeded Metaverse Object Types and Attributes (Phase 3) remains open by decision (scoped out of this branch).

### Phase 8: Connector Definitions ✅
1. Snapshot builder (metadata + capabilities + files as name/size/SHA-256); Activity plumbing on the five `ConnectedSystemServer` connector-definition methods, System-attributed where no principal exists (seeding). ✅
2. REST retrieval routes; `-Type ConnectorDefinition`. No mutation UI exists yet; capture protects the API/seed paths and any future upload UI. ✅
3. UI: Changes tab on `ConnectorDetail`. ✅

Delivery notes (2026-07-06): the snapshot scope was widened from the plan's "metadata + capabilities + files" to also include the connector's **setting definitions** (name, category, type, required flags, defaults, drop-down options; not any Connected System-supplied values). This was necessary, not gold-plating: the only live mutation path today is the startup drift-sync (`SeedingServer.SyncConnectorDefinitionAsync`), which most often changes a setting definition shipped in updated connector code, so omitting settings would have left the primary capture path recording semantically-empty snapshots that the dedupe guard then skipped. Setting definitions carry no secrets, but the builder defensively redacts an encrypted-typed default value (a keyed hash, never plaintext) even though that field is never populated in practice. File binaries (`ConnectorDefinitionFile.File`) are never captured; each file records a lowercase-hex SHA-256 content hash so a re-upload of changed bytes diffs without embedding or disclosing the assembly (asserted by test: the recognisable byte sequence's Base64 never appears in a stored snapshot).

The five mutators were reshaped onto the Scheduler's initiator-triad signature (`ActivityInitiatorType, Guid?, string?`, plus optional `changeReason`/`parentActivityId`) rather than gaining user/ApiKey overloads: there is no principal-carrying caller yet (built-in definitions are seeded and drift-synced, both System), and the triad lets a future upload UI/API arrive without a signature change. Only `UpdateConnectorDefinitionAsync` is live today; the drift-sync now attributes it to System and groups it under the seeding parent (`GetOrCreateSeedingActivityAsync`, created lazily only when there is actual drift), so a capability/setting change shipped in a JIM upgrade is audited under System Initialisation, while the semantic dedupe makes a no-drift startup record nothing. The two file mutators roll up into the owning definition's history (an Update Activity targeting the definition, resolved from `ConnectorDefinitionFile.ConnectorDefinition`), matching the granular sub-entity precedent.

The built-in LDAP and File Connector Definitions are seeded in the shared `SeedDataAsync` batch (like the Predefined Searches), so their baseline is recorded after the batch over the "created this pass" list (`RecordSeededConnectorDefinitionBaselineAsync`, mirroring `SearchServer.RecordSeededPredefinedSearchBaselineAsync`): a System-attributed Create Activity plus version-1 snapshot under the seeding parent. `PrepareConnectorDefinitionAsync` returns null for already-existing definitions, so the list holds only this pass's creations and a restart re-baselines nothing.

**Known still-open by decision (unchanged from Phase 7):** factory reset (`SystemRepository.ResetSystemAsync`) truncates the `Activities` table but preserves the built-in Connector Definitions, Predefined Searches, and Metaverse Object Types/Attributes (they are not customer data). `SeedAsync` then no-ops on the next startup because `ServiceSettings` survive the reset, so those preserved objects lose their v1 baseline Activity and it is not recreated. This is a pre-existing, consistent gap across every batch-seeded type (the Schedule and Role baselines, seeded individually and re-seeded by the reset path, do not have it); closing it (for example by re-baselining preserved seed objects after a reset, or re-running the seeding pipeline per #916) is a cross-type follow-up, deliberately not tackled in this phase alone.

### Phase 9: Example Data Templates and Sets ✅
1. `ExampleDataSet` gains `IAuditable` (+ migration); `ActivityTargetType.ExampleDataSet`; `ExampleDataSetId` FK (templates reuse `ExampleDataTemplateId`). ✅
2. Snapshot builders and capture across the six `ExampleDataServer` mutators; seed-time creates capture System-initiated v1 baselines, with the dedupe guard preventing version churn on restart re-seeding. ✅
3. REST routes on `ExampleDataController` + `ChangeReason`; `-ChangeReason` on `Set-JIMExampleDataSet`; both `-Type` values. ✅
4. UI: Changes tabs on `ExampleDataTemplateDetail` and `ExampleDataSetDetail` (full parity per PRD decision 3, ahead of the anticipated customer CRUD UI). ✅

Delivery notes (2026-07-06): the Phase 1 note left the `ExampleDataTemplate` Activity category "under System until Phase 9, because its existing activities are template-generation runs". Resolved here by **splitting the generation run onto its own target type**: a new `ActivityTargetType.DataGeneration` (categorised System) now carries a template *execution* (the run), created by `TaskingServer` with `TargetOperationType.Execute` and still linking to its template via `ExampleDataTemplateId`; `ExampleDataTemplate` is reclassified to Configuration and now denotes only the template *definition*'s change history. This keeps the Activities Configuration filter free of generation-run noise while surfacing template edits correctly (chosen over leaving the template under System, or reclassifying without the split which would have pulled generation runs into the Configuration filter). The migration includes a data migration reclassifying existing `ExampleDataTemplate` + `Execute` rows to `DataGeneration` (all pre-existing rows of that target type were runs, since template config capture did not exist before). Four UI touchpoints followed the split: the ActivityList generation-stats chip and type-filter, the ActivityDetail target deep-link (both `ExampleDataTemplate` and `DataGeneration` link to the template), and the OperationsHistoryTab run rendering.

`ExampleDataSet` gained `IAuditable` (seven audit columns, one migration); the snapshot is metadata-only (name, culture, built-in, **value count** only, never the individual values, which for a built-in set run to thousands of strings; asserted by test). The template snapshot captures its object types, generation attributes (pattern, expression, number/date config, percentages) and referenced Example Data Sets (by id reference, never the set's values). The Set mutators gained user/ApiKey overloads and `ExampleDataController` moved onto `ApiControllerBase` so REST edits attribute to the calling principal (previously the controller resolved none); the template mutators are System-attributed via the initiator triad (no user-facing template CRUD exists yet; the REST controller mutates only data sets). Built-in Example Data Sets and the built-in template record their System-attributed Create + v1 baseline after the seed batch (`RecordSeededExampleDataSetBaselineAsync` / `RecordSeededExampleDataTemplateBaselineAsync`), matching the Predefined Search / Connector Definition pattern; `SeedAsync`'s `ServiceSettingsExistAsync` guard means every set in the pass is genuinely new, so no re-baseline on restart.

**Known still-open by decision (unchanged across Phases 7-9):** factory reset truncates `Activities` but preserves the built-in Example Data Sets/Template (and the other batch-seeded types), and `EnsureBuiltInExampleDataTemplateAsync` rebuilds the template via a repository-direct repair path without capture, so their v1 baselines are lost on reset and not recreated. This is the same cross-type reset gap documented in Phases 7 and 8; closing it (re-baselining preserved seed objects after a reset, or re-running the seeding pipeline per #916) remains a deliberate follow-up.

### Phase 10: Close-out ✅
1. `docs/configuration/activities.md` coverage note updated to enumerate full coverage; per-type doc pages already updated in their phases. ✅
2. `engineering/DEVELOPER_GUIDE.md` capture-architecture section updated for the centralised helper. ✅
3. PRD acceptance-criteria sweep; original #14 PRD/plan status notes updated; issue #14 sub-task ticked. ✅

Delivery notes (2026-07-06): the `activities.md` coverage note now states coverage is complete across every administrator-mutable configuration type (previously it read as a growing subset). The `DEVELOPER_GUIDE.md` section was rewritten from its stale "Synchronisation Rule and Connected System only" framing to describe the delivered architecture: the type-agnostic `ConfigurationChangeCaptureService` (toggle/dedupe/version/best-effort/tombstone, int/Guid/string overloads), `Activity.SetConfigurationTargetId` target-column mapping, per-type redacted `ConfigurationSnapshotService` builders, the now-delivered target-type-aware retention (`History.ConfigurationChangeRetentionPeriod`), the System Initialisation seed-baseline pattern, and the `DataGeneration` run/definition split. The PRD's nine acceptance criteria are all ticked and its status moved to Done. This plan moves to `engineering/plans/done/`.

## Design Decisions

- **Q: Per-type Activity FK columns, or a generic `TargetIntId`/`TargetGuidId` pair?** Per-type columns, matching the existing `ConnectedSystemId`/`SyncRuleId`/`ScheduleId` pattern: real FKs with navigations, self-documenting queries, and the delete semantics already proven to keep tombstone history retrievable. Nullable columns on the high-volume Activity table are cheap in PostgreSQL; a generic pair would save columns but lose referential integrity and diverge from the established idiom.
- **Q: Service Setting keyed by string?** Yes; its PK is the setting `Key` (string). A third, string-keyed overload set is added through the retrieval stack rather than forcing a surrogate int key onto a stable entity.
- **Q: Centralise the capture guard?** Yes. Three hand-rolled copies were tolerable; twelve are not. The refactor lands first, behind the existing test fixtures, so every new type gets toggle/dedupe/best-effort/tombstone behaviour by construction rather than by copy-paste.
- **Q: API key secret in snapshots?** Never, in any form. Unlike connected-system credentials (keyed HMAC to prove rotation), the API key secret is shown once at creation and stored only as a hash at rest; there is no legitimate "did it change" question to answer, so the snapshot stores metadata and role assignments only.
- **Q: Where do Role membership changes attach?** To the Role (snapshot of the Role's post-change membership), not the member MVO: the auditable question is "who was in this role and when", and the MVO already has its own identity-data change history under #269.
- **Q: Role definition mutators now or with #612?** Builder, target type, FK, retrieval, and seed-baseline capture now; actual definition mutators arrive with #612 and inherit the pattern. This satisfies "the capture path must exist even while built-in Roles are seed-only" without inventing speculative admin APIs.
- **Q: Prompt on Predefined Search criteria micro-edits?** No; capture yes, prompt no. Per-criterion dialogs would nag on every row edit. Precedent: the Schedules list toggle is intentionally promptless.
- **Q: Back-fill v1 baselines for pre-existing objects on upgrade?** No; consistent with the original delivery, an object's first captured post-upgrade change becomes its v1. Seed-managed types (Roles, Example Data, Connector Definitions) get natural baselines because seeding itself is captured.

## Success Criteria

Maps to the PRD acceptance criteria: every mutation path across the nine type groups records an Activity carrying a versioned, redacted snapshot (including the fixed Metaverse Object Type update, with Schedule step changes continuing to version exactly once per owning save); API Key mutations flow through the application layer; encrypted Service Setting values and API key secrets are provably absent from stored and rendered history; every type is retrievable with diff/compare parity via `ChangeHistoryServer`, REST, and `Get-JIMConfigurationChangeHistory`; history is discoverable in the admin portal per the tab/per-row decision; reason capture works on UI, `-ChangeReason`, and REST for the covered mutations; toggle, dedupe, best-effort, and retention behaviours hold for all new paths, each covered by tests.

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Refactoring the three live capture paths regresses delivered behaviour | High | Phase 1 is a pure refactor gated on the existing capture/retrieval fixtures; no behaviour change permitted |
| A secret leaks into a new snapshot (Service Setting value, API key secret) | High (security) | Redaction in the builder, not the caller; explicit per-type tests asserting absence of plaintext, ciphertext, and (for API keys) any hash |
| Activity migration on large deployments | Low | Nullable columns only; PostgreSQL adds them without a table rewrite |
| API Key controller refactor breaks REST clients | Medium | Contracts (routes, DTOs, status codes) preserved verbatim; API tests pin them before the refactor |
| String-keyed overloads spread special-casing | Medium | Contained to the routing layer (repository query + one overload set); the diff/UI/PowerShell surfaces stay key-shape-agnostic |
| #377 lands mid-stream and adds uncaptured attribute mutation paths | Medium | Dependency noted both ways; Phase 3 test coverage asserts capture per `MetaverseServer` mutator, so a new path without capture fails review visibly |
| Seed-time capture churns versions on every restart | Low | The semantic dedupe guard already makes re-seeding a no-op; asserted by tests in Phases 6, 8, 9 |

## Dependencies

- Builds entirely on the delivered #14 infrastructure; no new NuGet packages, no external services.
- Related issues: #612 (RBAC roll-out; consumes the Role capture pattern), #377 (Metaverse Attribute CRUD; shares Phase 3 paths), #827 (configuration change preview; benefits from full coverage).

## Open Questions

None. The draft PRD's four open questions were resolved on 2026-07-05 (see the PRD's Decisions section); the judgement calls made here (promptless criteria micro-edits, no baseline back-fill, Role-side membership snapshots) are recorded above as design decisions and are cheap to revisit before their phases start.
