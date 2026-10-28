---
title: Object Matching Rules
description: "The cmdlets that switch a Connected System's matching mode and manage its Object Matching Rules, per Object Type or per Synchronisation Rule."
type: reference
audience: automation-author
---

# Object Matching Rules

The Object Matching Rule cmdlets manage how JIM pairs a Connected System Object with an existing Metaverse Object, on import and before provisioning, and switch where those rules live. In simple mode, the default, a Connected System's rules sit on its Object Types and every Synchronisation Rule for the type shares them: the `*-JIMMatchingRule` cmdlets manage them. In advanced mode each Synchronisation Rule has its own, managed with the `*-JIMSyncRuleMatchingRule` cmdlets. JIM consults only the rules of the mode a Connected System is in, and refuses to create a rule of the other kind. How matching works, and when to use each mode, is covered in [Object Matching Rules](../configuration/object-matching-rules.md).

```jim-diagram
archetype: lifecycle
title: Switching a Connected System between matching modes
desc: 'A Connected System is in one of two matching modes, and JIM consults only that mode''s Object Matching Rules. Simple mode, the default, keeps the rules on its Object Types, managed with the *-JIMMatchingRule cmdlets. Advanced mode gives each Synchronisation Rule its own, managed with the *-JIMSyncRuleMatchingRule cmdlets. Switch-JIMMatchingMode -Mode SyncRule moves it to advanced mode and copies each Object Type''s rules onto its import Synchronisation Rules that have none; export rules receive nothing. Switch-JIMMatchingMode -Mode ConnectedSystem moves it back to simple mode and moves the Synchronisation Rules'' rules onto their Object Types. JIM refuses to create a rule of the other mode''s kind.'
container: {label: Connected System, note: '`Switch-JIMMatchingMode` moves it between modes; JIM consults only the current mode''s rules'}
states:
- {id: simple, label: Simple mode, sub: 'the default: rules on each Object Type'}
- {id: advanced, label: Advanced mode, sub: rules on each Synchronisation Rule}
transitions:
- {from: simple, to: advanced, label: '`-Mode SyncRule`', note: copies rules onto import rules}
- {from: advanced, to: simple, label: '`-Mode ConnectedSystem`', note: moves rules onto Object Types}
```

---

## :material-toggle-switch-variant: Switch-JIMMatchingMode

Switches a Connected System between simple mode, where its Object Matching Rules sit on its Object Types, and advanced mode, where each Synchronisation Rule has its own. The rules move with the switch.

<!-- BEGIN GENERATED: Switch-JIMMatchingMode -->
### Syntax

```powershell
Switch-JIMMatchingMode -ConnectedSystemId <int> -Mode <string> [-PassThru] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. Accepts pipeline input by property name. |
| `Mode` | `string` | Yes |  | The mode to switch to: ConnectedSystem (simple mode) keeps the rules on Object Types, and SyncRule (advanced mode) on each Synchronisation Rule. One of `ConnectedSystem` or `SyncRule`. |
| `PassThru` | `switch` | No |  | Returns what the switch did. |

### Output

With `-PassThru`, what the switch did; otherwise nothing. Its warnings are written to the warning stream either way.

| Property | Type | Description |
|----------|------|-------------|
| `Success` | `bool` | Whether the switch was made. |
| `NewMode` | `string` | The mode the Connected System is now in: ConnectedSystem (simple mode, rules on Object Types) or SyncRule (advanced mode, rules on Synchronisation Rules). One of `ConnectedSystem` or `SyncRule`. |
| `SyncRulesUpdated` | `int` | How many import Synchronisation Rules received a copy of their Object Type's rules, when switching to advanced mode; 0 otherwise. One that already had rules of its own keeps them. |
| `ObjectTypesUpdated` | `int` | How many Object Types received rules, when switching to simple mode; 0 otherwise. |
| `Warnings` | `string[]` | What the switch left behind or discarded, one sentence each: rules kept but no longer consulted, export Synchronisation Rules left with none, rules that differed between Synchronisation Rules, and rules discarded because an Object Type already had its own. |
| `ObjectTypeMigrations` | `object[]` | When switching to simple mode, what happened on each Object Type; empty otherwise. Each has `ObjectTypeId`, `ObjectTypeName`, `SyncRuleCount`, `SyncRulesWithMatchingRules`, `UniqueSyncRuleConfigurations`, `RulesDiverged`, `ObjectTypeRulesTookPrecedence`, `MatchingRulesSet` and `SyncRulesCleared`. |
| `ErrorMessage` | `string` | Why the switch failed; empty when it succeeded. |
<!-- END GENERATED: Switch-JIMMatchingMode -->

### Notes

- Switching to advanced mode copies each Object Type's rules onto its import Synchronisation Rules that have none of their own. The Object Types' rules are kept, unconsulted, and come back into effect if the Connected System switches back. Export Synchronisation Rules receive nothing, so export matching stops for them until they have rules of their own.
- Switching to simple mode moves the Synchronisation Rules' rules onto their Object Types. Where the Synchronisation Rules for one Object Type have different rules, the most common set is used; where the Object Type already has rules, those stay and the Synchronisation Rules' rules are discarded.
- Each of these is written to the warning stream, so check the rules after a switch.

### Examples

```powershell title="Switch to advanced mode, with rules on each Synchronisation Rule"
Switch-JIMMatchingMode -ConnectedSystemId 1 -Mode SyncRule
```

```powershell title="Switch back to simple mode, and see what moved"
Switch-JIMMatchingMode -ConnectedSystemId 1 -Mode ConnectedSystem -PassThru |
    Select-Object -ExpandProperty ObjectTypeMigrations
```

---

## :material-vector-link: Get-JIMMatchingRule

Gets the Object Matching Rules on a Connected System Object Type, in the order JIM tries them, or one rule by ID. These are the rules simple mode uses.

<!-- BEGIN GENERATED: Get-JIMMatchingRule -->
### Syntax

```powershell
# ByObjectType (default)
Get-JIMMatchingRule -ConnectedSystemId <int> -ObjectTypeId <int>

# ById
Get-JIMMatchingRule -ConnectedSystemId <int> -Id <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. Accepts pipeline input by property name. |
| `ObjectTypeId` | `int` | Yes (ByObjectType) |  | The ID of the Connected System Object Type whose rules to get. Accepts pipeline input by property name. |
| `Id` | `int` | Yes (ById) |  | The ID of the rule to get. |

### Output

The rules, in the order JIM tries them, or the one rule.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The ID of the rule. |
| `Order` | `int` | Where the rule comes in the order JIM tries the rules, lowest first. |
| `ConnectedSystemObjectTypeId` | `int` | The ID of the Connected System Object Type the rule is on, for a simple mode rule; 0 for an advanced mode rule, which is on a Synchronisation Rule instead. |
| `ConnectedSystemObjectTypeName` | `string` | The name of the Connected System Object Type the rule is on; empty for an advanced mode rule. |
| `MetaverseObjectTypeId` | `int` | The ID of the Metaverse Object Type whose objects a simple mode rule searches; empty for an advanced mode rule, which searches its Synchronisation Rule's Metaverse Object Type. |
| `MetaverseObjectTypeName` | `string` | The name of that Metaverse Object Type; empty for an advanced mode rule. |
| `SyncRuleId` | `int` | The ID of the Synchronisation Rule an advanced mode rule is on; empty for a simple mode rule. |
| `TargetMetaverseAttributeId` | `int` | The ID of the Metaverse attribute the source is matched against. |
| `TargetMetaverseAttributeName` | `string` | The name of the Metaverse attribute the source is matched against. |
| `Sources` | `object[]` | The rule's source: one entry, naming a Connected System attribute or an expression. Each has `Id`, `Order`, `ConnectedSystemAttributeId`, `ConnectedSystemAttributeName` and `Expression`. |
| `CaseSensitive` | `bool` | Whether the comparison respects case; false ignores it. |
| `Created` | `datetime` | When the rule was created. |
| `ConnectedSystemId` | `int` | The ID of the Connected System the rule belongs to, so the rule can be piped to `Set-JIMMatchingRule` and `Remove-JIMMatchingRule`. |
<!-- END GENERATED: Get-JIMMatchingRule -->

### Examples

```powershell title="List matching rules for a Connected System Object Type"
Get-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 3
```

```powershell title="Get a specific matching rule"
Get-JIMMatchingRule -ConnectedSystemId 1 -Id 5
```

---

## :material-plus-circle-outline: New-JIMMatchingRule

Creates an Object Matching Rule on a Connected System Object Type: a Connected System attribute, or an expression over the Connected System Object, matched against a Metaverse attribute on the Metaverse Object Type the rule searches.

The Connected System must be in simple mode. In advanced mode JIM refuses the rule, because it would never be consulted: switch the mode first with [Switch-JIMMatchingMode](#switch-jimmatchingmode), or use [New-JIMSyncRuleMatchingRule](#new-jimsyncrulematchingrule).

<!-- BEGIN GENERATED: New-JIMMatchingRule -->
### Syntax

```powershell
# Attribute (default)
New-JIMMatchingRule -ConnectedSystemId <int> -ObjectTypeId <int> -MetaverseObjectTypeId <int> -SourceAttributeId <int> -TargetMetaverseAttributeId <int> [-Order <int>] [-CaseSensitive <bool>] [-PassThru] [-WhatIf] [-Confirm]

# Expression
New-JIMMatchingRule -ConnectedSystemId <int> -ObjectTypeId <int> -MetaverseObjectTypeId <int> -SourceExpression <string> -TargetMetaverseAttributeId <int> [-Order <int>] [-CaseSensitive <bool>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. Accepts pipeline input by property name. |
| `ObjectTypeId` | `int` | Yes |  | The ID of the Connected System Object Type the rule is for. |
| `MetaverseObjectTypeId` | `int` | Yes |  | The ID of the Metaverse Object Type whose objects the rule searches. |
| `SourceAttributeId` | `int` | Yes (Attribute) |  | The ID of the Connected System attribute whose values are matched against `-TargetMetaverseAttributeId`, for import and export matching alike. Give this or `-SourceExpression`. |
| `SourceExpression` | `string` | Yes (Expression) |  | An expression computing the value to match on, instead of a Connected System attribute, for an identifier held differently in each system: for example '"E" + Trim(cs["employeeId"])'. It reads the Connected System Object only, through cs["attributeName"]. Its result is converted to the type of `-TargetMetaverseAttributeId`, and a list of text values is matched value by value. JIM refuses an expression that does not parse, reads mv["..."], reads no cs["..."] attribute or one the Object Type does not have, or calls a function that returns a different value each time. |
| `TargetMetaverseAttributeId` | `int` | Yes |  | The ID of the Metaverse attribute the source is matched against. |
| `Order` | `int` | No |  | Where the rule comes in the order JIM tries the rules, lowest first. Without it, the rule goes after the existing ones. |
| `CaseSensitive` | `bool` | No |  | Whether the comparison respects case: with `$true`, 'emp123' does not match 'EMP123'. When omitted, follows the source: respects case when a source attribute's values are declared case-exact, and ignores it otherwise. |
| `PassThru` | `switch` | No |  | Returns the rule created. |

### Output

With `-PassThru`, the rule created; otherwise nothing.

Properties as under [Get-JIMMatchingRule](#get-jimmatchingrule).
<!-- END GENERATED: New-JIMMatchingRule -->

### Examples

```powershell title="Match the directory's employeeId with the Metaverse employee ID"
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

```powershell title="Match an HR number held without its E prefix"
New-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 3 `
    -MetaverseObjectTypeId 1 `
    -SourceExpression '"E" + Trim(cs["employeeId"])' `
    -TargetMetaverseAttributeId 5
```

---

## :material-pencil-outline: Set-JIMMatchingRule

Changes an Object Matching Rule on a Connected System Object Type. What you leave out stays as it is; a source attribute or expression replaces the rule's source.

<!-- BEGIN GENERATED: Set-JIMMatchingRule -->
### Syntax

```powershell
# NoSource (default)
Set-JIMMatchingRule -ConnectedSystemId <int> -Id <int> [-Order <int>] [-MetaverseObjectTypeId <int>] [-TargetMetaverseAttributeId <int>] [-CaseSensitive <bool>] [-SaveAnyway] [-AcceptNewResult] [-PassThru] [-WhatIf] [-Confirm]

# Attribute
Set-JIMMatchingRule -ConnectedSystemId <int> -Id <int> -SourceAttributeId <int> [-Order <int>] [-MetaverseObjectTypeId <int>] [-TargetMetaverseAttributeId <int>] [-CaseSensitive <bool>] [-SaveAnyway] [-AcceptNewResult] [-PassThru] [-WhatIf] [-Confirm]

# Expression
Set-JIMMatchingRule -ConnectedSystemId <int> -Id <int> -SourceExpression <string> [-Order <int>] [-MetaverseObjectTypeId <int>] [-TargetMetaverseAttributeId <int>] [-CaseSensitive <bool>] [-SaveAnyway] [-AcceptNewResult] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. Accepts pipeline input by property name. |
| `Id` | `int` | Yes |  | The ID of the rule to change. Accepts pipeline input by property name. |
| `Order` | `int` | No |  | Where the rule comes in the order JIM tries the rules, lowest first. |
| `MetaverseObjectTypeId` | `int` | No |  | The ID of the Metaverse Object Type whose objects the rule searches. |
| `TargetMetaverseAttributeId` | `int` | No |  | The ID of the Metaverse attribute the source is matched against. |
| `SourceAttributeId` | `int` | Yes (Attribute) |  | Replaces the rule's source with this Connected System attribute. Cannot be used with `-SourceExpression`; give neither to leave the source as it is. |
| `SourceExpression` | `string` | Yes (Expression) |  | Replaces the rule's source with an expression computing the value to match on: for example '"E" + Trim(cs["employeeId"])'. It reads the Connected System Object only, through cs["attributeName"]. Its result is converted to the type of the rule's Metaverse attribute, and a list of text values is matched value by value. JIM refuses an expression that does not parse, reads mv["..."], reads no cs["..."] attribute or one the Object Type does not have, or calls a function that returns a different value each time. Cannot be used with `-SourceAttributeId`. |
| `CaseSensitive` | `bool` | No |  | Whether the comparison respects case: with `$true`, 'emp123' does not match 'EMP123'. Omit it to leave it as it is. |
| `SaveAnyway` | `switch` | No |  | Saves even though the change stops configuration test cases passing. Without it, or `-AcceptNewResult`, such a change is refused, nothing is saved, and the error names each case. Configuration History records the cases saved past, by name. |
| `AcceptNewResult` | `switch` | No |  | Takes what the configuration now gives as what each configuration test case that stopped passing expects, rewriting those cases, and saves. A case whose new result cannot be expected (it needs review, could not run, or its Scope cannot be determined) still refuses the change unless `-SaveAnyway` is given too. |
| `PassThru` | `switch` | No |  | Returns the rule as changed. |

### Output

With `-PassThru`, the rule as changed; otherwise nothing.

Properties as under [Get-JIMMatchingRule](#get-jimmatchingrule).
<!-- END GENERATED: Set-JIMMatchingRule -->

### Examples

```powershell title="Change the evaluation order"
Set-JIMMatchingRule -ConnectedSystemId 1 -Id 5 -Order 2
```

```powershell title="Enable case-sensitive matching"
Set-JIMMatchingRule -ConnectedSystemId 1 -Id 5 -CaseSensitive $true
```

```powershell title="Match on an expression instead of an attribute"
Set-JIMMatchingRule -ConnectedSystemId 1 -Id 5 -SourceExpression '"E" + Trim(cs["employeeId"])'
```

---

## :material-trash-can-outline: Remove-JIMMatchingRule

Deletes an Object Matching Rule from a Connected System Object Type.

<!-- BEGIN GENERATED: Remove-JIMMatchingRule -->
### Syntax

```powershell
Remove-JIMMatchingRule -ConnectedSystemId <int> -Id <int> [-Force] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes |  | The ID of the Connected System. Accepts pipeline input by property name. |
| `Id` | `int` | Yes |  | The ID of the rule to delete. Accepts pipeline input by property name. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. |

### Output

None.
<!-- END GENERATED: Remove-JIMMatchingRule -->

### Examples

```powershell title="Delete a matching rule"
Remove-JIMMatchingRule -ConnectedSystemId 1 -Id 5
```

```powershell title="Delete without confirmation"
Remove-JIMMatchingRule -ConnectedSystemId 1 -Id 5 -Force
```

---

## :material-link-variant: Get-JIMSyncRuleMatchingRule

Gets the Object Matching Rules on a Synchronisation Rule, in the order JIM tries them, or one rule by ID. These are the rules advanced mode uses.

<!-- BEGIN GENERATED: Get-JIMSyncRuleMatchingRule -->
### Syntax

```powershell
# BySyncRule (default)
Get-JIMSyncRuleMatchingRule -SyncRuleId <int>

# ById
Get-JIMSyncRuleMatchingRule -SyncRuleId <int> [-Id <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule. Accepts pipeline input by property name. |
| `Id` | `int` | No (ById) |  | The ID of the rule to get. |

### Output

The rules, in the order JIM tries them, or the one rule.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The ID of the rule. |
| `Order` | `int` | Where the rule comes in the order JIM tries the rules, lowest first. |
| `ConnectedSystemObjectTypeId` | `int` | The ID of the Connected System Object Type the rule is on, for a simple mode rule; 0 for an advanced mode rule, which is on a Synchronisation Rule instead. |
| `ConnectedSystemObjectTypeName` | `string` | The name of the Connected System Object Type the rule is on; empty for an advanced mode rule. |
| `MetaverseObjectTypeId` | `int` | The ID of the Metaverse Object Type whose objects a simple mode rule searches; empty for an advanced mode rule, which searches its Synchronisation Rule's Metaverse Object Type. |
| `MetaverseObjectTypeName` | `string` | The name of that Metaverse Object Type; empty for an advanced mode rule. |
| `SyncRuleId` | `int` | The ID of the Synchronisation Rule an advanced mode rule is on; empty for a simple mode rule. |
| `TargetMetaverseAttributeId` | `int` | The ID of the Metaverse attribute the source is matched against. |
| `TargetMetaverseAttributeName` | `string` | The name of the Metaverse attribute the source is matched against. |
| `Sources` | `object[]` | The rule's source: one entry, naming a Connected System attribute or an expression. Each has `Id`, `Order`, `ConnectedSystemAttributeId`, `ConnectedSystemAttributeName` and `Expression`. |
| `CaseSensitive` | `bool` | Whether the comparison respects case; false ignores it. |
| `Created` | `datetime` | When the rule was created. |
<!-- END GENERATED: Get-JIMSyncRuleMatchingRule -->

### Examples

```powershell title="List matching rules for a Synchronisation Rule"
Get-JIMSyncRuleMatchingRule -SyncRuleId 5
```

```powershell title="Get a specific matching rule"
Get-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 3
```

---

## :material-link-variant-plus: New-JIMSyncRuleMatchingRule

Creates an Object Matching Rule on a Synchronisation Rule: a Connected System attribute, or an expression over the Connected System Object, matched against a Metaverse attribute. The rule searches the Synchronisation Rule's Metaverse Object Type, so there is none to give.

The Connected System must be in advanced mode. In simple mode JIM refuses the rule, because it would never be consulted: switch the mode first with [Switch-JIMMatchingMode](#switch-jimmatchingmode), or use [New-JIMMatchingRule](#new-jimmatchingrule).

<!-- BEGIN GENERATED: New-JIMSyncRuleMatchingRule -->
### Syntax

```powershell
# Attribute (default)
New-JIMSyncRuleMatchingRule -SyncRuleId <int> -SourceAttributeId <int> -TargetMetaverseAttributeId <int> [-Order <int>] [-CaseSensitive <bool>] [-PassThru] [-WhatIf] [-Confirm]

# Expression
New-JIMSyncRuleMatchingRule -SyncRuleId <int> -SourceExpression <string> -TargetMetaverseAttributeId <int> [-Order <int>] [-CaseSensitive <bool>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule. Accepts pipeline input by property name. |
| `SourceAttributeId` | `int` | Yes (Attribute) |  | The ID of the Connected System attribute whose values are matched against `-TargetMetaverseAttributeId`, for import and export matching alike. Give this or `-SourceExpression`. |
| `SourceExpression` | `string` | Yes (Expression) |  | An expression computing the value to match on, instead of a Connected System attribute, for an identifier held differently in each system: for example '"E" + Trim(cs["employeeId"])'. It reads the Connected System Object only, through cs["attributeName"]. Its result is converted to the type of `-TargetMetaverseAttributeId`, and a list of text values is matched value by value. JIM refuses an expression that does not parse, reads mv["..."], reads no cs["..."] attribute or one the Object Type does not have, or calls a function that returns a different value each time. |
| `TargetMetaverseAttributeId` | `int` | Yes |  | The ID of the Metaverse attribute the source is matched against. |
| `Order` | `int` | No |  | Where the rule comes in the order JIM tries the rules, lowest first. Without it, the rule goes after the existing ones. |
| `CaseSensitive` | `bool` | No |  | Whether the comparison respects case: with `$true`, 'emp123' does not match 'EMP123'. When omitted, follows the source: respects case when a source attribute's values are declared case-exact, and ignores it otherwise. |
| `PassThru` | `switch` | No |  | Returns the rule created. |

### Output

With `-PassThru`, the rule created; otherwise nothing.

Properties as under [Get-JIMSyncRuleMatchingRule](#get-jimsyncrulematchingrule).
<!-- END GENERATED: New-JIMSyncRuleMatchingRule -->

### Examples

```powershell title="Match on employeeId, on a Synchronisation Rule"
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

```powershell title="Match the directory's address trimmed and in lower case"
New-JIMSyncRuleMatchingRule -SyncRuleId 5 `
    -SourceExpression 'Lower(Trim(cs["mail"]))' `
    -TargetMetaverseAttributeId 8
```

---

## :material-link-edit: Set-JIMSyncRuleMatchingRule

Changes an Object Matching Rule on a Synchronisation Rule. What you leave out stays as it is; a source attribute or expression replaces the rule's source.

<!-- BEGIN GENERATED: Set-JIMSyncRuleMatchingRule -->
### Syntax

```powershell
# NoSource (default)
Set-JIMSyncRuleMatchingRule -SyncRuleId <int> -Id <int> [-Order <int>] [-TargetMetaverseAttributeId <int>] [-CaseSensitive <bool>] [-SaveAnyway] [-AcceptNewResult] [-PassThru] [-WhatIf] [-Confirm]

# Attribute
Set-JIMSyncRuleMatchingRule -SyncRuleId <int> -Id <int> -SourceAttributeId <int> [-Order <int>] [-TargetMetaverseAttributeId <int>] [-CaseSensitive <bool>] [-SaveAnyway] [-AcceptNewResult] [-PassThru] [-WhatIf] [-Confirm]

# Expression
Set-JIMSyncRuleMatchingRule -SyncRuleId <int> -Id <int> -SourceExpression <string> [-Order <int>] [-TargetMetaverseAttributeId <int>] [-CaseSensitive <bool>] [-SaveAnyway] [-AcceptNewResult] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule. Accepts pipeline input by property name. |
| `Id` | `int` | Yes |  | The ID of the rule to change. Accepts pipeline input by property name. |
| `Order` | `int` | No |  | Where the rule comes in the order JIM tries the rules, lowest first. |
| `TargetMetaverseAttributeId` | `int` | No |  | The ID of the Metaverse attribute the source is matched against. |
| `SourceAttributeId` | `int` | Yes (Attribute) |  | Replaces the rule's source with this Connected System attribute. Cannot be used with `-SourceExpression`; give neither to leave the source as it is. |
| `SourceExpression` | `string` | Yes (Expression) |  | Replaces the rule's source with an expression computing the value to match on: for example '"E" + Trim(cs["employeeId"])'. It reads the Connected System Object only, through cs["attributeName"]. Its result is converted to the type of the rule's Metaverse attribute, and a list of text values is matched value by value. JIM refuses an expression that does not parse, reads mv["..."], reads no cs["..."] attribute or one the Object Type does not have, or calls a function that returns a different value each time. Cannot be used with `-SourceAttributeId`. |
| `CaseSensitive` | `bool` | No |  | Whether the comparison respects case: with `$true`, 'emp123' does not match 'EMP123'. Omit it to leave it as it is. |
| `SaveAnyway` | `switch` | No |  | Saves even though the change stops configuration test cases passing. Without it, or `-AcceptNewResult`, such a change is refused, nothing is saved, and the error names each case. Configuration History records the cases saved past, by name. |
| `AcceptNewResult` | `switch` | No |  | Takes what the configuration now gives as what each configuration test case that stopped passing expects, rewriting those cases, and saves. A case whose new result cannot be expected (it needs review, could not run, or its Scope cannot be determined) still refuses the change unless `-SaveAnyway` is given too. |
| `PassThru` | `switch` | No |  | Returns the rule as changed. |

### Output

With `-PassThru`, the rule as changed; otherwise nothing.

Properties as under [Get-JIMSyncRuleMatchingRule](#get-jimsyncrulematchingrule).
<!-- END GENERATED: Set-JIMSyncRuleMatchingRule -->

### Examples

```powershell title="Change evaluation order"
Set-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 3 -Order 1
```

```powershell title="Update target attribute and enable case sensitivity"
Set-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 3 `
    -TargetMetaverseAttributeId 9 `
    -CaseSensitive $true
```

---

## :material-link-variant-remove: Remove-JIMSyncRuleMatchingRule

Deletes an Object Matching Rule from a Synchronisation Rule.

<!-- BEGIN GENERATED: Remove-JIMSyncRuleMatchingRule -->
### Syntax

```powershell
Remove-JIMSyncRuleMatchingRule -SyncRuleId <int> -Id <int> [-Force] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule. Accepts pipeline input by property name. |
| `Id` | `int` | Yes |  | The ID of the rule to delete. Accepts pipeline input by property name. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. |

### Output

None.
<!-- END GENERATED: Remove-JIMSyncRuleMatchingRule -->

### Examples

```powershell title="Delete a Synchronisation Rule matching rule"
Remove-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 3
```

```powershell title="Delete without confirmation"
Remove-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 3 -Force
```

---

## :material-test-tube: Test-JIMMatchingRule

Tests what a saved Object Matching Rule would match on for sample values or a real Connected System Object and, with `-LookUp`, which Metaverse Object a synchronisation would join. See [Testing a rule](../configuration/object-matching-rules.md#testing-a-rule).

<!-- BEGIN GENERATED: Test-JIMMatchingRule -->
### Syntax

```powershell
# ConnectedSystem (default)
Test-JIMMatchingRule -ConnectedSystemId <int> -Id <int> [-Attributes <hashtable>] [-ConnectedSystemObjectId <guid>] [-LookUp]

# SyncRule
Test-JIMMatchingRule -SyncRuleId <int> -Id <int> [-Attributes <hashtable>] [-ConnectedSystemObjectId <guid>] [-LookUp]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (ConnectedSystem) |  | The ID of the Connected System the rule belongs to, for a rule on a Connected System Object Type. Accepts pipeline input by property name. |
| `SyncRuleId` | `int` | Yes (SyncRule) |  | The ID of the Synchronisation Rule the rule belongs to, for a rule on a Synchronisation Rule. Accepts pipeline input by property name. |
| `Id` | `int` | Yes |  | The ID of the Object Matching Rule to test. Accepts pipeline input by property name. |
| `Attributes` | `hashtable` | No |  | A hashtable of sample values for the attributes the rule reads, keyed by attribute name. Each value is converted to its attribute's type; `$null` is no value, and an array gives a multi-valued attribute several values. With ConnectedSystemObjectId, a value here replaces the object's value for that attribute. |
| `ConnectedSystemObjectId` | `guid` | No |  | The ID of a Connected System Object of the rule's Object Type whose values to test with. |
| `LookUp` | `switch` | No |  | Also looks the values up in the Metaverse and reports what a synchronisation would do. Nothing is written. |

### Output

What the rule would match on (Outcome, ValueLines and ValueType, or FailureMessage when it fails), the values tested for each attribute it reads (Inputs) and, with `-LookUp`, what the values find in the Metaverse (Lookup). When the values cannot be used, IsValid is `$false` and InputErrors says why.

| Property | Type | Description |
|----------|------|-------------|
| `IsValid` | `bool` | False when the values could not be used; `InputErrors` says why, and nothing was evaluated. |
| `Outcome` | `string` | What the rule produced: values to match on, no value (a synchronisation would try the next rule), or a failure (a synchronisation would stop the object). Empty when `IsValid` is false. One of `Values`, `NoValue` or `Failed`. |
| `ValueLines` | `string[]` | The values the rule would compare, one line each, in the Metaverse attribute's type. |
| `ValueType` | `string` | The type the values are compared as: the target Metaverse attribute's. One of `NotSet`, `Text`, `Number`, `DateTime`, `Binary`, `Reference`, `Guid`, `Boolean`, `LongNumber` or `Decimal`. |
| `FailureMessage` | `string` | Why the rule failed, when `Outcome` is `Failed`. |
| `Inputs` | `object[]` | The attributes the rule reads, with the values tested for each. Each has `Attribute`, `Type`, `Plurality` and `ValueLines`. |
| `Lookup` | `object` | What the values find in the Metaverse. Empty when the lookup was not asked for, or the rule produced no values to look up. It has `Outcome`, `Description`, `MetaverseObjectId`, `MetaverseObjectName`, `Candidates` and `CandidateTotal`. |
| `InputErrors` | `string[]` | Why the values could not be used, one message per problem; empty when they could. |
<!-- END GENERATED: Test-JIMMatchingRule -->

### Examples

```powershell title="See what a rule would match on"
Test-JIMMatchingRule -ConnectedSystemId 3 -Id 40 -Attributes @{ employeeNumber = 'EMP-48213' }
```

```powershell title="Find the Metaverse Object a real object would join"
Test-JIMMatchingRule -ConnectedSystemId 3 -Id 40 -ConnectedSystemObjectId '0b0e1c5a-6d3a-4c7e-9a43-6a7b5d2f1e01' -LookUp
```

```powershell title="Check two email addresses identify one Metaverse Object"
(Test-JIMMatchingRule -SyncRuleId 12 -Id 7 -Attributes @{ mail = 'jo@corp.example', 'j.bloggs@corp.example' } -LookUp).Lookup
```

---

## :material-book-open-variant-outline: See also

- [Object Matching Rules](../configuration/object-matching-rules.md): how matching works, its outcomes, matching on an expression, and the two modes
- [Previews](previews.md#new-jimconfigurationchangepreview): preview a change to the rules, or a mode switch, before making it
- [Synchronisation Rules](synchronisation-rules.md): the rules advanced mode's matching rules sit on
- [Scoping Criteria](scoping-criteria.md): the other half of what decides which objects a Synchronisation Rule processes
