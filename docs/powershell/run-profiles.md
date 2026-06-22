---
title: Run Profiles
---

# Run Profiles

Run Profile cmdlets manage and execute synchronisation Run Profiles on Connected Systems. A Run Profile defines a specific operation type (import, sync, or export) that can be executed against a Connected System. Use these cmdlets to list, create, modify, remove, and trigger Run Profiles.

---

## Get-JIMRunProfile

Retrieves one or more Run Profiles from a Connected System, either by Connected System or by Run Profile ID.

### Syntax

```powershell
# By Connected System ID (default)
Get-JIMRunProfile -ConnectedSystemId <int> [-Name <string>]

# By Connected System name
Get-JIMRunProfile -ConnectedSystemName <string> [-Name <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (ById set) | | ID of the Connected System. Alias: `Id`. Accepts pipeline input by property name. |
| `ConnectedSystemName` | `string` | Yes (ByName set) | | Name of the Connected System. Must be an exact match. |
| `Name` | `string` | No | | Filter Run Profiles by name. Supports wildcards (e.g., `"Full*"`). |

### Output

Returns one or more `PSCustomObject` instances representing Run Profiles, each containing properties such as `Id`, `Name`, `Type`, `ConnectedSystemId`, and `PartitionId`.

### Examples

```powershell title="List all Run Profiles for a Connected System"
Get-JIMRunProfile -ConnectedSystemId 1
```

```powershell title="Filter by name"
Get-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import"
```

```powershell title="Filter by name with wildcards"
Get-JIMRunProfile -ConnectedSystemName "HR System" -Name "Delta*"
```

```powershell title="Pipeline from Get-JIMConnectedSystem"
Get-JIMConnectedSystem -Name "Active Directory" | Get-JIMRunProfile
```

---

## New-JIMRunProfile

Creates a new Run Profile on a Connected System. Supports `ShouldProcess`; use `-WhatIf` or `-Confirm` to preview or confirm the operation.

### Syntax

```powershell
# By Connected System ID (default)
New-JIMRunProfile -ConnectedSystemId <int> -Name <string> -Type <string> [-PartitionId <int>] [-PassThru] [-WhatIf] [-Confirm]

# By Connected System name
New-JIMRunProfile -ConnectedSystemName <string> -Name <string> -Type <string> [-PartitionId <int>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (ById set) | | ID of the Connected System to create the Run Profile on. |
| `ConnectedSystemName` | `string` | Yes (ByName set) | | Name of the Connected System to create the Run Profile on. |
| `Name` | `string` | Yes (Position 0) | | Display name for the new Run Profile. |
| `Type` | `string` | Yes | | Operation type. Valid values: `FullImport`, `DeltaImport`, `FullSync`, `DeltaSync`, `Export`. |
| `PartitionId` | `int` | No | | Optional partition to scope this Run Profile to. If omitted, the Run Profile applies to the default partition. |
| `PassThru` | `switch` | No | `$false` | Returns the created Run Profile object to the pipeline. |

### Output

By default, no output. When `-PassThru` is specified, returns the created Run Profile object.

### Examples

```powershell title="Create a full import Run Profile"
New-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import" -Type FullImport
```

```powershell title="Create a Run Profile by Connected System name"
New-JIMRunProfile -ConnectedSystemName "Active Directory" -Name "Delta Sync" -Type DeltaSync
```

```powershell title="Create and capture the result"
$rp = New-JIMRunProfile -ConnectedSystemId 1 -Name "Export" -Type Export -PassThru
$rp.Id
```

```powershell title="Create a partition-scoped Run Profile"
New-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import (UK)" -Type FullImport -PartitionId 3
```

```powershell title="Preview with WhatIf"
New-JIMRunProfile -ConnectedSystemId 1 -Name "Delta Import" -Type DeltaImport -WhatIf
```

---

## Set-JIMRunProfile

Modifies an existing Run Profile. Supports `ShouldProcess`; use `-WhatIf` or `-Confirm` to preview or confirm the operation.

### Syntax

```powershell
# By ID (default)
Set-JIMRunProfile -Id <int> [-Name <string>] [-PartitionId <int>] [-PassThru] [-WhatIf] [-Confirm]

# By input object
Set-JIMRunProfile -InputObject <PSCustomObject> [-Name <string>] [-PartitionId <int>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById set) | | ID of the Run Profile to modify. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject set) | | A Run Profile object, typically from `Get-JIMRunProfile`. Accepts pipeline input. |
| `Name` | `string` | No | | New display name for the Run Profile. |
| `PartitionId` | `int` | No | | New partition ID to scope the Run Profile to. |
| `PassThru` | `switch` | No | `$false` | Returns the updated Run Profile object to the pipeline. |

### Output

By default, no output. When `-PassThru` is specified, returns the updated Run Profile object.

### Examples

```powershell title="Rename a Run Profile"
Set-JIMRunProfile -Id 42 -Name "Full Import (Production)"
```

```powershell title="Update via pipeline"
Get-JIMRunProfile -Id 42 | Set-JIMRunProfile -Name "Full Import (Renamed)" -PassThru
```

```powershell title="Change partition assignment"
Set-JIMRunProfile -Id 42 -PartitionId 5
```

```powershell title="Preview changes with WhatIf"
Set-JIMRunProfile -Id 42 -Name "New Name" -WhatIf
```

---

## Remove-JIMRunProfile

Deletes a Run Profile. This is a destructive operation with high impact; by default, PowerShell will prompt for confirmation. Use `-Force` to suppress the confirmation prompt. Supports `ShouldProcess`.

### Syntax

```powershell
# By ID (default)
Remove-JIMRunProfile -Id <int> [-Force] [-PassThru] [-WhatIf] [-Confirm]

# By input object
Remove-JIMRunProfile -InputObject <PSCustomObject> [-Force] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById set) | | ID of the Run Profile to delete. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject set) | | A Run Profile object, typically from `Get-JIMRunProfile`. Accepts pipeline input. |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt. |
| `PassThru` | `switch` | No | `$false` | Returns the deleted Run Profile object to the pipeline before removal. |

### Output

By default, no output. When `-PassThru` is specified, returns the Run Profile object that was deleted.

### Examples

```powershell title="Delete a Run Profile by ID"
Remove-JIMRunProfile -Id 42
```

```powershell title="Delete without confirmation"
Remove-JIMRunProfile -Id 42 -Force
```

```powershell title="Delete via pipeline"
Get-JIMRunProfile -Id 42 | Remove-JIMRunProfile -Force
```

```powershell title="Delete all Run Profiles for a Connected System"
Get-JIMRunProfile -ConnectedSystemId 1 | Remove-JIMRunProfile -Force
```

```powershell title="Capture before deleting"
$deleted = Remove-JIMRunProfile -Id 42 -Force -PassThru
Write-Host "Removed Run Profile: $($deleted.Name)"
```

---

## Start-JIMRunProfile

Queues a Run Profile for execution on the JIM worker service. The operation is asynchronous by default; use `-Wait` to block until execution completes.

### Syntax

```powershell
# By Run Profile ID (default)
Start-JIMRunProfile -RunProfileId <int> [-Wait] [-Timeout <int>] [-PassThru]

# By Run Profile name and Connected System name
Start-JIMRunProfile -ConnectedSystemName <string> -RunProfileName <string> [-Wait] [-Timeout <int>] [-PassThru]

# By Run Profile ID and Connected System name
Start-JIMRunProfile -RunProfileId <int> -ConnectedSystemName <string> [-Wait] [-Timeout <int>] [-PassThru]

# By Run Profile name and Connected System ID
Start-JIMRunProfile -ConnectedSystemId <int> -RunProfileName <string> [-Wait] [-Timeout <int>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `RunProfileId` | `int` | Yes (ById, ByIdAndName sets) | | ID of the Run Profile to execute. Alias: `Id`. |
| `RunProfileName` | `string` | Yes (ByName, ByNameAndId sets) | | Name of the Run Profile to execute. |
| `ConnectedSystemId` | `int` | Yes (ByNameAndId set) | | ID of the Connected System that owns the Run Profile. |
| `ConnectedSystemName` | `string` | Yes (ByName, ByIdAndName sets) | | Name of the Connected System that owns the Run Profile. |
| `Wait` | `switch` | No | `$false` | Blocks until execution completes, displaying a progress indicator. Polls every 2 seconds. |
| `Timeout` | `int` | No | | Maximum number of seconds to wait when `-Wait` is specified. If exceeded, an error is thrown containing the activity ID for manual follow-up. |
| `PassThru` | `switch` | No | `$false` | Returns the execution response object to the pipeline. |

### Output

By default, writes status messages to the host. When `-PassThru` is specified, returns a `PSCustomObject` with the following properties:

| Property | Type | Description |
|----------|------|-------------|
| `ActivityId` | `int` | ID of the activity created for tracking the execution |
| `TaskId` | `string` | ID of the queued worker task |

### Examples

```powershell title="Start a Run Profile by ID"
Start-JIMRunProfile -RunProfileId 42
```

```powershell title="Start by name"
Start-JIMRunProfile -ConnectedSystemName "Active Directory" -RunProfileName "Full Import"
```

```powershell title="Start and wait for completion"
Start-JIMRunProfile -RunProfileId 42 -Wait
```

```powershell title="Start with a timeout"
Start-JIMRunProfile -RunProfileId 42 -Wait -Timeout 300
```

```powershell title="Capture execution details"
$result = Start-JIMRunProfile -RunProfileId 42 -PassThru
Write-Host "Activity ID: $($result.ActivityId)"
Write-Host "Task ID: $($result.TaskId)"
```

```powershell title="Pipeline: start all full imports for a Connected System"
Get-JIMRunProfile -ConnectedSystemId 1 |
    Where-Object { $_.Type -eq "FullImport" } |
    ForEach-Object { Start-JIMRunProfile -RunProfileId $_.Id -Wait }
```

```powershell title="Automation with error handling"
try {
    Start-JIMRunProfile -RunProfileId 42 -Wait -Timeout 600 -PassThru -ErrorAction Stop
} catch {
    Write-Error "Run Profile execution failed or timed out: $_"
}
```

### Notes

- The Run Profile is queued as an asynchronous task on the JIM worker service. Without `-Wait`, the cmdlet returns immediately after the task is queued.
- When `-Wait` is specified, the cmdlet polls the activity status every 2 seconds and displays a progress bar. If authentication tokens expire during polling, the cmdlet retries up to 3 times before failing.
- If `-Timeout` is exceeded, the cmdlet throws a terminating error that includes the activity ID, allowing you to check progress manually via [Get-JIMActivity](activities.md) or the JIM web interface.
- Run Profiles that are already executing will be rejected by the server; you do not need to check for running profiles before calling this cmdlet.

---

## See also

- [Run Profiles](../configuration/run-profiles.md): what Run Profiles are, run types, and how they fit alongside schedules and activities
- [Activities](activities.md): cmdlets for monitoring and inspecting activity execution history
- [Connected Systems](connected-systems.md): cmdlets for managing Connected Systems
