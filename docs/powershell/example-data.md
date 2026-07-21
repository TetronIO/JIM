---
title: Example Data
---

# Example Data

Cmdlets for generating sample identity data for testing and evaluation purposes. Example data sets and templates allow you to populate the metaverse with realistic test identities without requiring a live Connected System.

---

## Get-JIMExampleDataSet

Retrieves available example data sets. Each data set is a named pool of string values (e.g. a list of cities, or first names) that Data Generation Templates can draw from.

### Syntax

```powershell
# List (default)
Get-JIMExampleDataSet [-Page <int>] [-PageSize <int>]

# ById
Get-JIMExampleDataSet -Id <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById set) | | The ID of a specific data set to retrieve, including its values. |
| `Page` | `int` | No | `1` | Page number for paginated results. |
| `PageSize` | `int` | No | `100` | Number of results per page (maximum 1000). |

### Output

Returns one or more `PSCustomObject` instances representing example data sets.

### Examples

```powershell title="List all example data sets"
Get-JIMExampleDataSet
```

```powershell title="Get a specific data set, including its values"
Get-JIMExampleDataSet -Id 5
```

```powershell title="List data sets with pagination"
Get-JIMExampleDataSet -Page 2 -PageSize 50
```

```powershell title="Select specific properties"
Get-JIMExampleDataSet | Select-Object Name, Culture, ValueCount
```

---

## New-JIMExampleDataSet

Creates a new Example Data Set.

### Syntax

```powershell
New-JIMExampleDataSet -Name <string> -Culture <string> [-Values <string[]>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes | | The name for the data set. |
| `Culture` | `string` | Yes | | The .NET culture the values are in, e.g. `en-GB`. |
| `Values` | `string[]` | No | | The string values that make up this data set. |
| `PassThru` | `switch` | No | `$false` | Returns the created data set object. |

### Output

If `-PassThru` is specified, returns the created Example Data Set object.

### Examples

```powershell title="Create a data set of UK city names"
New-JIMExampleDataSet -Name "UK Cities" -Culture "en-GB" -Values "London", "Manchester", "Bristol" -PassThru
```

---

## Set-JIMExampleDataSet

Updates an existing Example Data Set. Built-in data sets cannot be updated.

### Syntax

```powershell
Set-JIMExampleDataSet -Id <int> [-Name <string>] [-Culture <string>] [-Values <string[]>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes | | The ID of the data set to update. Accepts pipeline input. |
| `Name` | `string` | No | | A new name for the data set. |
| `Culture` | `string` | No | | A new .NET culture for the values. |
| `Values` | `string[]` | No | | When specified, replaces the entire set of values. |
| `PassThru` | `switch` | No | `$false` | Returns the updated data set object. |

### Output

If `-PassThru` is specified, returns the updated Example Data Set object.

### Examples

```powershell title="Rename a data set"
Set-JIMExampleDataSet -Id 5 -Name "UK Cities (Extended)"
```

```powershell title="Replace a data set's values"
Set-JIMExampleDataSet -Id 5 -Values "London", "Manchester", "Bristol", "Leeds" -PassThru
```

---

## Remove-JIMExampleDataSet

Deletes an Example Data Set. Built-in data sets cannot be removed. This action cannot be undone.

### Syntax

```powershell
Remove-JIMExampleDataSet -Id <int> [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes | | The ID of the data set to remove. Accepts pipeline input. |
| `Force` | `switch` | No | `$false` | Bypasses confirmation prompts. |

### Output

None.

### Examples

```powershell title="Remove a data set with confirmation"
Remove-JIMExampleDataSet -Id 5
```

```powershell title="Remove a data set without confirmation"
Remove-JIMExampleDataSet -Id 5 -Force
```

---

## Get-JIMExampleDataTemplate

Retrieves data generation templates that define how test data should be generated. Templates specify object types, attribute patterns, and generation rules used when creating sample identity data.

### Syntax

```powershell
# List (default)
Get-JIMExampleDataTemplate [-Page <int>] [-PageSize <int>]

# ById
Get-JIMExampleDataTemplate -Id <int>

# ByName
Get-JIMExampleDataTemplate -Name <string>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById set) | | The ID of a specific template to retrieve. Accepts pipeline input. |
| `Name` | `string` | Yes (ByName set) | | The name of a specific template to retrieve. |
| `Page` | `int` | No (List set) | `1` | Page number for paginated results. |
| `PageSize` | `int` | No (List set) | `100` | Number of results per page (maximum 1000). |

### Output

Returns one or more `PSCustomObject` instances representing data generation templates. The list form returns `Id`, `Name`, `BuiltIn`, `Created`, and `ObjectTypeCount`; retrieving a single template by ID or name returns the full template including its Object Types.

### Examples

```powershell title="List all templates"
Get-JIMExampleDataTemplate
```

```powershell title="Get a specific template by ID"
Get-JIMExampleDataTemplate -Id 3
```

```powershell title="Get a template by name"
Get-JIMExampleDataTemplate -Name "UK Organisation"
```

```powershell title="Page through templates"
Get-JIMExampleDataTemplate -Page 1 -PageSize 10
```

---

## Invoke-JIMExampleDataTemplate

Executes a data generation template to create identity objects in the metaverse. Execution is asynchronous: the cmdlet returns as soon as the server has accepted the request, before data generation has finished. Monitor progress and completion via Activities ([`Get-JIMActivity`](activities.md)).

Supports `ShouldProcess`, so you can use `-WhatIf` or `-Confirm` to preview or confirm execution before it begins.

### Syntax

```powershell
# ById (default)
Invoke-JIMExampleDataTemplate -Id <int> [-PassThru] [-WhatIf] [-Confirm]

# ByName
Invoke-JIMExampleDataTemplate -Name <string> [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById set) | | The ID of the template to execute. Accepts pipeline input. |
| `Name` | `string` | Yes (ByName set) | | The name of the template to execute. |
| `PassThru` | `switch` | No | `false` | Return execution information to the pipeline. |
| `WhatIf` | `switch` | No | | Preview the operation without executing it. |
| `Confirm` | `switch` | No | | Prompt for confirmation before executing. |

### Output

By default, this cmdlet produces no output. When `-PassThru` is specified, returns a `PSCustomObject` with `TemplateId`, `Status` and `Message` properties confirming the request was accepted; it does not report completion, since the operation is still running asynchronously on the server at that point.

### Examples

```powershell title="Execute a template by ID"
Invoke-JIMExampleDataTemplate -Id 3
```

```powershell title="Execute a template by name"
Invoke-JIMExampleDataTemplate -Name "UK Organisation"
```

```powershell title="Execute and capture execution information"
$result = Invoke-JIMExampleDataTemplate -Id 3 -PassThru
$result
```

```powershell title="Pipeline from Get-JIMExampleDataTemplate"
Get-JIMExampleDataTemplate -Name "UK Organisation" |
    Invoke-JIMExampleDataTemplate -PassThru
```

```powershell title="Preview without executing"
Invoke-JIMExampleDataTemplate -Id 3 -WhatIf
```

```powershell title="Execute, then monitor progress via Activities"
Invoke-JIMExampleDataTemplate -Id 3
Get-JIMActivity -Page 1 -PageSize 10
```

---

## See also

- [Metaverse](metaverse.md): cmdlets for managing the metaverse schema and querying identity objects
- [Activities](activities.md): cmdlets for reviewing activity history and execution results
