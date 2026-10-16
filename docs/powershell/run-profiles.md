---
title: Run Profiles
description: "The cmdlets that list, create, change, remove and run a Connected System's Run Profiles."
type: reference
audience: automation-author
---

# Run Profiles

Run Profile cmdlets manage and execute synchronisation Run Profiles on Connected Systems. A Run Profile defines a specific operation type (import, synchronisation, or export) that can be executed against a Connected System. Use these cmdlets to list, create, modify, remove, and trigger Run Profiles. [Run Profiles](../configuration/run-profiles.md) explains the settings, including the [Safeguards](../configuration/run-profiles.md#safeguards) and [Allow Object Type Changes](../configuration/run-profiles.md#allow-object-type-changes-full-import).

---

## Get-JIMRunProfile

Retrieves one or more Run Profiles for a Connected System, identified either by Connected System ID or Connected System name, with an optional name filter.

<!-- BEGIN GENERATED: Get-JIMRunProfile -->
### Syntax

```powershell
# ById (default)
Get-JIMRunProfile -ConnectedSystemId <int> [-Name <string>]

# ByName
Get-JIMRunProfile -ConnectedSystemName <string> [-Name <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (ById) |  | The Connected System whose Run Profiles to get. Accepts pipeline input by property name, `ConnectedSystemId` or `Id`. |
| `ConnectedSystemName` | `string` | Yes (ByName) |  | The name of the Connected System whose Run Profiles to get. Must be an exact match. |
| `Name` | `string` | No |  | Return only the Run Profiles whose name matches, for example "Full*". Supports wildcards. |

### Output

Each Run Profile on the Connected System, or each one whose name matches `-Name`.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The unique identifier of the Run Profile. |
| `Name` | `string` | The user-supplied name for this Run Profile. |
| `ConnectedSystemId` | `int` | The Connected System this Run Profile belongs to. |
| `RunType` | `string` | The type of synchronisation operation (FullImport, DeltaImport, FullSynchronisation, DeltaSynchronisation, Export). One of `NotSet`, `FullImport`, `DeltaImport`, `FullSynchronisation`, `DeltaSynchronisation` or `Export`. |
| `PageSize` | `int` | How many items to process in one batch. |
| `PartitionName` | `string` | The partition name if this Run Profile targets a specific partition. |
| `TargetsDeselectedPartition` | `bool` | True when this Run Profile targets a partition that is no longer selected on the Connected System, which makes the Run Profile inoperable: a deselected partition is not managed by JIM, so executing this Run Profile is refused rather than reading scope the administrator has withdrawn. |
| `FilePath` | `string` | File path for file-based connectors. |
| `VerifyImportContentHashes` | `bool` | Whether Verification Mode is enabled: the Full Import skips no object whose content looks unchanged, and instead raises an error for any object whose stored content hash disagrees with what was read. Only meaningful when `RunType` is `FullImport`. |
| `AllowObjectTypeChanges` | `bool` | Whether this Full Import carries out an Object Type change rather than refusing it. Only ever true when `RunType` is `FullImport`, and only has an effect on a Connected System whose Connector can tell an Object Type change from a new object, as the LDAP Connector can. |
| `Safeguards` | `object` | The limits on what this Run Profile may attempt. Always present. It has `MaxCreates`, `MaxUpdates`, `MaxDeletes`, `MaxDetectedDeletions` and `MaxDetectedDeletionsPercent`. |
<!-- END GENERATED: Get-JIMRunProfile -->

`Safeguards` carries `MaxCreates`, `MaxUpdates` and `MaxDeletes` for an Export Run Profile, and `MaxDetectedDeletions` and `MaxDetectedDeletionsPercent` for a Full Import or Delta Import; each is empty when that limit is not set.

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

Creates a new Run Profile on a Connected System.

<!-- BEGIN GENERATED: New-JIMRunProfile -->
### Syntax

```powershell
# ById (default)
New-JIMRunProfile [-Name] <string> -ConnectedSystemId <int> -RunType <string> [-PageSize <int>] [-PartitionId <int>] [-FilePath <string>] [-VerifyImportContentHashes] [-AllowObjectTypeChanges] [-MaxCreates <int>] [-MaxUpdates <int>] [-MaxDeletes <int>] [-MaxDetectedDeletions <int>] [-MaxDetectedDeletionsPercent <int>] [-PassThru] [-WhatIf] [-Confirm]

# ByName
New-JIMRunProfile [-Name] <string> -ConnectedSystemName <string> -RunType <string> [-PageSize <int>] [-PartitionId <int>] [-FilePath <string>] [-VerifyImportContentHashes] [-AllowObjectTypeChanges] [-MaxCreates <int>] [-MaxUpdates <int>] [-MaxDeletes <int>] [-MaxDetectedDeletions <int>] [-MaxDetectedDeletionsPercent <int>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (ById) |  | The Connected System to create the Run Profile on. Accepts pipeline input by property name, `ConnectedSystemId` or `Id`. |
| `ConnectedSystemName` | `string` | Yes (ByName) |  | The name of the Connected System to create the Run Profile on. Must be an exact match. |
| `Name` | `string` | Yes |  | The name for the Run Profile. Can be given by position, as the first argument. |
| `RunType` | `string` | Yes |  | The operation the Run Profile carries out: an import from the Connected System, a synchronisation with the Metaverse, or an export to the Connected System. One of `FullImport`, `DeltaImport`, `FullSynchronisation`, `DeltaSynchronisation` or `Export`. |
| `PageSize` | `int` | No | `100` | How many items to process in one batch. From 1 to 10,000. |
| `PartitionId` | `int` | No |  | The partition the Run Profile targets, for a Connector with partitions. Omit it and the Run Profile follows every selected partition. |
| `FilePath` | `string` | No |  | The file to read or write, for a file-based Connector. |
| `VerifyImportContentHashes` | `switch` | No |  | Enables Verification Mode: the Full Import skips no object whose content looks unchanged, and raises an error for any object whose stored content hash disagrees with what was read. Only valid when `-RunType` is FullImport. Use it to check the skip, and leave it off for normal, faster Full Imports. |
| `AllowObjectTypeChanges` | `switch` | No |  | Lets this Full Import carry out an Object Type change rather than refusing it: an object that arrives under a different Object Type is imported as a new Connected System Object, and the old one is retired. Only valid when `-RunType` is FullImport, and only has an effect for a Connector that can tell an Object Type change from a new object, as the LDAP Connector can. |
| `MaxCreates` | `int` | No |  | A Safeguard: the most creates that may be pending for an Export run to attempt any of them. If more are pending when the run starts, it attempts none of them; 0 refuses creates outright. Only valid when `-RunType` is Export; omit it for no limit. |
| `MaxUpdates` | `int` | No |  | A Safeguard: the most updates that may be pending for an Export run to attempt any of them, under the same rules as `-MaxCreates`. |
| `MaxDeletes` | `int` | No |  | A Safeguard: the most deletes that may be pending for an Export run to attempt any of them, under the same rules as `-MaxCreates`. Recommended for an Export to a production directory. |
| `MaxDetectedDeletions` | `int` | No |  | A Safeguard: the most Connected System Objects an import run may newly mark as deleted: those a Full Import no longer finds, or those a Delta Import's Connector reports deleted. If more would be, it marks none of them; 0 refuses to mark any. A Delta Import that withholds its deletions reports them again on every later run until they are let through. Only valid when `-RunType` is FullImport or DeltaImport; omit it for no limit. |
| `MaxDetectedDeletionsPercent` | `int` | No |  | A Safeguard: the most Connected System Objects an import run may newly mark as deleted, as a share (0 to 100) of those in the run's scope before it saves anything, under the same rules as `-MaxDetectedDeletions`. Either limit tripping withholds every deletion in the run. |
| `PassThru` | `switch` | No |  | Returns the created Run Profile. |

### Output

With `-PassThru`, the created Run Profile; otherwise nothing.

Properties as under [Get-JIMRunProfile](#get-jimrunprofile).
<!-- END GENERATED: New-JIMRunProfile -->

### Examples

```powershell title="Create a full import Run Profile"
New-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import" -RunType FullImport
```

```powershell title="Create a Run Profile by Connected System name"
New-JIMRunProfile -ConnectedSystemName "Active Directory" -Name "Delta Synchronisation" -RunType DeltaSynchronisation
```

```powershell title="Create and capture the result"
$rp = New-JIMRunProfile -ConnectedSystemId 1 -Name "Export" -RunType Export -PassThru
$rp.Id
```

```powershell title="Create a Run Profile with a custom page size"
New-JIMRunProfile -ConnectedSystemId 1 -Name "Delta Import" -RunType DeltaImport -PageSize 500 -PassThru
```

```powershell title="Create a partition-scoped Run Profile"
New-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import (UK)" -RunType FullImport -PartitionId 3
```

```powershell title="Create a Run Profile for a file-based connector"
Get-JIMConnectedSystem -Name "CSV*" | ForEach-Object {
    New-JIMRunProfile -ConnectedSystemId $_.Id -Name "Full Import" -RunType FullImport -FilePath "/connector-files/import.csv"
}
```

```powershell title="Create a Full Import Run Profile with Verification Mode enabled"
New-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import (Verified)" -RunType FullImport -VerifyImportContentHashes
```

```powershell title="Create a hand-run Full Import Run Profile that carries out Object Type changes"
New-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import (Object Type changes)" -RunType FullImport -AllowObjectTypeChanges
```

```powershell title="Preview with WhatIf"
New-JIMRunProfile -ConnectedSystemId 1 -Name "Delta Import" -RunType DeltaImport -WhatIf
```

```powershell title="Create an Export Run Profile with a delete limit"
New-JIMRunProfile -ConnectedSystemId 1 -Name "Export" -RunType Export -MaxDeletes 100
```

```powershell title="Create a Full Import Run Profile with a deletion detection share limit"
New-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import" -RunType FullImport -MaxDetectedDeletionsPercent 10
```

```powershell title="Create a Delta Import Run Profile that withholds more than 50 reported deletions"
New-JIMRunProfile -ConnectedSystemId 1 -Name "Delta Import" -RunType DeltaImport -MaxDetectedDeletions 50
```

---

## Set-JIMRunProfile

Modifies an existing Run Profile. Only the settings you give change.

<!-- BEGIN GENERATED: Set-JIMRunProfile -->
### Syntax

```powershell
# ById (default)
Set-JIMRunProfile -ConnectedSystemId <int> -RunProfileId <int> [-Name <string>] [-PageSize <int>] [-PartitionId <int>] [-FilePath <string>] [-VerifyImportContentHashes <bool>] [-AllowObjectTypeChanges <bool>] [-MaxCreates <int>] [-MaxUpdates <int>] [-MaxDeletes <int>] [-MaxDetectedDeletions <int>] [-MaxDetectedDeletionsPercent <int>] [-PassThru] [-WhatIf] [-Confirm]

# ByName
Set-JIMRunProfile -ConnectedSystemName <string> -RunProfileId <int> [-Name <string>] [-PageSize <int>] [-PartitionId <int>] [-FilePath <string>] [-VerifyImportContentHashes <bool>] [-AllowObjectTypeChanges <bool>] [-MaxCreates <int>] [-MaxUpdates <int>] [-MaxDeletes <int>] [-MaxDetectedDeletions <int>] [-MaxDetectedDeletionsPercent <int>] [-PassThru] [-WhatIf] [-Confirm]

# ByInputObject
Set-JIMRunProfile -InputObject <PSCustomObject> [-Name <string>] [-PageSize <int>] [-PartitionId <int>] [-FilePath <string>] [-VerifyImportContentHashes <bool>] [-AllowObjectTypeChanges <bool>] [-MaxCreates <int>] [-MaxUpdates <int>] [-MaxDeletes <int>] [-MaxDetectedDeletions <int>] [-MaxDetectedDeletionsPercent <int>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (ById) |  | The Connected System the Run Profile belongs to. |
| `ConnectedSystemName` | `string` | Yes (ByName) |  | The name of the Connected System the Run Profile belongs to. Must be an exact match. |
| `RunProfileId` | `int` | Yes (ById, ByName) |  | The Run Profile to update. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | The Run Profile to update, as `Get-JIMRunProfile` returns it. Accepts pipeline input. |
| `Name` | `string` | No |  | The new name. |
| `PageSize` | `int` | No |  | How many items to process in one batch. From 1 to 10,000. |
| `PartitionId` | `int` | No |  | The partition the Run Profile targets, for a Connector with partitions. |
| `FilePath` | `string` | No |  | The file to read or write, for a file-based Connector. |
| `VerifyImportContentHashes` | `bool` | No |  | Whether Verification Mode is enabled: `$true` to enable it, `$false` to disable it. Only a Full Import Run Profile accepts `$true`. |
| `AllowObjectTypeChanges` | `bool` | No |  | Whether this Full Import carries out an Object Type change rather than refusing it: `$true` to allow it, `$false` to go back to refusing it. Only a Full Import Run Profile accepts `$true`. |
| `MaxCreates` | `int` | No |  | A Safeguard: the most creates that may be pending for an Export run to attempt any of them. 0 refuses creates outright, and `$null` clears the limit. Only valid on an Export Run Profile. The Safeguards you do not give keep their values. |
| `MaxUpdates` | `int` | No |  | A Safeguard: the most updates that may be pending for an Export run to attempt any of them, under the same rules as `-MaxCreates`. |
| `MaxDeletes` | `int` | No |  | A Safeguard: the most deletes that may be pending for an Export run to attempt any of them, under the same rules as `-MaxCreates`. |
| `MaxDetectedDeletions` | `int` | No |  | A Safeguard: the most Connected System Objects an import run may newly mark as deleted: those a Full Import no longer finds, or those a Delta Import's Connector reports deleted. 0 refuses to mark any, and `$null` clears the limit. Only valid on a Full Import or Delta Import Run Profile. The Safeguards you do not give keep their values. |
| `MaxDetectedDeletionsPercent` | `int` | No |  | A Safeguard: the most Connected System Objects an import run may newly mark as deleted, as a share (0 to 100) of those in the run's scope before it saves anything, under the same rules as `-MaxDetectedDeletions`. |
| `PassThru` | `switch` | No |  | Returns the updated Run Profile. |

### Output

With `-PassThru`, the updated Run Profile; otherwise nothing.

Properties as under [Get-JIMRunProfile](#get-jimrunprofile).
<!-- END GENERATED: Set-JIMRunProfile -->

### Examples

```powershell title="Rename a Run Profile"
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Name "Full Import (Production)"
```

```powershell title="Update by Connected System name"
Set-JIMRunProfile -ConnectedSystemName "Contoso AD" -RunProfileId 1 -PageSize 500
```

```powershell title="Update via pipeline"
Get-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import" | Set-JIMRunProfile -Name "Full Import (Renamed)" -PassThru
```

```powershell title="Change partition assignment"
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -PartitionId 5
```

```powershell title="Update a file-based connector's Run Profile"
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 7 -FilePath "/connector-files/import-v2.csv"
```

```powershell title="Enable Verification Mode temporarily"
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -VerifyImportContentHashes $true
```

```powershell title="Disable Verification Mode again"
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -VerifyImportContentHashes $false
```

```powershell title="Stop a Run Profile carrying out Object Type changes"
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 43 -AllowObjectTypeChanges $false
```

```powershell title="Preview changes with WhatIf"
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Name "New Name" -WhatIf
```

```powershell title="Set a delete limit on an Export Run Profile"
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 12 -MaxDeletes 100
```

```powershell title="Clear a limit, leaving the other two unchanged"
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 12 -MaxDeletes $null
```

```powershell title="Set a deletion detection share limit on a Full Import Run Profile"
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 7 -MaxDetectedDeletionsPercent 10
```

```powershell title="Set a deletion limit on a Delta Import Run Profile"
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 8 -MaxDetectedDeletions 50
```

```powershell title="Clear both deletion detection limits"
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 7 -MaxDetectedDeletions $null -MaxDetectedDeletionsPercent $null
```

---

## Remove-JIMRunProfile

Deletes a Run Profile.

<!-- BEGIN GENERATED: Remove-JIMRunProfile -->
### Syntax

```powershell
# ById (default)
Remove-JIMRunProfile -ConnectedSystemId <int> -RunProfileId <int> [-Force] [-PassThru] [-WhatIf] [-Confirm]

# ByName
Remove-JIMRunProfile -ConnectedSystemName <string> -RunProfileId <int> [-Force] [-PassThru] [-WhatIf] [-Confirm]

# ByInputObject
Remove-JIMRunProfile -InputObject <PSCustomObject> [-Force] [-PassThru] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (ById) |  | The Connected System the Run Profile belongs to. |
| `ConnectedSystemName` | `string` | Yes (ByName) |  | The name of the Connected System the Run Profile belongs to. Must be an exact match. |
| `RunProfileId` | `int` | Yes (ById, ByName) |  | The Run Profile to delete. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | The Run Profile to delete, as `Get-JIMRunProfile` returns it. Accepts pipeline input. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. |
| `PassThru` | `switch` | No |  | Returns the deleted Run Profile. |

### Output

With `-PassThru`, the deleted Run Profile, as it was before the deletion; otherwise nothing.

Properties as under [Get-JIMRunProfile](#get-jimrunprofile).
<!-- END GENERATED: Remove-JIMRunProfile -->

### Examples

```powershell title="Delete a Run Profile by ID"
Remove-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42
```

```powershell title="Delete without confirmation"
Remove-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Force
```

```powershell title="Delete using Connected System name"
Remove-JIMRunProfile -ConnectedSystemName "Contoso AD" -RunProfileId 42 -Force
```

```powershell title="Delete via pipeline"
Get-JIMRunProfile -ConnectedSystemId 1 -Name "Test Profile" | Remove-JIMRunProfile -Force
```

```powershell title="Delete all Run Profiles for a Connected System"
Get-JIMRunProfile -ConnectedSystemId 1 | Remove-JIMRunProfile -Force
```

```powershell title="Capture before deleting"
$deleted = Remove-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Force -PassThru
Write-Host "Removed Run Profile: $($deleted.Name)"
```

---

## Start-JIMRunProfile

Queues a Run Profile for execution on the JIM worker service. The operation is asynchronous by default; use `-Wait` to block until execution completes.

Every parameter set requires both a Connected System identifier (`-ConnectedSystemId` or `-ConnectedSystemName`) and a Run Profile identifier (`-RunProfileId` or `-RunProfileName`); a Run Profile identifier alone does not resolve to a parameter set.

<!-- BEGIN GENERATED: Start-JIMRunProfile -->
### Syntax

```powershell
# ById (default)
Start-JIMRunProfile -ConnectedSystemId <int> -RunProfileId <int> [-Wait] [-Timeout <int>] [-PreviewActivityId <guid>] [-PassThru]

# ByIdAndName
Start-JIMRunProfile -ConnectedSystemId <int> -RunProfileName <string> [-Wait] [-Timeout <int>] [-PreviewActivityId <guid>] [-PassThru]

# ByNameAndId
Start-JIMRunProfile -ConnectedSystemName <string> -RunProfileId <int> [-Wait] [-Timeout <int>] [-PreviewActivityId <guid>] [-PassThru]

# ByName
Start-JIMRunProfile -ConnectedSystemName <string> -RunProfileName <string> [-Wait] [-Timeout <int>] [-PreviewActivityId <guid>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (ById, ByIdAndName) |  | The Connected System the Run Profile belongs to. Accepts pipeline input by property name. |
| `ConnectedSystemName` | `string` | Yes (ByNameAndId, ByName) |  | The name of the Connected System the Run Profile belongs to. Must be an exact match. |
| `RunProfileId` | `int` | Yes (ById, ByNameAndId) |  | The Run Profile to execute. Accepts pipeline input by property name, `RunProfileId` or `Id`. |
| `RunProfileName` | `string` | Yes (ByIdAndName, ByName) |  | The name of the Run Profile to execute. Must be an exact match. |
| `Wait` | `switch` | No |  | Waits for the execution to finish, showing its progress: the step it is on, object counts, throughput and the estimated time remaining. |
| `Timeout` | `int` | No |  | How many seconds `-Wait` waits before it throws an error naming the Activity, which may still be running. Omit it to wait until the execution finishes. At least 1. |
| `PreviewActivityId` | `guid` | No |  | The Full Synchronisation preview this run was started after reading, recorded on the run's Activity so that it shows what informed the run. Only a Full Synchronisation Run Profile can cite one, and it must be a completed Full Synchronisation preview of the same Connected System; anything else is refused and the run is not queued. A preview that a later run or configuration change has overtaken is still recorded, and the cmdlet warns what overtook it. |
| `PassThru` | `switch` | No |  | Returns the response to queuing the run. |

### Output

With `-PassThru`, the response to queuing the run; `Get-JIMActivity -Follow` follows its ActivityId. Otherwise nothing. Warnings in the response are also written as PowerShell warnings.

| Property | Type | Description |
|----------|------|-------------|
| `ActivityId` | `guid` | The Activity that records the execution, and reports its progress and outcome. |
| `TaskId` | `guid` | The worker task queued to carry out the execution. |
| `Message` | `string` | Message describing the result. |
| `Warnings` | `string[]` | Warnings about the execution, such as a partition validation warning, or that the preview it cites was out of date. Empty when there are none. |
<!-- END GENERATED: Start-JIMRunProfile -->

### Examples

```powershell title="Start a Run Profile by ID"
Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42
```

```powershell title="Start by name"
Start-JIMRunProfile -ConnectedSystemName "Active Directory" -RunProfileName "Full Import"
```

```powershell title="Start and wait for completion"
Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Wait
```

```powershell title="Start with a timeout"
Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Wait -Timeout 300
```

```powershell title="Preview a Full Synchronisation, then run it and record the preview"
$preview = New-JIMConfigurationChangePreview -ConnectedSystemId 1 -FullSynchronisation -Wait
$preview.ImpactCounts | Format-Table TransitionType, ObjectCount
if ($preview.IsComplete) {
    Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileName "Full Synchronisation" -PreviewActivityId $preview.ActivityId -Wait
}
```

```powershell title="Capture execution details"
$result = Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -PassThru
Write-Host "Activity ID: $($result.ActivityId)"
Write-Host "Task ID: $($result.TaskId)"
if ($result.Warnings.Count -gt 0) {
    Write-Warning "Warnings: $($result.Warnings -join '; ')"
}
```

```powershell title="Pipeline: start all full imports for a Connected System"
Get-JIMRunProfile -ConnectedSystemId 1 |
    Where-Object { $_.RunType -eq "FullImport" } |
    Start-JIMRunProfile -Wait
```

```powershell title="Automation with error handling"
try {
    Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Wait -Timeout 600 -PassThru -ErrorAction Stop
} catch {
    Write-Error "Run Profile execution failed or timed out: $_"
}
```

### Notes

- The Run Profile is queued as an asynchronous task on the JIM worker service. Without `-Wait`, the cmdlet returns immediately after the task is queued.
- When `-Wait` is specified, the cmdlet polls the lightweight Activity progress endpoint (`/activities/{id}/progress`) every 2 seconds and displays a progress bar with the current phase, object counts, throughput and estimated time remaining. If authentication tokens expire during polling, the cmdlet retries up to 3 times before failing.
- If `-Timeout` is exceeded, the cmdlet throws a terminating error that includes the Activity ID, allowing you to check progress manually via [Get-JIMActivity](activities.md) or the JIM web interface.
- Run Profiles that are already executing will be rejected by the server; you do not need to check for running profiles before calling this cmdlet.

---

## See also

- [Run Profiles](../configuration/run-profiles.md): what Run Profiles are, run types, and how they fit alongside schedules and activities
- [Activities](activities.md): cmdlets for monitoring and inspecting activity execution history
- [Connected Systems](connected-systems.md): cmdlets for managing Connected Systems
