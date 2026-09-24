---
title: Feature Flags
---

# Feature Flags

Cmdlets for viewing and toggling JIM's [Preview features](../administration/preview-features.md). Feature flags are stored as Service Settings under the hood, but change only through these cmdlets (or the equivalent REST endpoints), never through [`Set-JIMServiceSetting`](service-settings.md).

!!! info
    A feature flag has a tier: **Preview** (returned by default, supported with a caveat) or **InDevelopment** (returned only with `-IncludeInDevelopment`, never something a production administrator needs to turn on).

---

## Get-JIMFeature

Retrieves feature flags. Returns every Preview-tier flag by default; specify `-IncludeInDevelopment` to also return In Development flags, and `-Name` to filter to a single flag by key.

### Syntax

```powershell
Get-JIMFeature [[-Name] <string>] [-IncludeInDevelopment]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | No, Position 0 | | The flag's key, e.g. `"Features.UniqueValueGeneration"`. Filters to a single flag. |
| `IncludeInDevelopment` | `switch` | No | `$false` | Also return In Development flags. |

### Output

Returns one or more `PSCustomObject` instances with the following properties:

| Property | Type | Description |
|----------|------|-------------|
| `Key` | `string` | The flag's key |
| `DisplayName` | `string` | Human-readable name |
| `Description` | `string` | What the flag controls |
| `Tier` | `string` | `"Preview"` or `"InDevelopment"` |
| `TrackingIssueNumber` | `int` | The GitHub issue tracking the flag's own removal |
| `Enabled` | `bool` | Whether the flag is currently on |
| `LastUpdated` | `datetime` | When the flag was last changed, or `$null` if never changed |
| `LastUpdatedByName` | `string` | Who last changed the flag, or `$null` if never changed |

### Examples

```powershell title="List every Preview-tier feature flag"
Get-JIMFeature
```

```powershell title="Include In Development flags (integration harness setup)"
Get-JIMFeature -IncludeInDevelopment
```

```powershell title="Get a single flag by key"
Get-JIMFeature -Name "Features.UniqueValueGeneration" -IncludeInDevelopment
```

```powershell title="List only the flags currently switched on"
Get-JIMFeature -IncludeInDevelopment | Where-Object { $_.Enabled }
```

---

## Enable-JIMFeature

Switches a feature flag on. Enabling an In Development flag requires `-AllowInDevelopment`; without it, the server refuses the request. Supports `ShouldProcess`; use `-WhatIf` or `-Confirm` to preview or confirm the operation.

### Syntax

```powershell
Enable-JIMFeature [-Name] <string> [-AllowInDevelopment] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes, Position 0 | | The flag's key to enable. Accepts pipeline input by property name (aliased to `Key`, so `Get-JIMFeature` output pipes straight in). |
| `AllowInDevelopment` | `switch` | No | `$false` | Required to enable an In Development flag. |
| `PassThru` | `switch` | No | `$false` | Returns the updated feature flag object. |

### Output

When `-PassThru` is specified, returns the updated feature flag object (same shape as `Get-JIMFeature`). Otherwise, no output.

### Examples

```powershell title="Enable a Preview-tier feature flag"
Enable-JIMFeature -Name "Features.SomePreviewFeature"
```

```powershell title="Enable an In Development flag, as the integration harness does in scenario setup"
Enable-JIMFeature -Name "Features.UniqueValueGeneration" -AllowInDevelopment
```

```powershell title="Enable a flag and see its new state"
Enable-JIMFeature -Name "Features.SomePreviewFeature" -PassThru
```

### Notes

- Attempting to enable an In Development flag without `-AllowInDevelopment` produces a non-terminating error naming the flag.
- Each successful change creates an audit activity, in the same [configuration change history](../configuration/activities.md#configuration-change-history) as any other Service Setting change.

---

## Disable-JIMFeature

Switches a feature flag off. Disabling never requires acknowledgement, whatever the flag's tier. Supports `ShouldProcess`; use `-WhatIf` or `-Confirm` to preview or confirm the operation.

### Syntax

```powershell
Disable-JIMFeature [-Name] <string> [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes, Position 0 | | The flag's key to disable. Accepts pipeline input by property name (aliased to `Key`). |
| `PassThru` | `switch` | No | `$false` | Returns the updated feature flag object. |

### Output

When `-PassThru` is specified, returns the updated feature flag object. Otherwise, no output.

### Examples

```powershell title="Disable a feature flag"
Disable-JIMFeature -Name "Features.UniqueValueGeneration"
```

```powershell title="Disable every flag Get-JIMFeature currently returns"
Get-JIMFeature -IncludeInDevelopment | Disable-JIMFeature
```

### Notes

- Each successful change creates an audit activity.

---

## See also

- [Preview Features](../administration/preview-features.md) -- what a Preview feature is, and where to turn one on in the portal
- [Service Settings](service-settings.md) -- the underlying store, and why the generic Service Settings cmdlets refuse feature-flag keys
- [Activities](activities.md) -- viewing audit activities created by flag changes
