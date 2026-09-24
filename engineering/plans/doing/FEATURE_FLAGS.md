# Feature Flags

- **Status:** Doing
- **Issue:** [#1781](https://github.com/TetronIO/JIM/issues/1781)
- **PRD:** [`../../prd/doing/PRD_FEATURE_FLAGS.md`](../../prd/doing/PRD_FEATURE_FLAGS.md)

## Overview

A homegrown feature-flag mechanism built directly on the existing Service Settings store, so JIM can ship incomplete or gradually-rolled-out work without a new dependency or a new admin concept. One flag per feature, default off, catalogued in code, with two tiers (Preview: administrator-visible; InDevelopment: developer/harness-only). This PR delivers the mechanism only; gating any real feature's behaviour with it is separate work.

## Technical Architecture

- **Catalogue (`JIM.Models.Core`):** `FeatureFlagTier` enum, `FeatureFlagDefinition` record, `FeatureFlagCatalogue` static class (`All` plus one named definition per flag), `FeatureFlagState` (definition + persisted state).
- **Storage:** each flag is one `ServiceSetting` row, category `ServiceSettingCategory.FeatureFlags` (appended, no migration needed - an int-backed enum member), Boolean, default `"false"`. `SeedingServer.SyncServiceSettingsAsync` seeds every catalogue entry and deletes any `FeatureFlags` row whose key has left the catalogue (`IServiceSettingsRepository.DeleteSettingAsync`, new).
- **Application layer:** `FeatureFlagServer` (`JIM.Application/Servers`), exposed as `JimApplication.FeatureFlags`. Reads cache per instance; writes go through `ServiceSettingsServer.UpdateSettingValueAsync` so flag changes get the same Activity/configuration-change-capture path as any other setting, keyed by the flag's Service Setting key. `FeatureFlagNotFoundException` / `FeatureDisabledException` in `JIM.Application/Exceptions`.
- **REST:** `FeatureFlagsController` (`api/v1/features`), `[Authorize(Roles = "Administrator")]` matching `ServiceSettingsController`. `ServiceSettingsController` excludes the `FeatureFlags` category from its list and refuses generic get/update/revert on a flag key (400, pointing at the Features endpoints).
- **PowerShell:** `Get-JIMFeature`, `Enable-JIMFeature` (`-AllowInDevelopment`), `Disable-JIMFeature`, registered in the manifest.
- **Portal:** Service Settings page gains a "Preview features" card (always shown); new `<PreviewChip />` shared component; InDevelopment flags join the card only on `IWebHostEnvironment.IsDevelopment()`.
- **Tests:** `JIM.TestSupport.InMemoryServiceSettingsRepository` (new), a minimal in-memory `IServiceSettingsRepository` fake with a `WithAllFeatureFlagsEnabled()` factory, so gating tests can construct a `JimApplication` with flags on without mocking the full audit path.

## Implementation Phases

### Phase 1: Catalogue and storage ✅
- `FeatureFlagTier`, `FeatureFlagDefinition`, `FeatureFlagCatalogue`, `FeatureFlagState` in `JIM.Models.Core`.
- `ServiceSettingCategory.FeatureFlags` appended.
- Seeding: create-from-catalogue and prune-removed, in `SeedingServer.SyncServiceSettingsAsync`.
- `IServiceSettingsRepository.DeleteSettingAsync` (interface, Postgres implementation, `ServiceSettingsServer` wrapper).

### Phase 2: Application server ✅
- `FeatureFlagServer`: `IsEnabledAsync`, `EnsureEnabledAsync`, `GetFeatureFlagsAsync`, `SetFeatureFlagAsync` (user + API key overloads), tier gate on enabling InDevelopment.
- Exposed on `JimApplication.FeatureFlags`.

### Phase 3: REST + generic-surface exclusion ✅
- `FeatureFlagsController`, `FeatureFlagDto`/`FeatureFlagUpdateRequestDto`.
- `ServiceSettingsController` list-exclude and key-refuse for the `FeatureFlags` category.

### Phase 4: PowerShell ✅
- `Get-JIMFeature`, `Enable-JIMFeature`, `Disable-JIMFeature`, manifest registration, Pester tests, cmdlet docs.

### Phase 5: Portal ✅
- `<PreviewChip />` shared component.
- Service Settings page "Preview features" card, generic settings list excludes `FeatureFlags`.

### Phase 6: Docs, tests, changelog ✅
- `docs/administration/preview-features.md`, `docs/powershell/feature-flags.md`, `docs/configuration/service-settings.md` note.
- `engineering/DEVELOPER_GUIDE.md` "Feature Flags" pattern section; `test/CLAUDE.md` "Tests run with flags on".
- `InMemoryServiceSettingsRepository` test helper.
- Changelog entry.

## Success Criteria

- A new flag needs one `FeatureFlagCatalogue` entry and nothing else to appear seeded, listed and toggleable across all three surfaces.
- Flag changes appear in configuration change history exactly like a Service Setting change.
- The generic Service Settings surfaces (list, REST, `Get/Set/Reset-JIMServiceSetting`) never expose a flag.
- `dotnet build`/`dotnet test` clean; runtime-verified seeding, portal card (dev shows the In Development flag), a REST toggle, and the change appearing in configuration change history.

## Dependencies

None beyond the existing Service Settings store and configuration-change-capture path.

## Risks & Mitigations

- **Risk:** a flag lives forever because nobody removes it. **Mitigation:** every catalogue entry carries a tracking issue number for its own removal, filed when the flag is introduced.
- **Risk:** a generic Service Setting write path bypasses the tier rules. **Mitigation:** flags are excluded/refused at the generic surfaces (list and per-key REST/PowerShell), so a flag can only change through `FeatureFlagServer`.
