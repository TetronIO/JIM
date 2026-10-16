---
title: Example Data
description: "The cmdlets that manage Example Data Sets and Example Data Templates, and generate sample identities from them."
type: reference
audience: automation-author
---

# Example Data

Cmdlets for generating sample identity data for testing and evaluation purposes. Example data sets and templates allow you to populate the Metaverse with realistic test identities without requiring a live Connected System.

---

## Get-JIMExampleDataSet

Retrieves available example data sets. Each data set is a named pool of string values (e.g. a list of cities, or first names) that Data Generation Templates can draw from.

<!-- BEGIN GENERATED: Get-JIMExampleDataSet -->
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
| `Id` | `int` | Yes (ById) |  | The Example Data Set to get, with its values. Accepts pipeline input by property name. |
| `Page` | `int` | No (List) | `1` | The page of Example Data Sets to return. At least 1. |
| `PageSize` | `int` | No (List) | `100` | How many Example Data Sets per page. From 1 to 1,000. |

### Output

A list returns each Example Data Set's header, and `-Id` the Example Data Set with its values.

**By default**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The unique identifier of the Example Data Set. |
| `Name` | `string` | The name of the Example Data Set, for example "Firstnames Female". |
| `BuiltIn` | `bool` | Whether the Example Data Set ships with JIM, rather than being created by an administrator. |
| `Created` | `datetime` | When the Example Data Set was created, in UTC. |
| `Culture` | `string` | The .NET culture code the values are in, for example "en-GB". |
| `ValueCount` | `int` | How many values the Example Data Set holds. |

**With `-Id`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The unique identifier of the Example Data Set. |
| `Name` | `string` | The name of the Example Data Set, for example "Firstnames Female". |
| `Culture` | `string` | The .NET culture code the values are in, for example "en-GB". |
| `BuiltIn` | `bool` | Whether the Example Data Set ships with JIM, rather than being created by an administrator. |
| `Created` | `datetime` | When the Example Data Set was created (UTC). |
| `CreatedByName` | `string` | The display name of the principal that created the Example Data Set. |
| `LastUpdated` | `datetime` | When the Example Data Set was last modified (UTC). Empty if never modified after creation. |
| `LastUpdatedByName` | `string` | The display name of the principal that last modified the Example Data Set. |
| `Values` | `object[]` | The values in the Example Data Set. Each has `Id` and `StringValue`. |
<!-- END GENERATED: Get-JIMExampleDataSet -->

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

<!-- BEGIN GENERATED: New-JIMExampleDataSet -->
### Syntax

```powershell
New-JIMExampleDataSet [-Name] <string> -Culture <string> [-Values <string[]>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes |  | The name for the Example Data Set. Can be given by position, as the first argument. |
| `Culture` | `string` | Yes |  | The .NET culture the values are in, for example "en-GB". |
| `Values` | `string[]` | No |  | The values that make up the Example Data Set. |
| `ChangeReason` | `string` | No |  | A reason for the change, recorded on its Activity and shown in the Example Data Set's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the created Example Data Set. |

### Output

With `-PassThru`, the created Example Data Set, with its values; otherwise nothing.

Properties as under [Get-JIMExampleDataSet](#get-jimexampledataset).
<!-- END GENERATED: New-JIMExampleDataSet -->

### Examples

```powershell title="Create a data set of UK city names"
New-JIMExampleDataSet -Name "UK Cities" -Culture "en-GB" -Values "London", "Manchester", "Bristol" -PassThru
```

---

## Set-JIMExampleDataSet

Updates an existing Example Data Set. Built-in data sets cannot be updated.

<!-- BEGIN GENERATED: Set-JIMExampleDataSet -->
### Syntax

```powershell
Set-JIMExampleDataSet -Id <int> [-Name <string>] [-Culture <string>] [-Values <string[]>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes |  | The Example Data Set to update. Accepts pipeline input by property name. |
| `Name` | `string` | No |  | The new name. |
| `Culture` | `string` | No |  | The .NET culture the values are in, for example "en-GB". |
| `Values` | `string[]` | No |  | The values, replacing all of the Example Data Set's current ones. |
| `ChangeReason` | `string` | No |  | A reason for the change, recorded on its Activity and shown in the Example Data Set's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the updated Example Data Set. |

### Output

With `-PassThru`, the updated Example Data Set; otherwise nothing.

Properties as under [Get-JIMExampleDataSet](#get-jimexampledataset).
<!-- END GENERATED: Set-JIMExampleDataSet -->

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

<!-- BEGIN GENERATED: Remove-JIMExampleDataSet -->
### Syntax

```powershell
Remove-JIMExampleDataSet -Id <int> [-ChangeReason <string>] [-Force] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes |  | The Example Data Set to delete. Accepts pipeline input by property name. |
| `ChangeReason` | `string` | No |  | A reason for the change, recorded on its Activity and shown in the Example Data Set's Configuration Change History. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. |

### Output

None.
<!-- END GENERATED: Remove-JIMExampleDataSet -->

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

<!-- BEGIN GENERATED: Get-JIMExampleDataTemplate -->
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
| `Id` | `int` | Yes (ById) |  | The Data Generation Template to get, in full. Accepts pipeline input by property name. |
| `Name` | `string` | Yes (ByName) |  | The name of the Data Generation Template to get, in full. |
| `Page` | `int` | No (List) | `1` | The page of Data Generation Templates to return. At least 1. |
| `PageSize` | `int` | No (List) | `100` | How many Data Generation Templates per page. From 1 to 1,000. |

### Output

A list returns each Data Generation Template's header; `-Id` and `-Name` return the template in full, with its Object Types and their attribute configuration.

**By default**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The unique identifier of the Data Generation Template. |
| `Name` | `string` | The name of the Data Generation Template, for example "Demo Users". |
| `BuiltIn` | `bool` | Whether the Data Generation Template ships with JIM, rather than being created by an administrator. |
| `Created` | `datetime` | When the Data Generation Template was created, in UTC. |
| `ObjectTypeCount` | `int` | How many Object Types the Data Generation Template generates objects for. |

**With `-Id` or `-Name`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The unique identifier of the template. |
| `Name` | `string` | The name of the template, for example "Demo Users". |
| `BuiltIn` | `bool` | Whether the Data Generation Template ships with JIM, rather than being created by an administrator. |
| `Created` | `datetime` | When the template was created (UTC). |
| `CreatedByName` | `string` | The display name of the principal that created the template. |
| `LastUpdated` | `datetime` | When the template was last modified (UTC). Empty if never modified after creation. |
| `LastUpdatedByName` | `string` | The display name of the principal that last modified the template. |
| `ObjectTypes` | `object[]` | The Object Types the template generates objects for. Each has `Id`, `MetaverseObjectTypeId`, `MetaverseObjectTypeName`, `ObjectsToCreate` and `TemplateAttributes`. |
| `Warnings` | `string[]` | What the create or update that returned this template had to say about its expressions without refusing it: an expression reading an attribute the template does not generate, or a problem with one the update left as it was. Empty on every read. |
<!-- END GENERATED: Get-JIMExampleDataTemplate -->

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

<!-- BEGIN GENERATED: New-JIMExampleDataTemplate -->
### Syntax

```powershell
New-JIMExampleDataTemplate [-Name] <string> -ObjectType <hashtable[]> [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes |  | The name for the Data Generation Template. Can be given by position, as the first argument. |
| `ObjectType` | `hashtable[]` | Yes |  | One hashtable per Object Type the template generates objects for. Each takes MetaverseObjectType (a Metaverse Object Type name or id; required), ObjectsToCreate (how many objects to generate; 1 if omitted) and Attributes, an optional array of attribute generation hashtables using the API's request field names (Pattern, Expression, PopulatedValuesPercentage, MinNumber, MaxNumber, MinDate, MaxDate, BoolTrueDistribution, BoolShouldBeRandom, SequentialNumbers, RandomNumbers, WeightedStringValues, ManagerDepthPercentage, MvaRefMinAssignments, MvaRefMaxAssignments, ReferenceMetaverseObjectTypeIds, AttributeDependency), except that MetaverseAttribute may be a Metaverse Attribute name or id, and ExampleDataSets entries may be Example Data Set names or ids, in order. Unknown keys throw, so a typo is caught before anything is sent. |
| `ChangeReason` | `string` | No |  | A reason for the change, recorded on its Activity and shown in the Data Generation Template's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the created Data Generation Template. |

### Output

With `-PassThru`, the created Data Generation Template; otherwise nothing.

Properties as under [Get-JIMExampleDataTemplate](#get-jimexampledatatemplate).
<!-- END GENERATED: New-JIMExampleDataTemplate -->

#### `-ObjectType` hashtable keys

| Key | Type | Required | Description |
|-----|------|----------|-------------|
| `MetaverseObjectType` | `string` or `int` | Yes | The Metaverse Object Type to generate objects for, by name or ID. |
| `ObjectsToCreate` | `int` | No (default `1`) | How many objects to generate for this Object Type. |
| `Attributes` | `hashtable[]` | No | One hashtable per attribute whose values are generated. See the attribute keys below. |

#### Attribute hashtable keys

| Key | Type | Description |
|-----|------|-------------|
| `MetaverseAttribute` | `string` or `int` | The Metaverse Attribute values are generated for, by name or ID. Required. |
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
| `AttributeDependency` | `hashtable` | A condition that must hold for the attribute to be generated: `@{ MetaverseAttribute = <name or id>; ComparisonType = "Equals"; StringValue = "Contractor" }`. The comparison types are the [scoping operators](../configuration/scoping-criteria.md#operators) for the depended-on attribute's data type, by name (`Equals`, `GreaterThanOrEquals`, `StartsWith`, `MatchesPattern`, `HasNoValue` and the rest); `HasValue` and `HasNoValue` take no `StringValue`. Write `StringValue` as that type reads it: numbers with a full stop, dates as `2026-07-01`, Booleans as `true` or `false`. Text compares case-sensitively. |

Any other key in an `-ObjectType` or attribute hashtable throws, so a mis-typed key is caught before anything is sent.

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

Expressions are [checked when the template is saved](../concepts/expressions.md#validation), by this cmdlet, `New-JIMExampleDataTemplate` and `Add-JIMExampleDataTemplateAttribute` alike. A new or changed expression that does not parse, or reads `cs["..."]`, is refused. One reading an attribute the template does not generate, or a problem with an expression the save left as it was, is written as a warning and the template is saved.

<!-- BEGIN GENERATED: Set-JIMExampleDataTemplate -->
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
| `Id` | `int` | Yes (ById) |  | The Data Generation Template to update. Accepts pipeline input by property name. |
| `Name` | `string` | Yes (ByName) |  | The name of the Data Generation Template to update. |
| `NewName` | `string` | No |  | The new name. |
| `ObjectType` | `hashtable[]` | No |  | Replaces the template's whole Object Type graph: every Object Type and attribute configuration not in these hashtables is removed. `Add-JIMExampleDataTemplateAttribute` adds one attribute without restating the rest. The hashtables take the same shape as `New-JIMExampleDataTemplate -ObjectType`. |
| `ChangeReason` | `string` | No |  | A reason for the change, recorded on its Activity and shown in the Data Generation Template's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the updated Data Generation Template. |

### Output

With `-PassThru`, the updated Data Generation Template; otherwise nothing.

Properties as under [Get-JIMExampleDataTemplate](#get-jimexampledatatemplate).
<!-- END GENERATED: Set-JIMExampleDataTemplate -->

At least one of `-NewName` or `-ObjectType` must be supplied; the cmdlet errors without sending a request otherwise.

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

<!-- BEGIN GENERATED: Remove-JIMExampleDataTemplate -->
### Syntax

```powershell
# ById (default)
Remove-JIMExampleDataTemplate -Id <int> [-ChangeReason <string>] [-Force] [-WhatIf] [-Confirm]

# ByName
Remove-JIMExampleDataTemplate -Name <string> [-ChangeReason <string>] [-Force] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The Data Generation Template to delete. Accepts pipeline input by property name. |
| `Name` | `string` | Yes (ByName) |  | The name of the Data Generation Template to delete. |
| `ChangeReason` | `string` | No |  | A reason for the change, recorded on its Activity and shown in the Data Generation Template's Configuration Change History. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. |

### Output

None.
<!-- END GENERATED: Remove-JIMExampleDataTemplate -->

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

<!-- BEGIN GENERATED: Add-JIMExampleDataTemplateAttribute -->
### Syntax

```powershell
# ById (default)
Add-JIMExampleDataTemplateAttribute -TemplateId <int> -ObjectType <object> [-MetaverseAttribute <object>] [-Pattern <string>] [-Expression <string>] [-ExampleDataSet <object[]>] [-WeightedValue <hashtable[]>] [-PopulatedValuesPercentage <int>] [-BoolTrueDistribution <int>] [-BoolShouldBeRandom <bool>] [-MinDate <datetime>] [-MaxDate <datetime>] [-MinNumber <int>] [-MaxNumber <int>] [-SequentialNumbers <bool>] [-RandomNumbers <bool>] [-ManagerDepthPercentage <int>] [-MvaRefMinAssignments <int>] [-MvaRefMaxAssignments <int>] [-ReferenceMetaverseObjectType <object[]>] [-AttributeDependency <hashtable>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByName
Add-JIMExampleDataTemplateAttribute -TemplateName <string> -ObjectType <object> [-MetaverseAttribute <object>] [-Pattern <string>] [-Expression <string>] [-ExampleDataSet <object[]>] [-WeightedValue <hashtable[]>] [-PopulatedValuesPercentage <int>] [-BoolTrueDistribution <int>] [-BoolShouldBeRandom <bool>] [-MinDate <datetime>] [-MaxDate <datetime>] [-MinNumber <int>] [-MaxNumber <int>] [-SequentialNumbers <bool>] [-RandomNumbers <bool>] [-ManagerDepthPercentage <int>] [-MvaRefMinAssignments <int>] [-MvaRefMaxAssignments <int>] [-ReferenceMetaverseObjectType <object[]>] [-AttributeDependency <hashtable>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `TemplateId` | `int` | Yes (ById) |  | The unique identifier of the Data Generation Template to add the attribute to. Accepts pipeline input by property name. |
| `TemplateName` | `string` | Yes (ByName) |  | The name of the Data Generation Template to add the attribute to. |
| `ObjectType` | `object` | Yes |  | The Metaverse Object Type (name or id) identifying which of the template's Object Types receives the attribute. |
| `MetaverseAttribute` | `object` | No |  | The Metaverse Attribute (name or id) values are generated for. Required. |
| `Pattern` | `string` | No |  | A variable-replacement pattern for constructing string values, for example "{0}.{1}@contoso.com". |
| `Expression` | `string` | No |  | An expression that constructs the value from other already-generated attributes via mv["Attribute Name"]. |
| `ExampleDataSet` | `object[]` | No |  | The Example Data Sets (names or ids) values are drawn from. Order follows array order, which is what index-based patterns like "{0} {1}" refer to. |
| `WeightedValue` | `hashtable[]` | No |  | Specific string values to choose from, weighted to control their distribution: one @{ Value = ..; Weight = .. } hashtable per value. |
| `PopulatedValuesPercentage` | `int` | No |  | What percentage of generated objects receive a value for this attribute. From 0 to 100. |
| `BoolTrueDistribution` | `int` | No |  | For boolean attributes, what percentage of values are true. From 0 to 100. |
| `BoolShouldBeRandom` | `bool` | No |  | For boolean attributes, whether values are generated randomly. |
| `MinDate` | `datetime` | No |  | For date attributes, the earliest value to generate. |
| `MaxDate` | `datetime` | No |  | For date attributes, the latest value to generate. |
| `MinNumber` | `int` | No |  | For number attributes, the smallest value to generate. |
| `MaxNumber` | `int` | No |  | For number attributes, the largest value to generate. |
| `SequentialNumbers` | `bool` | No |  | For number attributes, whether values are generated sequentially. |
| `RandomNumbers` | `bool` | No |  | For number attributes, whether values are generated randomly. |
| `ManagerDepthPercentage` | `int` | No |  | For Manager attributes, how far into the organisational hierarchy managers are present. From 0 to 100. |
| `MvaRefMinAssignments` | `int` | No |  | For multi-valued reference attributes, the minimum number of values to assign. |
| `MvaRefMaxAssignments` | `int` | No |  | For multi-valued reference attributes, the maximum number of values to assign. |
| `ReferenceMetaverseObjectType` | `object[]` | No |  | For reference attributes, the Metaverse Object Types (names or ids) generated references may point at. |
| `AttributeDependency` | `hashtable` | No |  | A condition on another attribute that must hold for this attribute to be generated: @{ MetaverseAttribute = <name or id>; ComparisonType = <operator>; StringValue = <value> }. The operators are the ones Synchronisation Rule scoping offers for the depended-on attribute's data type, such as Equals, GreaterThanOrEquals, StartsWith, MatchesPattern, HasValue and HasNoValue (which take no StringValue). Write StringValue as the attribute's type reads it: numbers with a full stop, dates as 2026-07-01, Booleans as true or false. Text is compared case-sensitively. |
| `ChangeReason` | `string` | No |  | A reason for the change, recorded on its Activity and shown in the Data Generation Template's Configuration Change History. |
| `PassThru` | `switch` | No |  | Returns the updated Data Generation Template. |

### Output

With `-PassThru`, the updated Data Generation Template; otherwise nothing.

Properties as under [Get-JIMExampleDataTemplate](#get-jimexampledatatemplate).
<!-- END GENERATED: Add-JIMExampleDataTemplateAttribute -->

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

Executes a Data Generation Template to create objects in the Metaverse. Execution is queued to the JIM worker service and tracked by an Activity: the cmdlet returns as soon as the server has queued the request. Monitor progress and completion via Activities ([`Get-JIMActivity`](activities.md)), or use `-Wait` to block until generation completes with a live progress display.

<!-- BEGIN GENERATED: Invoke-JIMExampleDataTemplate -->
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
| `Id` | `int` | Yes (ById) |  | The Data Generation Template to run. Accepts pipeline input by property name. |
| `Name` | `string` | Yes (ByName) |  | The name of the Data Generation Template to run. |
| `Wait` | `switch` | No |  | Waits for generation to finish, showing its progress: object counts, throughput and the estimated time remaining. |
| `Timeout` | `int` | No |  | How many seconds `-Wait` waits before it throws an error naming the Activity, which may still be running. Omit it to wait until generation finishes. At least 1. |
| `PassThru` | `switch` | No |  | Returns the response to queuing the run. |

### Output

With `-PassThru`, the response to queuing the run, with the template and where the run got to; otherwise nothing. `Get-JIMActivity -Follow` follows its ActivityId.

| Property | Type | Description |
|----------|------|-------------|
| `ActivityId` | `guid` | The Activity that records the execution. Follow it with `GET /api/v1/activities/{id}`, or the lighter `GET /api/v1/activities/{id}/progress`. |
| `TaskId` | `guid` | The worker task ID for the queued execution. |
| `Message` | `string` | Message describing the result. |
| `TemplateId` | `int` | The Data Generation Template that was run. |
| `Status` | `string` | Queued, or with `-Wait` the Activity's final status, for example Complete or FailedWithError. |
<!-- END GENERATED: Invoke-JIMExampleDataTemplate -->

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

## Test-JIMExampleDataExpression

Tests the expression an Example Data Template attribute generates its value with, against sample values for the attributes it reads, by the same evaluation generating objects runs. Nothing is generated. Each sample is read as the type of the template attribute that generates it on the same object; `Outcome` says whether the attribute would be generated as a value, without one, or whether generating objects from the template would fail. Give `-Expression` to try an edit before saving it. Find a template attribute's ID with [`Get-JIMExampleDataTemplate`](#get-jimexampledatatemplate).

<!-- BEGIN GENERATED: Test-JIMExampleDataExpression -->
### Syntax

```powershell
Test-JIMExampleDataExpression [-TemplateId] <int> [-TemplateAttributeId] <int> [-Expression <string>] [-Attributes <hashtable>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `TemplateId` | `int` | Yes |  | The ID of the Example Data Template the attribute belongs to. Can be given by position, as the first argument. |
| `TemplateAttributeId` | `int` | Yes |  | The ID of the template attribute whose expression to test. `Get-JIMExampleDataTemplate` lists each object type's template attributes with their IDs. Can be given by position, as the second argument. |
| `Expression` | `string` | No |  | An expression to test in place of the template attribute's saved one, so an edit can be tried before it is saved. When left out, the attribute's own expression is tested. |
| `Attributes` | `hashtable` | No |  | A hashtable of sample values for the attributes the expression reads, keyed by Metaverse attribute name. `$null` is no value and an array gives the attribute several values. |

### Output

What generating objects would give the attribute from the sample values. Value is the generated value in JIM's text form, OutcomeDescription says what would happen in one sentence, Inputs lists the attributes the expression reads with their types and the values they were given, and InputsNotGenerated names any it reads that the template does not generate, which never have a value.

| Property | Type | Description |
|----------|------|-------------|
| `IsValid` | `bool` | True when the sample values could be used and the expression gives the attribute a value or no value; false for an input error or an expression that would fail generation. |
| `Attribute` | `string` | The Metaverse attribute the template attribute generates. |
| `Expression` | `string` | The expression tested. |
| `Outcome` | `string` | What the attribute would be given: a value, no value, or an error. Not set when the sample values could not be used. One of `Value`, `NoValue` or `Error`. |
| `ErrorReason` | `string` | Why the outcome is an error. Set only when `Outcome` is Error. One of `EvaluationFailed`, `SeveralValues` or `NoTextForm`. |
| `Value` | `string` | The value the attribute would be generated as, in JIM's text form. Set only when `Outcome` is Value. |
| `OutcomeDescription` | `string` | What generating objects would do, in one sentence. Not set when the sample values could not be used. |
| `ErrorMessage` | `string` | Why the test failed: the input errors, or the outcome description for an expression that would fail generation. |
| `InputErrors` | `string[]` | Why the sample values could not be used, one message each. When present the expression was not evaluated. |
| `Inputs` | `object[]` | The attributes the expression reads that the template generates on the same object, with their types and the values they were given. Each has `Attribute`, `Type`, `Plurality` and `ValueLines`. |
| `InputsNotGenerated` | `string[]` | The attributes the expression reads that the template does not generate on the object, so they never have a value when objects are generated. |
<!-- END GENERATED: Test-JIMExampleDataExpression -->

### Examples

```powershell title="Test a template attribute's expression with sample values"
Test-JIMExampleDataExpression -TemplateId 4 -TemplateAttributeId 13 -Attributes @{ 'First Name' = 'Ada'; 'Last Name' = 'Lovelace' }
```

```powershell title="Try an edited expression before saving it"
Test-JIMExampleDataExpression -TemplateId 4 -TemplateAttributeId 13 -Expression 'Lower(Left(mv["First Name"], 1) + mv["Last Name"])' -Attributes @{ 'First Name' = 'Ada'; 'Last Name' = 'Lovelace' }
```

```powershell title="Show only the generated value"
(Test-JIMExampleDataExpression -TemplateId 4 -TemplateAttributeId 13 -Attributes @{ 'First Name' = 'Ada'; 'Last Name' = 'Lovelace' }).Value
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

- [Metaverse](metaverse.md): cmdlets for querying Metaverse Objects; the schema has [Metaverse Object Types](metaverse-object-types.md) and [Metaverse Attributes](metaverse-attributes.md)
- [Activities](activities.md): cmdlets for reviewing activity history and execution results
