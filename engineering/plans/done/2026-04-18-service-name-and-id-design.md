# Service Name and Service ID — Design

**Issue:** [#583](https://github.com/TetronIO/JIM/issues/583)
**Milestone:** v0.9-STABILISATION
**Date:** 2026-04-18

## Problem

Customers running more than one JIM instance (e.g. edge sync deployments) cannot tell them apart at a glance. Three concrete pain points:

1. Multiple admin browser tabs open to different instances look identical.
2. Logs and telemetry have no stable machine-readable instance identifier.
3. Air-gapped, independently-deployed instances need a way to correlate back to the customer's own inventory.

## Goals

- Add a user-editable **Service Name** that appears in the portal chrome so operators can identify the instance at a glance.
- Add a read-only **Service ID** (GUID) that is generated once on first start and never changes, for use by tooling, logs, and telemetry.
- Surface both through the Web API and the PowerShell module, consistent with existing service-settings patterns.

## Non-goals

- Automatic propagation of Service Name/ID into log lines, activity records, or connector telemetry. That's a follow-up.
- Syncing Service Name across replicas, clusters, or high-availability topologies. JIM's deployment model is single-instance.
- A dedicated "About this instance" page. Settings page + portal chrome is enough for v0.9.

## Design

### Model and configuration layer

**`ServiceSettingCategory` (enum, `JIM.Models/Core/CoreEnums.cs`):** add `Instance = 6`. Not `Identity` (overloaded in an identity-management product) and not `UI` (these aren't UI settings, they're instance-identity settings that happen to surface in UI).

**`ServiceSettingValueType` (enum, same file):** add `Guid = 6`. Preferred over storing a GUID in a `String`:

- `ConvertSettingValue<T>` gets type-safe `Guid.Parse`.
- The admin UI can render a monospaced value with a copy-to-clipboard affordance.
- PUT requests get server-side format validation.

**`Constants.SettingKeys` (`JIM.Models/Core/Constants.cs`):** add

- `ServiceName = "Instance.Name"`
- `ServiceId = "Instance.Id"`

### Seeding

`SeedingServer.SyncServiceSettingsAsync` seeds both settings. Service Name uses the existing `SeedSettingAsync` helper (nullable default, editable). Service ID needs a different contract — it must be generated exactly once and never overwritten on subsequent startups.

Add a new helper:

```csharp
private async Task SeedSettingOnceAsync(ServiceSetting template, Func<string> valueFactory)
{
    if (await Application.ServiceSettings.SettingExistsAsync(template.Key))
        return;

    template.Value = valueFactory();
    await Application.ServiceSettings.CreateSettingAsync(template);
    Log.Information("SeedSettingOnceAsync: Generated '{Key}' = '{Value}'", template.Key, template.Value);
}
```

Called as:

```csharp
await SeedSettingOnceAsync(new ServiceSetting
{
    Key = Constants.SettingKeys.ServiceId,
    DisplayName = "Service ID",
    Description = "A stable, immutable identifier generated once when this JIM instance was created. Used by tooling, logs, and telemetry to identify this instance.",
    Category = ServiceSettingCategory.Instance,
    ValueType = ServiceSettingValueType.Guid,
    IsReadOnly = true
}, () => Guid.NewGuid().ToString());
```

The existing `CreateOrUpdateSettingAsync` is not suitable here: it re-applies `Value` for any read-only setting (to support environment-variable-backed settings). For Service ID we need the opposite contract — create once, never touch again.

Service Name is seeded with the ordinary helper:

```csharp
await SeedSettingAsync(new ServiceSetting
{
    Key = Constants.SettingKeys.ServiceName,
    DisplayName = "Service Name",
    Description = "A friendly, editable name for this JIM instance. Appears in the sidebar, browser tab title, and footer so you can tell instances apart.",
    Category = ServiceSettingCategory.Instance,
    ValueType = ServiceSettingValueType.String,
    DefaultValue = null,
    IsReadOnly = false
});
```

### Value conversion

`ServiceSettingsServer.ConvertSettingValue<T>` gains a `Guid` branch:

```csharp
if (underlyingType == typeof(Guid))
    return (T)(object)Guid.Parse(value);
```

Read-only enforcement for Service ID requires no new code; `PrepareUpdateAsync` already throws `InvalidOperationException` for any `IsReadOnly` setting.

### API and DTOs

`ServiceSettingDto.FromEntity` already serialises the stored string value, so Service ID flows through as a GUID string with no change. The existing PUT endpoint returns `400 BadRequest` for read-only settings, so Service ID updates are already rejected — we only need a test to confirm it.

No new endpoints.

### PowerShell

`Get-JIMServiceSetting -Key Instance.Name` and `Get-JIMServiceSetting -Key Instance.Id` work through the existing list/get cmdlet. `Set-JIMServiceSetting -Key Instance.Id ...` fails with the existing read-only error path. `Reset-JIMServiceSetting` is not meaningful for Service ID (read-only) and sets Service Name to `null` (default).

No new cmdlets.

### Portal chrome — where Service Name appears

Three surfaces, each small, each reinforcing the others. Service ID appears only on the Settings page.

**1. Drawer header (`Shared/MainLayout.razor`, primary in-app signal):**

Keep "JIM" next to the logo. Render Service Name as a second line beneath it in monospace (existing `jim-text-code` class), `Typo.caption`, `mud-text-secondary`:

```
Expanded drawer:        Collapsed drawer:
 [logo]  JIM             [logo]   <- tooltip on hover:
         HQ-Production              HQ-Production
```

When Service Name is null: the second line is omitted — no placeholder, no empty gap. Collapsed-drawer tooltip appears only when Service Name is set.

The layout needs access to the value. Simplest path: `MainLayout` loads it once via `IJimApplicationFactory` in `OnInitializedAsync` (same pattern `NavMenu` already uses), stores it in a field, and renders conditionally. Changes to Service Name require a page reload to reflect — acceptable for a setting that changes infrequently.

**2. Browser tab title (`MainLayout.razor`, cross-tab signal):**

Currently `<PageTitle>JIM</PageTitle>` at the layout level, overridden by pages. Adjust the layout `<PageTitle>` to `"JIM"` when Service Name is null, `"JIM — {ServiceName}"` when set. Pages that set their own `<PageTitle>` still override (e.g. "Service Settings"), which is fine — the tab title on most pages is overridden per page anyway. The meaningful effect is tabs parked on pages without a custom PageTitle now distinguish themselves, and where we can fit it we can extend per-page titles in a follow-up.

Pragmatic scope for v0.9: only the layout-level `<PageTitle>`. Per-page title suffixes can come later if needed.

**3. Footer (`MainLayout.razor`, supplementary):**

Today: `© 2026 Tetron | All rights reserved | v0.8.4 | GitHub`. Add Service Name before GitHub when set:

```
© 2026 Tetron | All rights reserved | v0.8.4 | HQ-Production | GitHub
```

Null → current rendering unchanged.

### `/admin/settings` — Service ID display

The existing Settings table renders all service settings grouped by category. The new `Instance` category appears as a new group with two rows:

- **Service Name** — standard editable string field.
- **Service ID** — read-only GUID rendered with the existing `jim-text-code` monospace class, plus a copy-to-clipboard `MudIconButton` (mirror the secret-reveal pattern already used for `StringEncrypted` settings).

Add a case in `Settings.razor`'s value-rendering switch for `ServiceSettingValueType.Guid`.

Add a display name for the new category in `GetCategoryDisplayName`: `Instance = "Instance"`.

### Model and schema changes

- `ServiceSettingValueType.Guid` and `ServiceSettingCategory.Instance` are new enum values. Enums are stored as integers in PostgreSQL and are backwards-compatible (existing rows use values 0–5, the new values are 6).
- **No EF migration is required.** The enum expansion is a code-only change; no column additions, no constraint changes.

### Changelog entry

Under `[Unreleased]`, category `Added`:

> ✨ Added a Service Name and Service ID so you can tell JIM instances apart at a glance. Set a friendly name per instance on the Service Settings page and see it under "JIM" in the sidebar, in the browser tab title, and in the footer. The Service ID is generated once per instance and never changes — useful for tooling, logs, and telemetry.

## Test plan (TDD)

All tests are written before implementation. Locations follow existing conventions.

**`test/JIM.Worker.Tests/` (or `test/JIM.Application.Tests/` if it exists) — seeding:**

- First-run seeding creates Service Name with null value and Service ID with a valid GUID.
- Second-run seeding does not regenerate Service ID when one already exists.
- Second-run seeding does not overwrite a user-set Service Name.
- Generated Service ID parses as a valid `Guid` and is unique per seed cycle (test two fresh seed runs get different GUIDs).

**`test/JIM.Worker.Tests/` — value conversion:**

- `GetSettingValueAsync<Guid>(Instance.Id)` returns the correct `Guid`.
- `ConvertSettingValue<Guid>` with a malformed string returns `default` (matches existing catch-and-default behaviour).

**`test/JIM.Web.Api.Tests/` — API:**

- `GET /api/v1/service-settings/Instance.Name` returns the setting with null value by default.
- `GET /api/v1/service-settings/Instance.Id` returns the setting with a GUID string value.
- `PUT /api/v1/service-settings/Instance.Name` with `{"value":"HQ-Production"}` succeeds and returns the updated DTO.
- `PUT /api/v1/service-settings/Instance.Id` with any value returns `400 BadRequest` with a read-only error message.
- `DELETE /api/v1/service-settings/Instance.Name` (revert) sets the value back to null.
- `DELETE /api/v1/service-settings/Instance.Id` returns `400 BadRequest`.

**`test/JIM.Models.Tests/`:**

- `ServiceSetting` with `ValueType = Guid` round-trips a `Guid.NewGuid().ToString()` value via `GetEffectiveValue`.

**UI/portal chrome:** no automated tests (JIM has no UI tests). Manual verification covered under acceptance.

## Acceptance (copied from issue for traceability)

- [ ] `Service Name` and `Service ID` appear on `/admin/settings` with correct category, types, and read-only state.
- [ ] Service ID is auto-generated exactly once on first startup and never changes thereafter.
- [ ] Setting Service Name via PowerShell works; attempting to set Service ID fails with a clear read-only error.
- [ ] Both settings are visible somewhere in the Portal chrome so the instance can be identified at a glance — delivered via drawer header, browser tab title, and footer (Service Name only; Service ID on Settings page only).
- [ ] Tests cover: default values, Service ID GUID generation on first run, Service ID preservation across restarts, Service Name CRUD via the API, read-only enforcement for Service ID.
- [ ] Changelog entry added under `[Unreleased]` (✨ category).

## Open decisions deferred to follow-ups

- Whether to also propagate Service Name/ID into log enrichers, Activity records, or exported diagnostic bundles. Out of scope for v0.9.
- Per-page browser-title suffixing (every page gets ` — {ServiceName}`). The v0.9 scope covers the layout-level title only.
- Per-instance favicon tinting to further differentiate tabs. Speculative; revisit if customers ask.
