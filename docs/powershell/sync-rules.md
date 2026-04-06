---
title: Sync Rules
---

# Sync Rules

Sync rules define how data flows between connected systems and the metaverse. They control attribute mappings, scoping criteria, and object matching logic. The cmdlets on this page cover the full lifecycle of sync rule configuration.

---

### Sync Rule CRUD

Create, retrieve, update, and delete sync rules.

---

## Get-JIMSyncRule

Retrieves one or more sync rules. When called without parameters, returns all sync rules. Use the parameter sets to filter by ID, connected system ID, or connected system name.

### Syntax

```powershell
# List all sync rules (default)
Get-JIMSyncRule [-Name <string>]

# By sync rule ID
Get-JIMSyncRule -Id <int>

# By connected system ID
Get-JIMSyncRule -ConnectedSystemId <int> [-Name <string>]

# By connected system name
Get-JIMSyncRule -ConnectedSystemName <string> [-Name <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById set) | | The ID of a specific sync rule to retrieve |
| `ConnectedSystemId` | `int` | No | | Filter sync rules by connected system ID. Accepts pipeline input. |
| `ConnectedSystemName` | `string` | No | | Filter sync rules by connected system name. Must be an exact match. |
| `Name` | `string` | No | | Filter sync rules by name. Supports wildcards (e.g., `"Inbound*"`). |

### Output

Returns one or more sync rule objects containing the rule configuration, direction, projection/provisioning settings, and enabled state.

### Examples

```powershell title="List all sync rules"
Get-JIMSyncRule
```

```powershell title="Get a specific sync rule by ID"
Get-JIMSyncRule -Id 5
```

```powershell title="Filter by name"
Get-JIMSyncRule -Name "Inbound*"
```

```powershell title="Get sync rules for a connected system"
Get-JIMSyncRule -ConnectedSystemName "Active Directory"
```

```powershell title="Pipeline from connected system ID"
$cs = Get-JIMConnectedSystem -Name "HR System"
Get-JIMSyncRule -ConnectedSystemId $cs.Id
```

---

## New-JIMSyncRule

Creates a new sync rule for a connected system. The rule defines how objects flow between the connected system and the metaverse.

### Syntax

```powershell
# By connected system ID (default)
New-JIMSyncRule -Name <string> -ConnectedSystemId <int>
    -ConnectedSystemObjectTypeId <int> -MetaverseObjectTypeId <int>
    -Direction <string> [-ProjectToMetaverse] [-ProvisionToConnectedSystem]
    [-Enabled <bool>] [-PassThru]

# By connected system name
New-JIMSyncRule -Name <string> -ConnectedSystemName <string>
    -ConnectedSystemObjectTypeId <int> -MetaverseObjectTypeId <int>
    -Direction <string> [-ProjectToMetaverse] [-ProvisionToConnectedSystem]
    [-Enabled <bool>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes (Position 0) | | Display name for the sync rule |
| `ConnectedSystemId` | `int` | Yes (ById set) | | The ID of the connected system this rule belongs to |
| `ConnectedSystemName` | `string` | Yes (ByName set) | | The name of the connected system this rule belongs to |
| `ConnectedSystemObjectTypeId` | `int` | Yes | | The object type ID on the connected system side |
| `MetaverseObjectTypeId` | `int` | Yes | | The object type ID on the metaverse side |
| `Direction` | `string` | Yes | | Data flow direction. Valid values: `Import`, `Export` |
| `ProjectToMetaverse` | `switch` | No | `$false` | When set, import rules will project new metaverse objects. Only applicable when Direction is `Import`. |
| `ProvisionToConnectedSystem` | `switch` | No | `$false` | When set, export rules will provision new connected system objects. Only applicable when Direction is `Export`. |
| `Enabled` | `bool` | No | `$true` | Whether the sync rule is active |
| `PassThru` | `switch` | No | `$false` | Returns the created sync rule object |

### Output

With `-PassThru`, returns the created sync rule object. Without it, returns nothing.

**ShouldProcess impact level:** Medium.

### Examples

```powershell title="Create an import sync rule with projection"
New-JIMSyncRule -Name "AD User Import" `
    -ConnectedSystemId 1 `
    -ConnectedSystemObjectTypeId 3 `
    -MetaverseObjectTypeId 1 `
    -Direction Import `
    -ProjectToMetaverse `
    -PassThru
```

```powershell title="Create an export sync rule by connected system name"
New-JIMSyncRule -Name "AD User Export" `
    -ConnectedSystemName "Active Directory" `
    -ConnectedSystemObjectTypeId 3 `
    -MetaverseObjectTypeId 1 `
    -Direction Export `
    -ProvisionToConnectedSystem
```

```powershell title="Create a disabled sync rule"
New-JIMSyncRule -Name "HR Import (Draft)" `
    -ConnectedSystemId 2 `
    -ConnectedSystemObjectTypeId 5 `
    -MetaverseObjectTypeId 1 `
    -Direction Import `
    -Enabled $false
```

---

## Set-JIMSyncRule

Modifies an existing sync rule. Supports renaming, toggling enabled state, and changing projection/provisioning settings.

### Syntax

```powershell
# By ID (default)
Set-JIMSyncRule -Id <int> [-Name <string>] [-ProjectToMetaverse <bool>]
    [-ProvisionToConnectedSystem <bool>] [-PassThru]

# Enable shortcut
Set-JIMSyncRule -Id <int> -Enable [-PassThru]

# Disable shortcut
Set-JIMSyncRule -Id <int> -Disable [-PassThru]

# By input object
Set-JIMSyncRule -InputObject <PSCustomObject> [-Name <string>]
    [-ProjectToMetaverse <bool>] [-ProvisionToConnectedSystem <bool>]
    [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById, Enable, Disable sets) | | The ID of the sync rule to modify. Accepts pipeline input. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject set) | | A sync rule object from `Get-JIMSyncRule`. Accepts pipeline input. |
| `Name` | `string` | No | | New display name for the sync rule |
| `Enable` | `switch` | Yes (Enable set) | | Enables the sync rule |
| `Disable` | `switch` | Yes (Disable set) | | Disables the sync rule |
| `ProjectToMetaverse` | `bool` | No | | Controls whether the rule projects new metaverse objects |
| `ProvisionToConnectedSystem` | `bool` | No | | Controls whether the rule provisions new connected system objects |
| `PassThru` | `switch` | No | `$false` | Returns the updated sync rule object |

### Output

With `-PassThru`, returns the updated sync rule object. Without it, returns nothing.

**ShouldProcess impact level:** Medium.

### Examples

```powershell title="Rename a sync rule"
Set-JIMSyncRule -Id 5 -Name "AD User Import (Production)"
```

```powershell title="Enable a sync rule"
Set-JIMSyncRule -Id 5 -Enable
```

```powershell title="Disable a sync rule"
Set-JIMSyncRule -Id 5 -Disable
```

```powershell title="Pipeline: disable all sync rules for a connected system"
Get-JIMSyncRule -ConnectedSystemName "HR System" | Set-JIMSyncRule -Disable
```

```powershell title="Enable projection on an existing import rule"
Set-JIMSyncRule -Id 5 -ProjectToMetaverse $true -PassThru
```

---

## Remove-JIMSyncRule

Deletes a sync rule and all associated configuration, including attribute mappings, scoping criteria, and matching rules.

### Syntax

```powershell
# By ID (default)
Remove-JIMSyncRule -Id <int> [-Force] [-PassThru]

# By input object
Remove-JIMSyncRule -InputObject <PSCustomObject> [-Force] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById set) | | The ID of the sync rule to delete. Accepts pipeline input. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject set) | | A sync rule object from `Get-JIMSyncRule`. Accepts pipeline input. |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt |
| `PassThru` | `switch` | No | `$false` | Returns the deleted sync rule object before removal |

### Output

With `-PassThru`, returns the sync rule object that was deleted. Without it, returns nothing.

**ShouldProcess impact level:** High. Prompts for confirmation unless `-Force` is specified.

### Examples

```powershell title="Delete a sync rule with confirmation"
Remove-JIMSyncRule -Id 5
```

```powershell title="Force delete without confirmation"
Remove-JIMSyncRule -Id 5 -Force
```

```powershell title="Pipeline: remove all disabled sync rules for a connected system"
Get-JIMSyncRule -ConnectedSystemName "Legacy HR" |
    Where-Object { -not $_.Enabled } |
    Remove-JIMSyncRule -Force
```

---

### Attribute Mappings

Configure how attributes flow between connected system objects and metaverse objects within a sync rule. Mappings can use direct attribute-to-attribute flows or expression-based transformations.

---

## Get-JIMSyncRuleMapping

Retrieves attribute flow mappings for a sync rule. Returns all mappings for the rule, or a specific mapping by ID.

### Syntax

```powershell
# All mappings for a sync rule
Get-JIMSyncRuleMapping -SyncRuleId <int>

# Specific mapping
Get-JIMSyncRuleMapping -SyncRuleId <int> -MappingId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the sync rule. Accepts pipeline input. Alias: `Id`. |
| `MappingId` | `int` | No | | The ID of a specific mapping to retrieve |

### Output

Returns one or more mapping objects representing attribute flow rules. Each mapping includes the source attribute(s) or expression, the target attribute, and the flow direction.

### Examples

```powershell title="List all mappings for a sync rule"
Get-JIMSyncRuleMapping -SyncRuleId 5
```

```powershell title="Get a specific mapping"
Get-JIMSyncRuleMapping -SyncRuleId 5 -MappingId 12
```

```powershell title="Pipeline from Get-JIMSyncRule"
Get-JIMSyncRule -Id 5 | Get-JIMSyncRuleMapping
```

---

## New-JIMSyncRuleMapping

Creates a new attribute flow mapping on a sync rule. Mappings can be direct attribute flows (one or more source attributes to a target) or expression-based transformations.

### Syntax

```powershell
# Import: direct attribute flow (CS -> MV)
New-JIMSyncRuleMapping -SyncRuleId <int>
    -SourceConnectedSystemAttributeId <int[]>
    -TargetMetaverseAttributeId <int>

# Import: expression-based flow (CS -> MV)
New-JIMSyncRuleMapping -SyncRuleId <int>
    -Expression <string>
    -TargetMetaverseAttributeId <int>

# Export: direct attribute flow (MV -> CS)
New-JIMSyncRuleMapping -SyncRuleId <int>
    -SourceMetaverseAttributeId <int[]>
    -TargetConnectedSystemAttributeId <int>

# Export: expression-based flow (MV -> CS)
New-JIMSyncRuleMapping -SyncRuleId <int>
    -Expression <string>
    -TargetConnectedSystemAttributeId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the sync rule. Accepts pipeline input. Alias: `Id`. |
| `TargetMetaverseAttributeId` | `int` | Yes (Import sets) | | The metaverse attribute to write to (import direction) |
| `TargetConnectedSystemAttributeId` | `int` | Yes (Export sets) | | The connected system attribute to write to (export direction) |
| `SourceConnectedSystemAttributeId` | `int[]` | Yes (ImportAttribute set) | | One or more connected system attribute IDs to read from |
| `SourceMetaverseAttributeId` | `int[]` | Yes (ExportAttribute set) | | One or more metaverse attribute IDs to read from |
| `Expression` | `string` | Yes (ImportExpression, ExportExpression sets) | | A DynamicExpresso expression. Use `mv["Name"]` for metaverse attributes and `cs["Name"]` for connected system attributes. |

### Output

Returns the created mapping object.

**ShouldProcess impact level:** Medium.

### Notes

- When multiple source attributes are provided, they are automatically ordered by position (0, 1, 2, and so on).
- Expressions use DynamicExpresso syntax with `mv["AttributeName"]` and `cs["AttributeName"]` accessors.

### Examples

```powershell title="Direct import: map CS 'givenName' to MV 'firstName'"
New-JIMSyncRuleMapping -SyncRuleId 5 `
    -SourceConnectedSystemAttributeId 10 `
    -TargetMetaverseAttributeId 3
```

```powershell title="Expression import: concatenate CS attributes into MV 'displayName'"
New-JIMSyncRuleMapping -SyncRuleId 5 `
    -Expression 'cs["givenName"] + " " + cs["sn"]' `
    -TargetMetaverseAttributeId 7
```

```powershell title="Direct export: map MV 'email' to CS 'mail'"
New-JIMSyncRuleMapping -SyncRuleId 8 `
    -SourceMetaverseAttributeId 15 `
    -TargetConnectedSystemAttributeId 22
```

```powershell title="Multiple source attributes for import"
New-JIMSyncRuleMapping -SyncRuleId 5 `
    -SourceConnectedSystemAttributeId 10, 11 `
    -TargetMetaverseAttributeId 7
```

---

## Remove-JIMSyncRuleMapping

Deletes an attribute flow mapping from a sync rule.

### Syntax

```powershell
# By IDs
Remove-JIMSyncRuleMapping -SyncRuleId <int> -MappingId <int> [-Force]

# By input object
Remove-JIMSyncRuleMapping -SyncRuleId <int> -InputObject <PSCustomObject> [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the sync rule |
| `MappingId` | `int` | Yes (by ID) | | The ID of the mapping to delete. Accepts pipeline input. Alias: `Id`. |
| `InputObject` | `PSCustomObject` | Yes (by object) | | A mapping object from `Get-JIMSyncRuleMapping`. Accepts pipeline input. |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt |

### Output

None.

**ShouldProcess impact level:** High. Prompts for confirmation unless `-Force` is specified.

### Examples

```powershell title="Delete a specific mapping"
Remove-JIMSyncRuleMapping -SyncRuleId 5 -MappingId 12
```

```powershell title="Force delete without confirmation"
Remove-JIMSyncRuleMapping -SyncRuleId 5 -MappingId 12 -Force
```

```powershell title="Pipeline: remove all mappings for a sync rule"
Get-JIMSyncRuleMapping -SyncRuleId 5 |
    Remove-JIMSyncRuleMapping -SyncRuleId 5 -Force
```

---

### Scoping Criteria

Scoping criteria control which objects a sync rule processes. Criteria are organised into groups that evaluate as `All` (AND) or `Any` (OR), and groups can be nested for complex logic.

---

## Get-JIMScopingCriteria

Retrieves scoping criteria groups and their nested criteria for a sync rule.

### Syntax

```powershell
# All groups for a sync rule
Get-JIMScopingCriteria -SyncRuleId <int>

# Specific group
Get-JIMScopingCriteria -SyncRuleId <int> -GroupId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the sync rule. Accepts pipeline input. Alias: `Id`. |
| `GroupId` | `int` | No | | The ID of a specific scoping criteria group to retrieve |

### Output

Returns one or more scoping criteria group objects. Each group contains its type (`All` or `Any`), position, and nested criteria or child groups.

### Examples

```powershell title="List all scoping criteria for a sync rule"
Get-JIMScopingCriteria -SyncRuleId 5
```

```powershell title="Get a specific group"
Get-JIMScopingCriteria -SyncRuleId 5 -GroupId 2
```

```powershell title="Pipeline from Get-JIMSyncRule"
Get-JIMSyncRule -Id 5 | Get-JIMScopingCriteria
```

---

## New-JIMScopingCriteriaGroup

Creates a new scoping criteria group on a sync rule. Groups evaluate their contents using either `All` (AND) or `Any` (OR) logic. Groups can be nested within other groups to build complex scoping expressions.

### Syntax

```powershell
New-JIMScopingCriteriaGroup -SyncRuleId <int>
    [-ParentGroupId <int>] [-Type <string>] [-Position <int>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the sync rule. Accepts pipeline input. |
| `ParentGroupId` | `int` | No | | The ID of a parent group to nest this group within |
| `Type` | `string` | No | `All` | Evaluation logic for the group. Valid values: `All` (AND), `Any` (OR). |
| `Position` | `int` | No | `0` | Display order position within the parent context |
| `PassThru` | `switch` | No | `$false` | Returns the created group object |

### Output

With `-PassThru`, returns the created scoping criteria group object. Without it, returns nothing.

**ShouldProcess impact level:** Medium.

### Examples

```powershell title="Create a top-level AND group"
New-JIMScopingCriteriaGroup -SyncRuleId 5 -Type All -PassThru
```

```powershell title="Create a nested OR group inside an existing group"
New-JIMScopingCriteriaGroup -SyncRuleId 5 -ParentGroupId 2 -Type Any
```

```powershell title="Create an AND group at a specific position"
New-JIMScopingCriteriaGroup -SyncRuleId 5 -Type All -Position 1
```

---

## Set-JIMScopingCriteriaGroup

Modifies an existing scoping criteria group; for example, changing the evaluation type or position.

### Syntax

```powershell
Set-JIMScopingCriteriaGroup -SyncRuleId <int> -GroupId <int>
    [-Type <string>] [-Position <int>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the sync rule. Accepts pipeline input. |
| `GroupId` | `int` | Yes | | The ID of the group to modify. Accepts pipeline input. Alias: `Id`. |
| `Type` | `string` | No | | Evaluation logic. Valid values: `All` (AND), `Any` (OR). |
| `Position` | `int` | No | | Display order position |
| `PassThru` | `switch` | No | `$false` | Returns the updated group object |

### Output

With `-PassThru`, returns the updated scoping criteria group object. Without it, returns nothing.

**ShouldProcess impact level:** Medium.

### Examples

```powershell title="Change a group from AND to OR"
Set-JIMScopingCriteriaGroup -SyncRuleId 5 -GroupId 2 -Type Any
```

```powershell title="Reorder a group"
Set-JIMScopingCriteriaGroup -SyncRuleId 5 -GroupId 2 -Position 3
```

---

## Remove-JIMScopingCriteriaGroup

Deletes a scoping criteria group and all of its nested criteria and child groups.

### Syntax

```powershell
Remove-JIMScopingCriteriaGroup -SyncRuleId <int> -GroupId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the sync rule. Accepts pipeline input. |
| `GroupId` | `int` | Yes | | The ID of the group to delete. Accepts pipeline input. Alias: `Id`. |

### Output

None.

**ShouldProcess impact level:** High. Prompts for confirmation.

### Notes

- Deleting a group also deletes all nested criteria and child groups within it. This operation is not reversible.

### Examples

```powershell title="Delete a scoping criteria group"
Remove-JIMScopingCriteriaGroup -SyncRuleId 5 -GroupId 2
```

---

## New-JIMScopingCriterion

Adds an individual scoping criterion to a group. Each criterion compares an attribute value against a specified constant. Import rules use connected system attributes; export rules use metaverse attributes.

### Syntax

```powershell
# By metaverse attribute ID
New-JIMScopingCriterion -SyncRuleId <int> -GroupId <int>
    -MetaverseAttributeId <int> -ComparisonType <string>
    [-StringValue <string>] [-IntValue <int>] [-DateTimeValue <datetime>]
    [-BoolValue <bool>] [-GuidValue <guid>] [-PassThru]

# By metaverse attribute name
New-JIMScopingCriterion -SyncRuleId <int> -GroupId <int>
    -MetaverseAttributeName <string> -ComparisonType <string>
    [-StringValue <string>] [-IntValue <int>] [-DateTimeValue <datetime>]
    [-BoolValue <bool>] [-GuidValue <guid>] [-PassThru]

# By connected system attribute ID
New-JIMScopingCriterion -SyncRuleId <int> -GroupId <int>
    -ConnectedSystemAttributeId <int> -ComparisonType <string>
    [-StringValue <string>] [-IntValue <int>] [-DateTimeValue <datetime>]
    [-BoolValue <bool>] [-GuidValue <guid>] [-PassThru]

# By connected system attribute name
New-JIMScopingCriterion -SyncRuleId <int> -GroupId <int>
    -ConnectedSystemAttributeName <string> -ComparisonType <string>
    [-StringValue <string>] [-IntValue <int>] [-DateTimeValue <datetime>]
    [-BoolValue <bool>] [-GuidValue <guid>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the sync rule |
| `GroupId` | `int` | Yes | | The ID of the scoping criteria group to add this criterion to |
| `MetaverseAttributeId` | `int` | Yes (ByMvId set) | | The metaverse attribute ID to evaluate (export rules only) |
| `MetaverseAttributeName` | `string` | Yes (ByMvName set) | | The metaverse attribute name to evaluate; auto-resolves to ID (export rules only) |
| `ConnectedSystemAttributeId` | `int` | Yes (ByCsId set) | | The connected system attribute ID to evaluate (import rules only) |
| `ConnectedSystemAttributeName` | `string` | Yes (ByCsName set) | | The connected system attribute name to evaluate; auto-resolves to ID (import rules only) |
| `ComparisonType` | `string` | Yes | | The comparison operator. Valid values: `Equals`, `NotEquals`, `StartsWith`, `NotStartsWith`, `EndsWith`, `NotEndsWith`, `Contains`, `NotContains`, `LessThan`, `LessThanOrEquals`, `GreaterThan`, `GreaterThanOrEquals`. |
| `StringValue` | `string` | No | | String value to compare against |
| `IntValue` | `int` | No | | Integer value to compare against |
| `DateTimeValue` | `datetime` | No | | Date/time value to compare against (ISO 8601 format) |
| `BoolValue` | `bool` | No | | Boolean value to compare against |
| `GuidValue` | `guid` | No | | GUID value to compare against |
| `PassThru` | `switch` | No | `$false` | Returns the created criterion object |

### Output

With `-PassThru`, returns the created scoping criterion object. Without it, returns nothing.

**ShouldProcess impact level:** Medium.

### Notes

- Export rules only support metaverse attributes. Import rules only support connected system attributes.
- Exactly one comparison value parameter should be provided; the correct parameter depends on the attribute's data type.

### Examples

```powershell title="Import scope: only process users where objectClass equals 'user'"
New-JIMScopingCriterion -SyncRuleId 5 -GroupId 2 `
    -ConnectedSystemAttributeName "objectClass" `
    -ComparisonType Equals `
    -StringValue "user"
```

```powershell title="Import scope: employee ID greater than 1000"
New-JIMScopingCriterion -SyncRuleId 5 -GroupId 2 `
    -ConnectedSystemAttributeId 14 `
    -ComparisonType GreaterThan `
    -IntValue 1000
```

```powershell title="Export scope: only export active metaverse persons"
New-JIMScopingCriterion -SyncRuleId 8 -GroupId 3 `
    -MetaverseAttributeName "accountEnabled" `
    -ComparisonType Equals `
    -BoolValue $true
```

```powershell title="Import scope: department starts with 'Engineering'"
New-JIMScopingCriterion -SyncRuleId 5 -GroupId 2 `
    -ConnectedSystemAttributeName "department" `
    -ComparisonType StartsWith `
    -StringValue "Engineering"
```

```powershell title="Export scope: modified after a specific date"
New-JIMScopingCriterion -SyncRuleId 8 -GroupId 3 `
    -MetaverseAttributeId 20 `
    -ComparisonType GreaterThanOrEquals `
    -DateTimeValue "2025-01-01T00:00:00Z"
```

---

## Remove-JIMScopingCriterion

Deletes a single scoping criterion from a group.

### Syntax

```powershell
Remove-JIMScopingCriterion -SyncRuleId <int> -GroupId <int> -CriterionId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the sync rule |
| `GroupId` | `int` | Yes | | The ID of the scoping criteria group |
| `CriterionId` | `int` | Yes | | The ID of the criterion to delete. Alias: `Id`. |

### Output

None.

**ShouldProcess impact level:** High. Prompts for confirmation.

### Examples

```powershell title="Delete a scoping criterion"
Remove-JIMScopingCriterion -SyncRuleId 5 -GroupId 2 -CriterionId 7
```

---

### Object Matching Rules

Matching rules determine how JIM links connected system objects to metaverse objects during synchronisation. JIM supports two matching modes:

- **Per-object-type (simple):** matching rules are defined at the connected system level and apply to all sync rules for a given object type. This is the default mode.
- **Per-sync-rule (advanced):** each sync rule has its own independent matching rules, allowing different sync rules to use different join criteria.

Use [Switch-JIMMatchingMode](#switch-jimmatchingmode) to change between modes. The current mode determines which set of cmdlets to use.

---

## Switch-JIMMatchingMode

Switches a connected system between per-object-type (simple) and per-sync-rule (advanced) matching modes. Existing matching rules are migrated automatically during the switch.

### Syntax

```powershell
Switch-JIMMatchingMode -ConnectedSystemId <int> -Mode <string> [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | The ID of the connected system. Accepts pipeline input. |
| `Mode` | `string` | Yes | | Target matching mode. Valid values: `ConnectedSystem` (simple, per-object-type), `SyncRule` (advanced, per-sync-rule). |
| `PassThru` | `switch` | No | `$false` | Returns the updated connected system object |

### Output

With `-PassThru`, returns the connected system object reflecting the new mode. Without it, returns nothing.

**ShouldProcess impact level:** High. Prompts for confirmation.

### Notes

- `ConnectedSystem` mode defines matching rules at the object type level; all sync rules for that object type share the same matching configuration.
- `SyncRule` mode defines matching rules on each sync rule independently, providing fine-grained control.
- When switching modes, existing rules are migrated automatically. Review the migrated rules after switching to confirm they are correct.

### Examples

```powershell title="Switch to advanced per-sync-rule matching"
Switch-JIMMatchingMode -ConnectedSystemId 1 -Mode SyncRule
```

```powershell title="Switch back to simple per-object-type matching"
Switch-JIMMatchingMode -ConnectedSystemId 1 -Mode ConnectedSystem
```

---

## Per-Object-Type Matching Rules

These cmdlets manage matching rules in simple (per-object-type) mode, where rules are defined at the connected system level.

### Get-JIMMatchingRule

Retrieves matching rules for a connected system.

#### Syntax

```powershell
# By object type
Get-JIMMatchingRule -ConnectedSystemId <int> -ObjectTypeId <int>

# By rule ID
Get-JIMMatchingRule -ConnectedSystemId <int> -Id <int>
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | The ID of the connected system. Accepts pipeline input. |
| `ObjectTypeId` | `int` | Yes (ByObjectType set) | | The object type ID to retrieve matching rules for |
| `Id` | `int` | Yes (ById set) | | The ID of a specific matching rule to retrieve |

#### Output

Returns one or more matching rule objects containing source/target attribute mappings, order, and case sensitivity settings.

#### Examples

```powershell title="List matching rules for a connected system object type"
Get-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 3
```

```powershell title="Get a specific matching rule"
Get-JIMMatchingRule -ConnectedSystemId 1 -Id 5
```

---

### New-JIMMatchingRule

Creates a new matching rule for a connected system object type. Rules can match on a connected system attribute or a metaverse attribute as the source.

#### Syntax

```powershell
# Source: connected system attribute
New-JIMMatchingRule -ConnectedSystemId <int> -ObjectTypeId <int>
    -MetaverseObjectTypeId <int> -SourceAttributeId <int>
    -TargetMetaverseAttributeId <int> [-Order <int>]
    [-CaseSensitive <bool>] [-PassThru]

# Source: metaverse attribute
New-JIMMatchingRule -ConnectedSystemId <int> -ObjectTypeId <int>
    -MetaverseObjectTypeId <int> -SourceMetaverseAttributeId <int>
    -TargetMetaverseAttributeId <int> [-Order <int>]
    [-CaseSensitive <bool>] [-PassThru]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | The ID of the connected system |
| `ObjectTypeId` | `int` | Yes | | The connected system object type ID |
| `MetaverseObjectTypeId` | `int` | Yes | | The metaverse object type ID to match against |
| `SourceAttributeId` | `int` | Yes (CSAttribute set) | | The connected system attribute ID to use as the match source |
| `SourceMetaverseAttributeId` | `int` | Yes (MVAttribute set) | | The metaverse attribute ID to use as the match source |
| `TargetMetaverseAttributeId` | `int` | Yes | | The metaverse attribute ID to match against |
| `Order` | `int` | No | | Evaluation order; lower numbers are evaluated first |
| `CaseSensitive` | `bool` | No | `$false` | Whether the match comparison is case-sensitive |
| `PassThru` | `switch` | No | `$false` | Returns the created matching rule object |

#### Examples

```powershell title="Match CS employeeId to MV employeeId"
New-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 3 `
    -MetaverseObjectTypeId 1 `
    -SourceAttributeId 10 `
    -TargetMetaverseAttributeId 5 `
    -PassThru
```

```powershell title="Case-sensitive match on email"
New-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 3 `
    -MetaverseObjectTypeId 1 `
    -SourceAttributeId 12 `
    -TargetMetaverseAttributeId 8 `
    -CaseSensitive $true
```

---

### Set-JIMMatchingRule

Modifies an existing per-object-type matching rule. Setting a source attribute replaces all existing source attributes on the rule.

#### Syntax

```powershell
Set-JIMMatchingRule -ConnectedSystemId <int> -Id <int>
    [-Order <int>] [-MetaverseObjectTypeId <int>]
    [-TargetMetaverseAttributeId <int>] [-SourceAttributeId <int>]
    [-SourceMetaverseAttributeId <int>] [-CaseSensitive <bool>] [-PassThru]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | The ID of the connected system |
| `Id` | `int` | Yes | | The ID of the matching rule to modify |
| `Order` | `int` | No | | New evaluation order |
| `MetaverseObjectTypeId` | `int` | No | | New metaverse object type ID |
| `TargetMetaverseAttributeId` | `int` | No | | New target metaverse attribute ID |
| `SourceAttributeId` | `int` | No | | New connected system source attribute ID |
| `SourceMetaverseAttributeId` | `int` | No | | New metaverse source attribute ID |
| `CaseSensitive` | `bool` | No | | Whether the match comparison is case-sensitive |
| `PassThru` | `switch` | No | `$false` | Returns the updated matching rule object |

#### Notes

- Setting `SourceAttributeId` or `SourceMetaverseAttributeId` replaces all existing source attributes on the rule.

#### Examples

```powershell title="Change the evaluation order"
Set-JIMMatchingRule -ConnectedSystemId 1 -Id 5 -Order 2
```

```powershell title="Enable case-sensitive matching"
Set-JIMMatchingRule -ConnectedSystemId 1 -Id 5 -CaseSensitive $true
```

---

### Remove-JIMMatchingRule

Deletes a per-object-type matching rule.

#### Syntax

```powershell
Remove-JIMMatchingRule -ConnectedSystemId <int> -Id <int> [-Force]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | The ID of the connected system |
| `Id` | `int` | Yes | | The ID of the matching rule to delete |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt |

#### Output

None.

**ShouldProcess impact level:** High. Prompts for confirmation unless `-Force` is specified.

#### Examples

```powershell title="Delete a matching rule"
Remove-JIMMatchingRule -ConnectedSystemId 1 -Id 5
```

```powershell title="Force delete without confirmation"
Remove-JIMMatchingRule -ConnectedSystemId 1 -Id 5 -Force
```

---

## Per-Sync-Rule Matching Rules

These cmdlets manage matching rules in advanced (per-sync-rule) mode, where each sync rule defines its own matching configuration independently.

### Get-JIMSyncRuleMatchingRule

Retrieves matching rules for a specific sync rule.

#### Syntax

```powershell
# All matching rules for a sync rule
Get-JIMSyncRuleMatchingRule -SyncRuleId <int>

# Specific matching rule
Get-JIMSyncRuleMatchingRule -SyncRuleId <int> -Id <int>
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the sync rule. Accepts pipeline input. |
| `Id` | `int` | No | | The ID of a specific matching rule to retrieve |

#### Output

Returns one or more matching rule objects.

#### Examples

```powershell title="List matching rules for a sync rule"
Get-JIMSyncRuleMatchingRule -SyncRuleId 5
```

```powershell title="Get a specific matching rule"
Get-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 3
```

---

### New-JIMSyncRuleMatchingRule

Creates a new matching rule on a specific sync rule. The metaverse object type is derived automatically from the sync rule configuration.

#### Syntax

```powershell
# Source: connected system attribute
New-JIMSyncRuleMatchingRule -SyncRuleId <int>
    -SourceAttributeId <int> -TargetMetaverseAttributeId <int>
    [-Order <int>] [-CaseSensitive <bool>] [-PassThru]

# Source: metaverse attribute
New-JIMSyncRuleMatchingRule -SyncRuleId <int>
    -SourceMetaverseAttributeId <int> -TargetMetaverseAttributeId <int>
    [-Order <int>] [-CaseSensitive <bool>] [-PassThru]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the sync rule |
| `SourceAttributeId` | `int` | Yes (CSAttribute set) | | The connected system attribute ID to use as the match source |
| `SourceMetaverseAttributeId` | `int` | Yes (MVAttribute set) | | The metaverse attribute ID to use as the match source |
| `TargetMetaverseAttributeId` | `int` | Yes | | The metaverse attribute ID to match against |
| `Order` | `int` | No | | Evaluation order; lower numbers are evaluated first |
| `CaseSensitive` | `bool` | No | `$false` | Whether the match comparison is case-sensitive |
| `PassThru` | `switch` | No | `$false` | Returns the created matching rule object |

#### Notes

- The metaverse object type is derived from the sync rule, so you do not need to specify it explicitly.

#### Examples

```powershell title="Match CS employeeId to MV employeeId on a sync rule"
New-JIMSyncRuleMatchingRule -SyncRuleId 5 `
    -SourceAttributeId 10 `
    -TargetMetaverseAttributeId 5 `
    -PassThru
```

```powershell title="Case-sensitive email match"
New-JIMSyncRuleMatchingRule -SyncRuleId 5 `
    -SourceAttributeId 12 `
    -TargetMetaverseAttributeId 8 `
    -CaseSensitive $true
```

---

### Set-JIMSyncRuleMatchingRule

Modifies an existing per-sync-rule matching rule.

#### Syntax

```powershell
Set-JIMSyncRuleMatchingRule -SyncRuleId <int> -Id <int>
    [-Order <int>] [-TargetMetaverseAttributeId <int>]
    [-SourceAttributeId <int>] [-SourceMetaverseAttributeId <int>]
    [-CaseSensitive <bool>] [-PassThru]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the sync rule |
| `Id` | `int` | Yes | | The ID of the matching rule to modify |
| `Order` | `int` | No | | New evaluation order |
| `TargetMetaverseAttributeId` | `int` | No | | New target metaverse attribute ID |
| `SourceAttributeId` | `int` | No | | New connected system source attribute ID |
| `SourceMetaverseAttributeId` | `int` | No | | New metaverse source attribute ID |
| `CaseSensitive` | `bool` | No | | Whether the match comparison is case-sensitive |
| `PassThru` | `switch` | No | `$false` | Returns the updated matching rule object |

#### Examples

```powershell title="Change evaluation order"
Set-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 3 -Order 1
```

```powershell title="Update target attribute and enable case sensitivity"
Set-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 3 `
    -TargetMetaverseAttributeId 9 `
    -CaseSensitive $true
```

---

### Remove-JIMSyncRuleMatchingRule

Deletes a per-sync-rule matching rule.

#### Syntax

```powershell
Remove-JIMSyncRuleMatchingRule -SyncRuleId <int> -Id <int> [-Force]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the sync rule |
| `Id` | `int` | Yes | | The ID of the matching rule to delete |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt |

#### Output

None.

**ShouldProcess impact level:** High. Prompts for confirmation unless `-Force` is specified.

#### Examples

```powershell title="Delete a sync rule matching rule"
Remove-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 3
```

```powershell title="Force delete without confirmation"
Remove-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 3 -Force
```

---

## See also

- [API Sync Rules](../api/sync-rules/index.md): REST API reference for sync rule endpoints
- [Connected Systems](connected-systems.md): PowerShell cmdlets for managing connected systems
- [Metaverse](metaverse.md): PowerShell cmdlets for managing the metaverse
