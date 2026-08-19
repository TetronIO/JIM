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
New-JIMExampleDataSet -Name <string> -Culture <string> [-Values <string[]>] [-ChangeReason <string>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes | | The name for the data set. |
| `Culture` | `string` | Yes | | The .NET culture the values are in, e.g. `en-GB`. |
| `Values` | `string[]` | No | | The string values that make up this data set. |
| `ChangeReason` | `string` | No | | Reason for the change, recorded on the audit Activity and shown in the data set's configuration change history. |
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
Set-JIMExampleDataSet -Id <int> [-Name <string>] [-Culture <string>] [-Values <string[]>] [-ChangeReason <string>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes | | The ID of the data set to update. Accepts pipeline input. |
| `Name` | `string` | No | | A new name for the data set. |
| `Culture` | `string` | No | | A new .NET culture for the values. |
| `Values` | `string[]` | No | | When specified, replaces the entire set of values. |
| `ChangeReason` | `string` | No | | Reason for the change, recorded on the audit Activity and shown in the data set's configuration change history. |
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
Remove-JIMExampleDataSet -Id <int> [-ChangeReason <string>] [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes | | The ID of the data set to remove. Accepts pipeline input. |
| `ChangeReason` | `string` | No | | Reason for the change, recorded on the audit Activity and shown in the data set's configuration change history. |
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

## New-JIMExampleDataTemplate

Creates a Data Generation Template: a definition of which Metaverse Object Types to generate test objects for, how many of each, and how each attribute's values are generated.

Supports `ShouldProcess`, so you can use `-WhatIf` or `-Confirm` to preview or confirm creation.

### Syntax

```powershell
New-JIMExampleDataTemplate -Name <string> -ObjectType <hashtable[]> [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes | | The name for the Data Generation Template. |
| `ObjectType` | `hashtable[]` | Yes | | One hashtable per Object Type the template generates objects for. See the hashtable shape below. |
| `ChangeReason` | `string` | No | | Reason for the change, recorded on the audit Activity and shown in the template's configuration change history. |
| `PassThru` | `switch` | No | `$false` | Returns the created Data Generation Template object. |
| `WhatIf` | `switch` | No | | Preview the operation without performing it. |
| `Confirm` | `switch` | No | | Prompt for confirmation before creating. |

#### `-ObjectType` hashtable keys

| Key | Type | Required | Description |
|-----|------|----------|-------------|
| `MetaverseObjectType` | `string` or `int` | Yes | The Metaverse Object Type to generate objects for, by name or ID. |
| `ObjectsToCreate` | `int` | No (default `1`) | How many objects to generate for this Object Type. |
| `Attributes` | `hashtable[]` | No | One hashtable per attribute whose values are generated. See the attribute keys below. |

#### Attribute hashtable keys

| Key | Type | Description |
|-----|------|-------------|
| `MetaverseAttribute` | `string` or `int` | The Metaverse Attribute values are generated for, by name or ID. |
| `ConnectedSystemObjectTypeAttributeId` | `int` | The Connected System attribute values are generated for, when targeting a Connected System instead of the metaverse. |
| `Pattern` | `string` | A variable-replacement pattern for constructing string values, e.g. `{0}.{1}@contoso.com`. |
| `Expression` | `string` | An expression constructing the value from already-generated attributes via `mv["Attribute Name"]`. |
| `ExampleDataSets` | `object[]` | The Example Data Sets values are drawn from, by name or ID. Order follows array order, which is what index-based patterns refer to. |
| `WeightedStringValues` | `hashtable[]` | Specific values to choose from, weighted: `@{ Value = "active"; Weight = 0.9 }`. |
| `PopulatedValuesPercentage` | `int` | What percentage of generated objects receive a value for this attribute. |
| `BoolTrueDistribution` | `int` | For boolean attributes, what percentage of values are true. |
| `BoolShouldBeRandom` | `bool` | For boolean attributes, whether values are generated randomly. |
| `MinDate` / `MaxDate` | `datetime` | For date attributes, the earliest and latest values to generate. |
| `MinNumber` / `MaxNumber` | `int` | For number attributes, the smallest and largest values to generate. |
| `SequentialNumbers` / `RandomNumbers` | `bool` | For number attributes, whether values are generated sequentially or randomly. |
| `ManagerDepthPercentage` | `int` | For Manager attributes, how far into the organisational hierarchy managers are present. |
| `MvaRefMinAssignments` / `MvaRefMaxAssignments` | `int` | For multi-valued reference attributes, the minimum and maximum number of values to assign. |
| `ReferenceMetaverseObjectTypeIds` | `int[]` | For reference attributes, the Metaverse Object Types generated references may point at. |
| `AttributeDependency` | `hashtable` | A condition that must hold for the attribute to be generated: `@{ MetaverseAttribute = <name or id>; ComparisonType = "Equals"; StringValue = "Contractor" }`. Valid comparison types are `Equals`, `NotEquals`, `LessThan`, `GreaterThan`, `GreaterThanOrEqual`, `LessThanOrEqual` and `Like`. |

Any other key in an `-ObjectType` or attribute hashtable throws, so a mis-typed key is caught before anything is sent.

### Output

If `-PassThru` is specified, returns the created Data Generation Template object: `Id`, `Name`, `BuiltIn`, `Created`, `CreatedByName`, `LastUpdated`, `LastUpdatedByName`, and `ObjectTypes` (each with `MetaverseObjectTypeId`, `MetaverseObjectTypeName`, `ObjectsToCreate` and `TemplateAttributes`).

### Examples

```powershell title="Create a template generating 500 Users"
New-JIMExampleDataTemplate -Name "Demo Users" -ObjectType @{
    MetaverseObjectType = "User"
    ObjectsToCreate     = 500
    Attributes          = @(
        @{
            MetaverseAttribute = "Display Name"
            Pattern            = "{0} {1}"
            ExampleDataSets    = @("Firstnames Female", "Lastnames")
        }
    )
}
```

```powershell title="Create a template for two Object Types, recording a change reason"
New-JIMExampleDataTemplate -Name "Demo Organisation" -ObjectType @(
    @{ MetaverseObjectType = "User"; ObjectsToCreate = 1000 },
    @{ MetaverseObjectType = "Group"; ObjectsToCreate = 50 }
) -ChangeReason "Seeding evaluation data (CHG0200)" -PassThru
```

---

## Set-JIMExampleDataTemplate

Renames a Data Generation Template and/or replaces its Object Type configuration. Built-in templates cannot be updated.

Supplying `-ObjectType` **replaces the template's entire Object Type graph**: every Object Type and attribute configuration not present in the supplied hashtables is removed. To add a single attribute without restating the rest, use [`Add-JIMExampleDataTemplateAttribute`](#add-jimexampledatatemplateattribute).

### Syntax

```powershell
# ById (default)
Set-JIMExampleDataTemplate -Id <int> [-NewName <string>] [-ObjectType <hashtable[]>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByName
Set-JIMExampleDataTemplate -Name <string> [-NewName <string>] [-ObjectType <hashtable[]>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById set) | | The ID of the template to update. Accepts pipeline input by property name. |
| `Name` | `string` | Yes (ByName set) | | The name of the template to update. |
| `NewName` | `string` | No | | A new name for the template. |
| `ObjectType` | `hashtable[]` | No | | Replaces the template's entire Object Type graph. Hashtable shape is as `New-JIMExampleDataTemplate`. |
| `ChangeReason` | `string` | No | | Reason for the change, recorded on the audit Activity and shown in the template's configuration change history. |
| `PassThru` | `switch` | No | `$false` | Returns the updated Data Generation Template object. |
| `WhatIf` | `switch` | No | | Preview the operation without performing it. |
| `Confirm` | `switch` | No | | Prompt for confirmation before updating. |

At least one of `-NewName` or `-ObjectType` must be supplied; the cmdlet errors without sending a request otherwise.

### Output

If `-PassThru` is specified, returns the updated Data Generation Template object, in the same shape as `New-JIMExampleDataTemplate`.

### Examples

```powershell title="Rename a template"
Set-JIMExampleDataTemplate -Id 7 -NewName "Demo Users v2"
```

```powershell title="Replace a template's entire Object Type configuration"
Set-JIMExampleDataTemplate -Name "Demo Users" -ObjectType @{
    MetaverseObjectType = "User"
    ObjectsToCreate     = 2000
} -ChangeReason "Scaling up the evaluation dataset (CHG0201)" -PassThru
```

```powershell title="Preview a rename without applying it"
Set-JIMExampleDataTemplate -Id 7 -NewName "Demo Users v2" -WhatIf
```

---

## Remove-JIMExampleDataTemplate

Deletes a Data Generation Template, including its whole per-Object-Type attribute configuration. Built-in templates cannot be removed. This action cannot be undone; objects the template has already generated are unaffected.

### Syntax

```powershell
# ById (default)
Remove-JIMExampleDataTemplate -Id <int> [-ChangeReason <string>] [-Force] [-WhatIf] [-Confirm]

# ByName
Remove-JIMExampleDataTemplate -Name <string> [-ChangeReason <string>] [-Force] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById set) | | The ID of the template to remove. Accepts pipeline input by property name. |
| `Name` | `string` | Yes (ByName set) | | The name of the template to remove. |
| `ChangeReason` | `string` | No | | Reason for the change, recorded on the audit Activity and shown in the template's configuration change history. |
| `Force` | `switch` | No | `$false` | Bypasses confirmation prompts. |
| `WhatIf` | `switch` | No | | Preview the deletion without performing it. |
| `Confirm` | `switch` | No | | Prompt for confirmation before deleting. |

### Output

None.

### Examples

```powershell title="Delete one template, with confirmation"
Remove-JIMExampleDataTemplate -Id 7
```

```powershell title="Delete one template by name, without confirmation"
Remove-JIMExampleDataTemplate -Name "Demo Users" -Force
```

```powershell title="Delete every template whose name starts with Demo (deletes all matches)"
# Review what matches first: this deletes every template the filter returns, not just one.
Get-JIMExampleDataTemplate | Where-Object { $_.Name -like "Demo*" -and -not $_.BuiltIn } |
    ForEach-Object { Remove-JIMExampleDataTemplate -Id $_.Id -Force -ChangeReason "Retiring demo templates (CHG0202)" }
```

---

## Add-JIMExampleDataTemplateAttribute

Adds one attribute's generation configuration to an Object Type within an existing Data Generation Template, without restating the rest of the template: the cmdlet reads the template, appends the new attribute, and writes the whole configuration back with every existing setting preserved.

Supply exactly one of `-MetaverseAttribute` or `-ConnectedSystemObjectTypeAttributeId` to say which attribute values are generated for.

### Syntax

```powershell
# ById (default)
Add-JIMExampleDataTemplateAttribute -TemplateId <int> -ObjectType <object> [-MetaverseAttribute <object>] [-ConnectedSystemObjectTypeAttributeId <int>] [-Pattern <string>] [-Expression <string>] [-ExampleDataSet <object[]>] [-WeightedValue <hashtable[]>] [-PopulatedValuesPercentage <int>] [-BoolTrueDistribution <int>] [-BoolShouldBeRandom <bool>] [-MinDate <datetime>] [-MaxDate <datetime>] [-MinNumber <int>] [-MaxNumber <int>] [-SequentialNumbers <bool>] [-RandomNumbers <bool>] [-ManagerDepthPercentage <int>] [-MvaRefMinAssignments <int>] [-MvaRefMaxAssignments <int>] [-ReferenceMetaverseObjectType <object[]>] [-AttributeDependency <hashtable>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByName
Add-JIMExampleDataTemplateAttribute -TemplateName <string> -ObjectType <object> [-MetaverseAttribute <object>] [-ConnectedSystemObjectTypeAttributeId <int>] [-Pattern <string>] [-Expression <string>] [-ExampleDataSet <object[]>] [-WeightedValue <hashtable[]>] [-PopulatedValuesPercentage <int>] [-BoolTrueDistribution <int>] [-BoolShouldBeRandom <bool>] [-MinDate <datetime>] [-MaxDate <datetime>] [-MinNumber <int>] [-MaxNumber <int>] [-SequentialNumbers <bool>] [-RandomNumbers <bool>] [-ManagerDepthPercentage <int>] [-MvaRefMinAssignments <int>] [-MvaRefMaxAssignments <int>] [-ReferenceMetaverseObjectType <object[]>] [-AttributeDependency <hashtable>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `TemplateId` | `int` | Yes (ById set) | | The ID of the template to add the attribute to. Accepts pipeline input by property name. |
| `TemplateName` | `string` | Yes (ByName set) | | The name of the template to add the attribute to. |
| `ObjectType` | `object` | Yes | | The Metaverse Object Type (name or ID) identifying which of the template's Object Types receives the attribute. |
| `MetaverseAttribute` | `object` | Yes (unless targeting a Connected System) | | The Metaverse Attribute (name or ID) values are generated for. |
| `ConnectedSystemObjectTypeAttributeId` | `int` | Yes (unless targeting the metaverse) | | The ID of the Connected System attribute values are generated for. |
| `Pattern` | `string` | No | | A variable-replacement pattern for constructing string values, e.g. `{0}.{1}@contoso.com`. |
| `Expression` | `string` | No | | An expression constructing the value from already-generated attributes via `mv["Attribute Name"]`. |
| `ExampleDataSet` | `object[]` | No | | The Example Data Sets (names or IDs) values are drawn from. Order follows array order. |
| `WeightedValue` | `hashtable[]` | No | | Specific values to choose from, weighted: `@{ Value = "active"; Weight = 0.9 }`. |
| `PopulatedValuesPercentage` | `int` | No | | What percentage of generated objects receive a value for this attribute (0 to 100). |
| `BoolTrueDistribution` | `int` | No | | For boolean attributes, what percentage of values are true (0 to 100). |
| `BoolShouldBeRandom` | `bool` | No | | For boolean attributes, whether values are generated randomly. |
| `MinDate` / `MaxDate` | `datetime` | No | | For date attributes, the earliest and latest values to generate. |
| `MinNumber` / `MaxNumber` | `int` | No | | For number attributes, the smallest and largest values to generate. |
| `SequentialNumbers` / `RandomNumbers` | `bool` | No | | For number attributes, whether values are generated sequentially or randomly. |
| `ManagerDepthPercentage` | `int` | No | | For Manager attributes, how far into the organisational hierarchy managers are present (0 to 100). |
| `MvaRefMinAssignments` / `MvaRefMaxAssignments` | `int` | No | | For multi-valued reference attributes, the minimum and maximum number of values to assign. |
| `ReferenceMetaverseObjectType` | `object[]` | No | | For reference attributes, the Metaverse Object Types (names or IDs) generated references may point at. |
| `AttributeDependency` | `hashtable` | No | | A condition that must hold for the attribute to be generated: `@{ MetaverseAttribute = <name or id>; ComparisonType = "Equals"; StringValue = "Contractor" }`. Valid comparison types are `Equals`, `NotEquals`, `LessThan`, `GreaterThan`, `GreaterThanOrEqual`, `LessThanOrEqual` and `Like`. |
| `ChangeReason` | `string` | No | | Reason for the change, recorded on the audit Activity and shown in the template's configuration change history. |
| `PassThru` | `switch` | No | `$false` | Returns the updated Data Generation Template object. |
| `WhatIf` | `switch` | No | | Preview the operation without performing it. |
| `Confirm` | `switch` | No | | Prompt for confirmation before updating. |

### Output

If `-PassThru` is specified, returns the updated Data Generation Template object, in the same shape as `New-JIMExampleDataTemplate`.

### Examples

```powershell title="Add pattern-based Display Name generation"
Add-JIMExampleDataTemplateAttribute -TemplateName "Demo Users" -ObjectType "User" -MetaverseAttribute "Display Name" -Pattern "{0} {1}" -ExampleDataSet "Firstnames Female", "Lastnames"
```

```powershell title="Add a weighted-value attribute"
Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType "User" -MetaverseAttribute "Status" -WeightedValue @{ Value = "active"; Weight = 0.9 }, @{ Value = "suspended"; Weight = 0.1 }
```

```powershell title="Add a conditional date attribute"
Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType "User" -MetaverseAttribute "Employee End Date" -MinDate (Get-Date) -MaxDate (Get-Date).AddYears(2) -PopulatedValuesPercentage 10 -AttributeDependency @{
    MetaverseAttribute = "Employee Type"
    ComparisonType     = "Equals"
    StringValue        = "Contractor"
} -PassThru
```

---

## Invoke-JIMExampleDataTemplate

Executes a data generation template to create identity objects in the metaverse. Execution is queued to the JIM worker service and tracked by an Activity: the cmdlet returns as soon as the server has queued the request. Monitor progress and completion via Activities ([`Get-JIMActivity`](activities.md)), or use `-Wait` to block until generation completes with a live progress display.

Supports `ShouldProcess`, so you can use `-WhatIf` or `-Confirm` to preview or confirm execution before it begins.

### Syntax

```powershell
# ById (default)
Invoke-JIMExampleDataTemplate -Id <int> [-Wait] [-Timeout <int>] [-PassThru] [-WhatIf] [-Confirm]

# ByName
Invoke-JIMExampleDataTemplate -Name <string> [-Wait] [-Timeout <int>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById set) | | The ID of the template to execute. Accepts pipeline input. |
| `Name` | `string` | Yes (ByName set) | | The name of the template to execute. |
| `Wait` | `switch` | No | `false` | Wait for generation to complete, showing live progress (object counts, throughput and estimated time remaining). |
| `Timeout` | `int` | No | | Maximum seconds to wait when using `-Wait`; throws if exceeded. Waits indefinitely when omitted. |
| `PassThru` | `switch` | No | `false` | Return execution information to the pipeline. |
| `WhatIf` | `switch` | No | | Preview the operation without executing it. |
| `Confirm` | `switch` | No | | Prompt for confirmation before executing. |

### Output

By default, this cmdlet produces no output. When `-PassThru` is specified, returns a `PSCustomObject` with `TemplateId`, `ActivityId`, `TaskId`, `Status` and `Message` properties confirming the request was queued. `ActivityId` identifies the Activity tracking the generation; pass it to [`Get-JIMActivity`](activities.md) to check progress and completion (or combine with `-Wait`, in which case the object is returned after completion).

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

```powershell title="Execute and wait for completion with live progress"
Invoke-JIMExampleDataTemplate -Id 3 -Wait
```

```powershell title="Execute and wait up to 10 minutes"
Invoke-JIMExampleDataTemplate -Id 3 -Wait -Timeout 600
```

```powershell title="Execute, then follow the Activity"
$result = Invoke-JIMExampleDataTemplate -Id 3 -PassThru
Get-JIMActivity -Id $result.ActivityId
```

---

## Building a template end to end

Create a Data Generation Template with one attribute inline, add a second attribute to it, then generate the data:

```powershell title="Create, extend and run a Data Generation Template"
# 1. Create the template with a pattern-based Display Name attribute
$template = New-JIMExampleDataTemplate -Name "Evaluation Users" -ObjectType @{
    MetaverseObjectType = "User"
    ObjectsToCreate     = 250
    Attributes          = @(
        @{
            MetaverseAttribute = "Display Name"
            Pattern            = "{0} {1}"
            ExampleDataSets    = @("Firstnames Female", "Lastnames")
        }
    )
} -ChangeReason "Evaluation dataset (CHG0210)" -PassThru

# 2. Add an expression-based email address that reuses the generated Display Name
Add-JIMExampleDataTemplateAttribute -TemplateId $template.Id -ObjectType "User" -MetaverseAttribute "Email" -Expression 'mv["Display Name"].Replace(" ", ".").ToLower() + "@contoso.com"' -PopulatedValuesPercentage 95

# 3. Generate the objects and wait for completion
Invoke-JIMExampleDataTemplate -Id $template.Id -Wait
```

---

## See also

- [Metaverse](metaverse.md): cmdlets for managing the metaverse schema and querying identity objects
- [Activities](activities.md): cmdlets for reviewing activity history and execution results
