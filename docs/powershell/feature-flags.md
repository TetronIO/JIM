---
title: Feature Flags
description: "The cmdlets that list JIM's preview features and switch them on or off."
type: reference
audience: automation-author
---

# Feature Flags

Cmdlets for viewing and toggling JIM's [Preview features](../administration/preview-features.md). Feature flags are stored as Service Settings under the hood, but change only through these cmdlets (or the equivalent REST endpoints), never through [`Set-JIMServiceSetting`](service-settings.md).

!!! info
    A feature flag has a tier: **Preview** (returned by default, supported with a caveat) or **InDevelopment** (returned only with `-IncludeInDevelopment`, never something a production administrator needs to turn on).

---

## Get-JIMFeature

Retrieves feature flags. Returns every Preview-tier flag by default; specify `-IncludeInDevelopment` to also return In Development flags, and `-Name` to filter to a single flag by key.

<!-- BEGIN GENERATED: Get-JIMFeature -->
### Syntax

```powershell
Get-JIMFeature [[-Name] <string>] [-IncludeInDevelopment]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | No |  | The key of the one flag to return, such as "Features.SomeInDevelopmentFeature". Can be given by position, as the first argument. |
| `IncludeInDevelopment` | `switch` | No |  | Also returns In Development flags. Intended for development and the integration harness; enabling one outside Development still requires `-AllowInDevelopment` on `Enable-JIMFeature`. |

### Output

Each feature flag.

| Property | Type | Description |
|----------|------|-------------|
| `Key` | `string` | The flag's key, e.g. "Features.SomeInDevelopmentFeature". |
| `DisplayName` | `string` | Human-readable display name. |
| `Description` | `string` | What the flag controls. |
| `Tier` | `string` | The flag's tier: "Preview" (visible to administrators) or "InDevelopment" (developers only). |
| `TrackingIssueNumber` | `int` | The GitHub issue tracking removal of this flag once the feature it gates ships. |
| `Enabled` | `bool` | Whether the flag is currently enabled. |
| `LastUpdated` | `datetime` | When the flag was last changed, or empty if never changed from its seeded default. |
| `LastUpdatedByName` | `string` | The display name of the principal that last changed the flag, or empty if never changed. |
<!-- END GENERATED: Get-JIMFeature -->

### Examples

```powershell title="List every Preview-tier feature flag"
Get-JIMFeature
```

```powershell title="Include In Development flags (integration harness setup)"
Get-JIMFeature -IncludeInDevelopment
```

```powershell title="Get a single flag by key"
Get-JIMFeature -Name "Features.SomeInDevelopmentFeature" -IncludeInDevelopment
```

```powershell title="List only the flags currently switched on"
Get-JIMFeature -IncludeInDevelopment | Where-Object { $_.Enabled }
```

---

## Enable-JIMFeature

Switches a feature flag on. Enabling an In Development flag requires `-AllowInDevelopment`; without it, the server refuses the request. Supports `ShouldProcess`; use `-WhatIf` or `-Confirm` to preview or confirm the operation.

<!-- BEGIN GENERATED: Enable-JIMFeature -->
### Syntax

```powershell
Enable-JIMFeature [-Name] <string> [-AllowInDevelopment] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes |  | The key of the flag to enable, such as "Features.SomeInDevelopmentFeature". A flag piped from `Get-JIMFeature` binds by its Key. Can be given by position, as the first argument. Accepts pipeline input by property name, `Name` or `Key`. |
| `AllowInDevelopment` | `switch` | No |  | Required to enable an In Development flag; the integration harness uses it in scenario setup. A Preview-tier flag needs no such acknowledgement. |
| `PassThru` | `switch` | No |  | Returns the feature flag as updated. |

### Output

With `-PassThru`, the feature flag as updated; otherwise nothing.

Properties as under [Get-JIMFeature](#get-jimfeature).
<!-- END GENERATED: Enable-JIMFeature -->

### Examples

```powershell title="Enable a Preview-tier feature flag"
Enable-JIMFeature -Name "Features.SomePreviewFeature"
```

```powershell title="Enable an In Development flag, as the integration harness does in scenario setup"
Enable-JIMFeature -Name "Features.SomeInDevelopmentFeature" -AllowInDevelopment
```

```powershell title="Enable a flag and see its new state"
Enable-JIMFeature -Name "Features.SomePreviewFeature" -PassThru
```

### Notes

- Attempting to enable an In Development flag without `-AllowInDevelopment` produces a non-terminating error naming the flag.
- Each successful change creates an audit activity, in the same [Configuration Change History](../configuration/activities.md#configuration-change-history) as any other Service Setting change.

---

## Disable-JIMFeature

Switches a feature flag off. Disabling never requires acknowledgement, whatever the flag's tier. Supports `ShouldProcess`; use `-WhatIf` or `-Confirm` to preview or confirm the operation.

<!-- BEGIN GENERATED: Disable-JIMFeature -->
### Syntax

```powershell
Disable-JIMFeature [-Name] <string> [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes |  | The key of the flag to disable, such as "Features.SomeInDevelopmentFeature". A flag piped from `Get-JIMFeature` binds by its Key. Can be given by position, as the first argument. Accepts pipeline input by property name, `Name` or `Key`. |
| `PassThru` | `switch` | No |  | Returns the feature flag as updated. |

### Output

With `-PassThru`, the feature flag as updated; otherwise nothing.

Properties as under [Get-JIMFeature](#get-jimfeature).
<!-- END GENERATED: Disable-JIMFeature -->

### Examples

```powershell title="Disable a feature flag"
Disable-JIMFeature -Name "Features.SomeInDevelopmentFeature"
```

```powershell title="Disable every flag Get-JIMFeature currently returns"
Get-JIMFeature -IncludeInDevelopment | Disable-JIMFeature
```

### Notes

- Each successful change creates an audit activity.

---

## See also

- [Preview Features](../administration/preview-features.md): what a Preview feature is, and where to turn one on in the portal
- [Service Settings](service-settings.md): the underlying store, and why the generic Service Settings cmdlets refuse feature-flag keys
- [Activities](activities.md): viewing audit activities created by flag changes
