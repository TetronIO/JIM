# Feature Flags

- **Status:** Done
- **Created:** 2026-09-24
- **Author:** Tetron
- **Issue:** #1781

## Problem Statement

JIM has no mechanism for shipping a feature that is not yet ready to be on by default. Every feature currently ships fully on, or it does not ship at all, which forces a choice between holding back a merge until a feature is completely finished, or shipping it live to every deployment on day one. Neither suits a feature like Unique Value Generation (#242), whose engine work should be developed and merged incrementally without exposing incomplete behaviour to customers, or a feature JIM wants customer feedback on before locking its behaviour in.

## Goals

- One flag per feature, default off, catalogued in code so every flag is discoverable in one place.
- Two tiers: Preview (visible and toggleable by administrators, documented publicly) and InDevelopment (never surfaced to administrators; on only in development and the integration harness).
- Surface parity for the switch itself: portal, REST, PowerShell, each with tests and docs.
- Flag changes are audited exactly like any other Service Setting change.
- Homegrown on the existing Service Settings store; no new dependency.

## Non-Goals

- Gating any actual feature behaviour. This PR delivers the mechanism only; the #242 Unique Value Generation gating is a separate change on another branch.
- A generic experimentation/percentage-rollout system. JIM's flags are per-instance on/off switches, not a cohort-targeting framework.
- Client-side (browser) flag evaluation; every check is server-side.

## User Stories

1. As a JIM developer, I want to merge incomplete engine work behind a flag that is off everywhere but development and the integration harness, so that I can develop a feature incrementally without exposing it to customers.
2. As a JIM administrator, I want to see and try Preview features from the Service Settings page, so that I can opt into new capability before it is switched on for everyone.
3. As an automation author, I want to enable and disable feature flags from PowerShell, so that I can script environment setup (in particular, integration test scenarios) without using the portal.

## Requirements

### Functional Requirements

1. A `FeatureFlagCatalogue` in `JIM.Models.Core` lists every flag as a `FeatureFlagDefinition` (key, display name, description, tier, tracking issue number for the flag's own removal).
2. Each flag is one `ServiceSetting` row in a new `FeatureFlags` category, Boolean, default `"false"`, seeded by the existing Service Settings seeding pass, which also prunes rows for flags removed from the catalogue.
3. The generic Service Settings list, REST endpoints, and PowerShell cmdlets (`Get-JIMServiceSetting`/`Set-JIMServiceSetting`/`Reset-JIMServiceSetting`) exclude and refuse the `FeatureFlags` category.
4. `FeatureFlagServer` (`JIM.Application`), exposed as `JimApplication.FeatureFlags`, provides: `IsEnabledAsync`, `EnsureEnabledAsync` (throws `FeatureDisabledException`), `GetFeatureFlagsAsync`, and `SetFeatureFlagAsync` (interactive-user and API-key overloads). Enabling an InDevelopment flag requires an explicit `allowInDevelopment: true`. An unknown key throws `FeatureFlagNotFoundException`.
5. REST: `GET api/v1/features`, `PUT api/v1/features/{key}`. `[Authorize(Roles = "Administrator")]`, matching `ServiceSettingsController`.
6. PowerShell: `Get-JIMFeature`, `Enable-JIMFeature` (`-AllowInDevelopment`), `Disable-JIMFeature`, both write cmdlets `SupportsShouldProcess`.
7. Portal: a "Preview features" card on the Service Settings page, always shown, listing Preview-tier flags with a switch, description, `<PreviewChip />`, and last-changed info. InDevelopment flags join the card only when `IWebHostEnvironment.IsDevelopment()`.

### Non-Functional Requirements

- `IsEnabledAsync` is cheap: cached per `JimApplication` instance.
- Air-gap deployable, no new third-party dependency.

## Examples and Scenarios

### Scenario 1: Administrator enables a Preview feature

**Given**: JIM ships a Preview-tier flag, currently off.
**When**: An administrator opens Service Settings and switches it on in the "Preview features" card.
**Then**: The feature's own controls appear (carrying a Preview chip), and the change is recorded in the flag's configuration change history.

### Scenario 2: Integration harness enables an InDevelopment flag

**Given**: A flag is tier InDevelopment.
**When**: Scenario setup runs `Enable-JIMFeature -Name '...' -AllowInDevelopment`.
**Then**: The flag switches on; the same call without `-AllowInDevelopment` fails with a clear error, and the flag never appears in the portal's Preview features card outside a Development host.

### Scenario 3: Flag removed from the catalogue

**Given**: A flag's feature has shipped and its `FeatureFlagCatalogue` entry is deleted.
**When**: JIM next starts.
**Then**: The seeding pass removes the flag's `ServiceSetting` row; nothing about it remains.

## Constraints

- Gate at entry points (a portal control, a REST endpoint, a server-side configuration write) only, never deep inside the sync engine.
- Configuration already made while a flag was on must keep working, or fail loudly and specifically, when the flag is later off; never silently ignored.

## Affected Areas

| Area | Impact |
|------|--------|
| Database | No migration: `ServiceSettingCategory` gains an appended enum member (stored as int); flag rows are ordinary `ServiceSetting` inserts |
| API | New `FeatureFlagsController`; `ServiceSettingsController` excludes/refuses the `FeatureFlags` category |
| Application | New `FeatureFlagServer`; `SeedingServer` seeds and prunes flag rows |
| PowerShell | New `Get-JIMFeature`/`Enable-JIMFeature`/`Disable-JIMFeature` cmdlets |
| UI | Service Settings page gains a "Preview features" card; new `<PreviewChip />` shared component |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/administration/preview-features.md` | New page: what Preview means, where to turn features on |
| `docs/powershell/feature-flags.md` | New page: `Get-JIMFeature`/`Enable-JIMFeature`/`Disable-JIMFeature` |
| `docs/configuration/service-settings.md` | Note the `FeatureFlags` category's exclusion from the generic surfaces |
| `engineering/DEVELOPER_GUIDE.md` | New "Feature Flags" pattern section |

## Dependencies

- None. The #242 Unique Value Generation gating that will use this mechanism's `InDevelopment` flag is a separate, later change.
