---
title: Synchronisation Rules
description: "The cmdlets that list, create, change and delete Synchronisation Rules, and what deleting one does to the values it contributed."
type: reference
audience: automation-author
---

# Synchronisation Rules

Synchronisation Rules decide how values flow between a Connected System and the Metaverse. The cmdlets on this page list, create, change and delete them. A rule's [Attribute Flows](attribute-mappings.md), [scoping criteria](scoping-criteria.md), [Object Matching Rules](object-matching-rules.md) and [initial password](initial-password.md) have pages of their own. What each setting does is covered in [Synchronisation Rules](../configuration/synchronisation-rules.md).

---
## Get-JIMSyncRule

Gets Synchronisation Rules: every rule by default, one by ID, or those matching the filters. The filters are the ones on the portal's Synchronisation Rules page: they combine with AND, and the values given to one filter combine with OR.

<!-- BEGIN GENERATED: Get-JIMSyncRule -->
### Syntax

```powershell
# List (default)
Get-JIMSyncRule [-ConnectedSystemId <int>] [-InputObject <PSCustomObject>] [-Name <string>] [-Direction <string[]>] [-ActionType <string[]>] [-Status <string[]>]

# ById
Get-JIMSyncRule -Id <int>

# ByConnectedSystemId
Get-JIMSyncRule [-ConnectedSystemId <int>] [-Name <string>] [-Direction <string[]>] [-ActionType <string[]>] [-Status <string[]>]

# ByConnectedSystemName
Get-JIMSyncRule [-ConnectedSystemName <string>] [-Name <string>] [-Direction <string[]>] [-ActionType <string[]>] [-Status <string[]>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the Synchronisation Rule to get. |
| `ConnectedSystemId` | `int` | No (List, ByConnectedSystemId) |  | Returns only the rules for the Connected System with this ID. Accepts pipeline input by property name. |
| `ConnectedSystemName` | `string` | No (ByConnectedSystemName) |  | Returns only the rules for the Connected System with this name, matched exactly. |
| `InputObject` | `PSCustomObject` | No (List) |  | A Connected System from `Get-JIMConnectedSystem`, through the pipeline: returns only its rules, as `-ConnectedSystemId` would. Accepts pipeline input. |
| `Name` | `string` | No (List, ByConnectedSystemId, ByConnectedSystemName) |  | Returns only the rules whose name matches. Supports wildcards. |
| `Direction` | `string[]` | No (List, ByConnectedSystemId, ByConnectedSystemName) |  | Returns only the rules of these directions: Import for rules that flow values into the Metaverse, Export for rules that flow them out. One of `Import` or `Export`. |
| `ActionType` | `string[]` | No (List, ByConnectedSystemId, ByConnectedSystemName) |  | Returns only the rules that do one of these: Projects for import rules that create Metaverse Objects, Provisions for export rules that create Connected System Objects, and FlowOnly for rules that create nothing and only flow values. One of `Projects`, `Provisions` or `FlowOnly`. |
| `Status` | `string[]` | No (List, ByConnectedSystemId, ByConnectedSystemName) |  | Returns only the rules that are Enabled or Disabled. One of `Enabled` or `Disabled`. |

### Output

The matching rules, or with `-Id`, the one rule.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The ID of the Synchronisation Rule. |
| `Name` | `string` | The name of the Synchronisation Rule. |
| `Description` | `string` | What the Synchronisation Rule is for, for administrators; empty when none is set. |
| `Created` | `datetime` | When the Synchronisation Rule was created (UTC). |
| `ConnectedSystemId` | `int` | The ID of the Connected System the rule synchronises with. |
| `ConnectedSystemName` | `string` | The name of that Connected System. |
| `ConnectedSystemObjectTypeId` | `int` | The ID of the Connected System Object Type the rule applies to. |
| `ConnectedSystemObjectTypeName` | `string` | The name of that Connected System Object Type. |
| `MetaverseObjectTypeId` | `int` | The ID of the Metaverse Object Type the rule applies to. |
| `MetaverseObjectTypeName` | `string` | The name of that Metaverse Object Type. |
| `Direction` | `string` | Import for a rule that flows values from the Connected System into the Metaverse, Export for one that flows them out to it. One of `NotSet`, `Import` or `Export`. |
| `ProvisionToConnectedSystem` | `bool` | Whether an export rule creates a Connected System Object for each Metaverse Object in its scope that has none; empty when it has not been set. |
| `ProjectToMetaverse` | `bool` | Whether an import rule creates a Metaverse Object for each Connected System Object in its scope that matches none; empty when it has not been set. |
| `Enabled` | `bool` | Whether the rule takes part in synchronisation; a disabled rule is skipped. |
| `EnforceState` | `bool` | For an export rule, whether a change imported from the Connected System re-evaluates the rule, so the value JIM exported is put back when the target drifts from it. Only meaningful on an export rule. |
| `OutboundDeprovisionAction` | `string` | For an export rule, what happens to a Connected System Object when its Metaverse Object leaves the rule's scope or is deleted: Disconnect breaks the join and leaves the object in the Connected System, and Delete removes it from there. Only meaningful on an export rule. One of `Disconnect` or `Delete`. |
<!-- END GENERATED: Get-JIMSyncRule -->

### Examples

```powershell title="List all Synchronisation Rules"
Get-JIMSyncRule
```

```powershell title="Get a specific Synchronisation Rule by ID"
Get-JIMSyncRule -Id 5
```

```powershell title="Filter by name"
Get-JIMSyncRule -Name "Inbound*"
```

```powershell title="Get Synchronisation Rules for a Connected System"
Get-JIMSyncRule -ConnectedSystemName "Active Directory"
```

```powershell title="Find enabled outbound rules"
Get-JIMSyncRule -Direction Export -Status Enabled
```

```powershell title="Find the rules that create objects"
Get-JIMSyncRule -ActionType Projects, Provisions
```

```powershell title="Combine filters to audit one system"
Get-JIMSyncRule -ConnectedSystemName "Active Directory" -Direction Export -Status Disabled
```

```powershell title="Pipeline from Connected System ID"
$cs = Get-JIMConnectedSystem -Name "HR System"
Get-JIMSyncRule -ConnectedSystemId $cs.Id
```

```powershell title="Pipe Connected Systems straight in, and filter them"
Get-JIMConnectedSystem -Name "HR*" | Get-JIMSyncRule -Direction Import -Status Enabled
```

---

## New-JIMSyncRule

Creates a Synchronisation Rule between a Connected System Object Type and a Metaverse Object Type. Add its [Attribute Flows](attribute-mappings.md#new-jimsyncrulemapping), [scoping criteria](scoping-criteria.md) and [Object Matching Rules](object-matching-rules.md) afterwards.

<!-- BEGIN GENERATED: New-JIMSyncRule -->
### Syntax

```powershell
# ById (default)
New-JIMSyncRule [-Name] <string> -ConnectedSystemId <int> -ConnectedSystemObjectTypeId <int> -MetaverseObjectTypeId <int> -Direction <string> [-Description <string>] [-ProjectToMetaverse] [-ProvisionToConnectedSystem] [-Enabled <bool>] [-OutboundDeprovisionAction <string>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByName
New-JIMSyncRule [-Name] <string> -ConnectedSystemName <string> -ConnectedSystemObjectTypeId <int> -MetaverseObjectTypeId <int> -Direction <string> [-Description <string>] [-ProjectToMetaverse] [-ProvisionToConnectedSystem] [-Enabled <bool>] [-OutboundDeprovisionAction <string>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes |  | The name of the Synchronisation Rule. Can be given by position, as the first argument. |
| `Description` | `string` | No |  | What the Synchronisation Rule is for, for administrators. |
| `ConnectedSystemId` | `int` | Yes (ById) |  | The ID of the Connected System the rule synchronises with. |
| `ConnectedSystemName` | `string` | Yes (ByName) |  | The name of the Connected System the rule synchronises with, matched exactly. |
| `ConnectedSystemObjectTypeId` | `int` | Yes |  | The ID of the Connected System Object Type the rule applies to. |
| `MetaverseObjectTypeId` | `int` | Yes |  | The ID of the Metaverse Object Type the rule applies to. |
| `Direction` | `string` | Yes |  | Import to flow values from the Connected System into the Metaverse, Export to flow them out to it. One of `Import` or `Export`. |
| `ProjectToMetaverse` | `switch` | No |  | On an import rule, creates a Metaverse Object for each Connected System Object in scope that matches none. |
| `ProvisionToConnectedSystem` | `switch` | No |  | On an export rule, creates a Connected System Object for each Metaverse Object in scope that has none. |
| `Enabled` | `bool` | No | `$true` | Whether the rule takes part in synchronisation. |
| `OutboundDeprovisionAction` | `string` | No |  | On an export rule, what happens to a Connected System Object when its Metaverse Object leaves the rule's scope or is deleted: Disconnect breaks the join and leaves the object in the Connected System, and Delete removes it from there. Disconnect when omitted. One of `Disconnect` or `Delete`. |
| `ChangeReason` | `string` | No |  | An optional reason for the change, recorded against this Synchronisation Rule's change history. |
| `PassThru` | `switch` | No |  | Returns the rule created. |

### Output

With `-PassThru`, the rule created; otherwise nothing. A deletion source warning is written to the warning stream either way.

| Property | Type | Description |
|----------|------|-------------|
| `Warnings` | `string[]` | Non-blocking warnings the save raised about the rule's Attribute Flows, for example that an Attribute Flow deriving a Metaverse attribute calls a function returning a different value each time it is evaluated. The save went ahead regardless. Always present; empty when there are none. |
| `DependentDerivedFlows` | `object[]` | The Metaverse-Derived Attribute Flows the save left with a missing input (for example by disabling the rule holding the last enabled contributor of an attribute they read). The save went ahead regardless; each flow's Missing Input Behaviour now decides what it contributes. Always present. Each has `MappingId`, `TargetMetaverseAttributeName`, `SyncRuleId`, `SyncRuleName`, `ConnectedSystemId`, `ConnectedSystemName` and `MissingInputs`. |
| `DeletionSourceWarning` | `string` | Set when the save took the rule into projecting into a Metaverse Object Type deleted When Authoritative Source Disconnected, from a Connected System that is not one of its authoritative sources: objects only this system holds will never be deleted, and those it shares with an authoritative source are deleted when that source disconnects. Empty when there is nothing to warn about, including for a rule that already projected. |
| `Id` | `int` | The ID of the Synchronisation Rule. |
| `Name` | `string` | The name of the Synchronisation Rule. |
| `Description` | `string` | What the Synchronisation Rule is for, for administrators; empty when none is set. |
| `Created` | `datetime` | When the Synchronisation Rule was created (UTC). |
| `ConnectedSystemId` | `int` | The ID of the Connected System the rule synchronises with. |
| `ConnectedSystemName` | `string` | The name of that Connected System. |
| `ConnectedSystemObjectTypeId` | `int` | The ID of the Connected System Object Type the rule applies to. |
| `ConnectedSystemObjectTypeName` | `string` | The name of that Connected System Object Type. |
| `MetaverseObjectTypeId` | `int` | The ID of the Metaverse Object Type the rule applies to. |
| `MetaverseObjectTypeName` | `string` | The name of that Metaverse Object Type. |
| `Direction` | `string` | Import for a rule that flows values from the Connected System into the Metaverse, Export for one that flows them out to it. One of `NotSet`, `Import` or `Export`. |
| `ProvisionToConnectedSystem` | `bool` | Whether an export rule creates a Connected System Object for each Metaverse Object in its scope that has none; empty when it has not been set. |
| `ProjectToMetaverse` | `bool` | Whether an import rule creates a Metaverse Object for each Connected System Object in its scope that matches none; empty when it has not been set. |
| `Enabled` | `bool` | Whether the rule takes part in synchronisation; a disabled rule is skipped. |
| `EnforceState` | `bool` | For an export rule, whether a change imported from the Connected System re-evaluates the rule, so the value JIM exported is put back when the target drifts from it. Only meaningful on an export rule. |
| `OutboundDeprovisionAction` | `string` | For an export rule, what happens to a Connected System Object when its Metaverse Object leaves the rule's scope or is deleted: Disconnect breaks the join and leaves the object in the Connected System, and Delete removes it from there. Only meaningful on an export rule. One of `Disconnect` or `Delete`. |
<!-- END GENERATED: New-JIMSyncRule -->

### Examples

```powershell title="Create an import Synchronisation Rule with projection"
New-JIMSyncRule -Name "AD User Import" `
    -Description "Imports user accounts from Active Directory and projects new joiners into the Metaverse" `
    -ConnectedSystemId 1 `
    -ConnectedSystemObjectTypeId 3 `
    -MetaverseObjectTypeId 1 `
    -Direction Import `
    -ProjectToMetaverse `
    -PassThru
```

```powershell title="Create an export Synchronisation Rule by Connected System name"
New-JIMSyncRule -Name "AD User Export" `
    -ConnectedSystemName "Active Directory" `
    -ConnectedSystemObjectTypeId 3 `
    -MetaverseObjectTypeId 1 `
    -Direction Export `
    -ProvisionToConnectedSystem
```

```powershell title="Create an export Synchronisation Rule that deletes leavers from the target system"
New-JIMSyncRule -Name "AD User Export" `
    -ConnectedSystemId 2 `
    -ConnectedSystemObjectTypeId 3 `
    -MetaverseObjectTypeId 1 `
    -Direction Export `
    -ProvisionToConnectedSystem `
    -OutboundDeprovisionAction Delete
```

```powershell title="Create a disabled Synchronisation Rule"
New-JIMSyncRule -Name "HR Import (Draft)" `
    -ConnectedSystemId 2 `
    -ConnectedSystemObjectTypeId 5 `
    -MetaverseObjectTypeId 1 `
    -Direction Import `
    -Enabled $false
```

---

## Set-JIMSyncRule

Changes a Synchronisation Rule: its name and description, whether it is enabled, and how it projects, provisions and deprovisions. What you leave out stays as it is. Changes that leave a [Metaverse-Derived Attribute Flow](attribute-mappings.md#derived-attribute-flow-warnings) with a missing input, or that leave objects a Metaverse Object Type's deletion rule will never delete, go ahead with a warning rather than being refused.

<!-- BEGIN GENERATED: Set-JIMSyncRule -->
### Syntax

```powershell
# ById (default)
Set-JIMSyncRule -Id <int> [-Name <string>] [-Description <string>] [-ProjectToMetaverse <bool>] [-ProvisionToConnectedSystem <bool>] [-InboundOutOfScopeAction <string>] [-OutboundDeprovisionAction <string>] [-EnforceState <bool>] [-ChangeReason <string>] [-PreviewActivityId <guid>] [-PassThru] [-WhatIf] [-Confirm]

# Disable
Set-JIMSyncRule -Id <int> -Disable [-Name <string>] [-Description <string>] [-ProjectToMetaverse <bool>] [-ProvisionToConnectedSystem <bool>] [-InboundOutOfScopeAction <string>] [-OutboundDeprovisionAction <string>] [-EnforceState <bool>] [-ChangeReason <string>] [-PreviewActivityId <guid>] [-PassThru] [-WhatIf] [-Confirm]

# Enable
Set-JIMSyncRule -Id <int> -Enable [-Name <string>] [-Description <string>] [-ProjectToMetaverse <bool>] [-ProvisionToConnectedSystem <bool>] [-InboundOutOfScopeAction <string>] [-OutboundDeprovisionAction <string>] [-EnforceState <bool>] [-ChangeReason <string>] [-PreviewActivityId <guid>] [-PassThru] [-WhatIf] [-Confirm]

# ByInputObject
Set-JIMSyncRule -InputObject <PSCustomObject> [-Name <string>] [-Description <string>] [-ProjectToMetaverse <bool>] [-ProvisionToConnectedSystem <bool>] [-InboundOutOfScopeAction <string>] [-OutboundDeprovisionAction <string>] [-EnforceState <bool>] [-ChangeReason <string>] [-PreviewActivityId <guid>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById, Disable, Enable) |  | The ID of the Synchronisation Rule to change. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A Synchronisation Rule from `Get-JIMSyncRule`, through the pipeline. Accepts pipeline input. |
| `Name` | `string` | No |  | The new name of the Synchronisation Rule. |
| `Description` | `string` | No |  | What the Synchronisation Rule is for, for administrators. `$null` or an empty string clears it. |
| `Enable` | `switch` | Yes (Enable) |  | Enables the rule, so it takes part in synchronisation. |
| `Disable` | `switch` | Yes (Disable) |  | Disables the rule, so synchronisation skips it. |
| `ProjectToMetaverse` | `bool` | No |  | On an import rule, whether it creates a Metaverse Object for each Connected System Object in scope that matches none. |
| `ProvisionToConnectedSystem` | `bool` | No |  | On an export rule, whether it creates a Connected System Object for each Metaverse Object in scope that has none. |
| `InboundOutOfScopeAction` | `string` | No |  | On an import rule, what happens when a Connected System Object leaves the rule's scope. Disconnect breaks the join; whether the values it contributed are recalled as well depends on the Connected System Object Type's settings and the Metaverse Object Type's deletion settings. RemainJoined keeps the join and stops the values flowing. One of `Disconnect` or `RemainJoined`. |
| `OutboundDeprovisionAction` | `string` | No |  | On an export rule, what happens to a Connected System Object when its Metaverse Object leaves the rule's scope or is deleted: Disconnect breaks the join and leaves the object in the Connected System, and Delete removes it from there. One of `Disconnect` or `Delete`. |
| `EnforceState` | `bool` | No |  | On an export rule, whether a change imported from the Connected System re-evaluates the rule, so the value JIM exported is put back when the target drifts from it. |
| `ChangeReason` | `string` | No |  | An optional reason for the change, recorded against this Synchronisation Rule's change history. |
| `PreviewActivityId` | `guid` | No |  | The Configuration Change Preview this change was made after reading, as returned by `New-JIMConfigurationChangePreview -SyncRuleId`. Recorded on the change's Activity so "previewed, then applied" is auditable rather than a claim. |
| `PassThru` | `switch` | No |  | Returns the rule as changed. |

### Output

With `-PassThru`, the rule as changed; otherwise nothing. Its warnings are written to the warning stream either way.

Properties as under [New-JIMSyncRule](#new-jimsyncrule).
<!-- END GENERATED: Set-JIMSyncRule -->

### Examples

```powershell title="Rename a Synchronisation Rule"
Set-JIMSyncRule -Id 5 -Name "AD User Import (Production)"
```

```powershell title="Set or update a Synchronisation Rule's description"
Set-JIMSyncRule -Id 5 -Description "Imports production user accounts from Active Directory"
```

```powershell title="Clear a Synchronisation Rule's description"
Set-JIMSyncRule -Id 5 -Description $null
```

```powershell title="Enable a Synchronisation Rule"
Set-JIMSyncRule -Id 5 -Enable
```

```powershell title="Disable a Synchronisation Rule"
Set-JIMSyncRule -Id 5 -Disable
```

```powershell title="Pipeline: disable all Synchronisation Rules for a Connected System"
Get-JIMSyncRule -ConnectedSystemName "HR System" | Set-JIMSyncRule -Disable
```

```powershell title="Enable projection on an existing import rule"
Set-JIMSyncRule -Id 5 -ProjectToMetaverse $true -PassThru
```

```powershell title="Preview, then set, the Deprovisioning Action on an export rule"
$preview = New-JIMConfigurationChangePreview -SyncRuleId 5 -OutboundDeprovisionAction Delete -Wait
Set-JIMSyncRule -Id 5 -OutboundDeprovisionAction Delete -EnforceState $true -PreviewActivityId $preview.ActivityId
```

```powershell title="Disable a rule and record why (shown in the change history)"
Set-JIMSyncRule -Id 12 -Disable -ChangeReason "Pausing during HR cutover (CHG0098)"
```

---

## Remove-JIMSyncRule

Deletes a Synchronisation Rule, with its Attribute Flows, scoping criteria and Object Matching Rules.

When the rule still contributes Metaverse attribute values, the deletion recalls them by default: the rule is disabled at once, a queued task withdraws its values and re-elects the other contributors, and the rule is deleted as the task's last step. The command then returns the Activity doing it. `-KeepContributedValues` deletes the rule at once and leaves the values in place instead. A rule that contributes nothing is deleted at once either way. The confirmation states how many values the rule contributes, so you can choose before anything changes.

<!-- BEGIN GENERATED: Remove-JIMSyncRule -->
### Syntax

```powershell
# ById (default)
Remove-JIMSyncRule -Id <int> [-ChangeReason <string>] [-KeepContributedValues] [-Force] [-Wait] [-Timeout <int>] [-PassThru] [-WhatIf] [-Confirm]

# ByInputObject
Remove-JIMSyncRule -InputObject <PSCustomObject> [-ChangeReason <string>] [-KeepContributedValues] [-Force] [-Wait] [-Timeout <int>] [-PassThru] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the Synchronisation Rule to delete. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A Synchronisation Rule from `Get-JIMSyncRule`, through the pipeline. Accepts pipeline input. |
| `ChangeReason` | `string` | No |  | An optional reason for the deletion, recorded against the change history. |
| `KeepContributedValues` | `switch` | No |  | Keeps the Metaverse attribute values the rule contributed, instead of recalling them, and deletes the rule at once. The kept values have no provenance, so nothing can ever recall them, and the other contributors to those attributes are not re-elected. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt, and the count of contributed values it states. |
| `Wait` | `switch` | No |  | Waits for a queued recall to finish, so the rule has gone when the command returns. Without it, the command returns once the recall is queued, and the rule can still be read, disabled, until the recall finishes. Has no effect when the rule is deleted at once. |
| `Timeout` | `int` | No |  | How many seconds `-Wait` waits before reporting an error; the recall carries on regardless. Without it, `-Wait` waits until the recall finishes. At least 1. |
| `PassThru` | `switch` | No |  | Returns the rule as it stood before the deletion. |

### Output

When the deletion recalls the values the rule contributed, what is being recalled and the Activity doing it. Nothing when the rule is deleted at once. With `-PassThru`, the rule as it stood before the deletion as well.

**When the deletion recalls the values the rule contributed**:

| Property | Type | Description |
|----------|------|-------------|
| `RecallActivityId` | `guid` | The ID of the Activity recalling the values. The Synchronisation Rule is deleted as its last step. |
| `AffectedValueCount` | `int` | How many Metaverse attribute values the Synchronisation Rule contributed when the deletion was asked for. |
| `AffectedObjectCount` | `int` | How many Metaverse Objects held at least one of those values. |
| `DependentDerivedFlows` | `object[]` | The Metaverse-Derived Attribute Flows on other Synchronisation Rules that the deletion leaves with a missing input, because an attribute they read lost its last enabled contributor. The rule is disabled at once, so they are affected from now. The deletion goes ahead regardless. Always present. Each has `MappingId`, `TargetMetaverseAttributeName`, `SyncRuleId`, `SyncRuleName`, `ConnectedSystemId`, `ConnectedSystemName` and `MissingInputs`. |

**With `-PassThru`**: properties as under [Get-JIMSyncRule](#get-jimsyncrule).
<!-- END GENERATED: Remove-JIMSyncRule -->

### Examples

```powershell title="Delete a Synchronisation Rule with confirmation"
Remove-JIMSyncRule -Id 5
```

```powershell title="Delete without confirmation"
Remove-JIMSyncRule -Id 5 -Force
```

```powershell title="Delete a contributing rule and monitor the recall"
$recall = Remove-JIMSyncRule -Id 5 -Force
Get-JIMActivity -Id $recall.RecallActivityId
```

```powershell title="Delete a contributing rule and wait for the recall to land"
Remove-JIMSyncRule -Id 5 -Force -Wait
```

```powershell title="Delete a contributing rule, then reorder the survivors straight away"
Remove-JIMSyncRule -Id 5 -Force
Set-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 3 -MappingId @(7, 9)
```

A rule whose recall is queued drops to the bottom of every Attribute Priority order it contributes to, so the
reorder need not wait for the recall and may leave the deleted rule's mapping out.

```powershell title="Delete a rule KEEPING the values it contributed"
Remove-JIMSyncRule -Id 5 -KeepContributedValues
```

Keeping severs the values' provenance: nothing records that the rule contributed them, so no future synchronisation can recall them. Only choose this when the values should outlive the rule.

```powershell title="Delete every disabled Synchronisation Rule for a Connected System"
Get-JIMSyncRule -ConnectedSystemName "Legacy HR" -Status Disabled |
    Remove-JIMSyncRule -Force
```

This deletes every rule the filter returns. Run it with `-WhatIf` in place of `-Force` first to see which.

Deleting a rule can leave a Metaverse-Derived Attribute Flow on another rule with a missing input; each one is written to the warning stream, as [Derived Attribute Flow warnings](attribute-mappings.md#derived-attribute-flow-warnings) describes.

---

## See also

- [Synchronisation Rules](../configuration/synchronisation-rules.md): direction, scoping, object matching, projection, and attribute mappings
- [Attribute Mappings](attribute-mappings.md): the Attribute Flows on a Synchronisation Rule
- [Scoping Criteria](scoping-criteria.md): decide which objects a Synchronisation Rule processes
- [Object Matching Rules](object-matching-rules.md): match the objects a Synchronisation Rule processes to Metaverse Objects
- [Initial Password](initial-password.md): the password a Synchronisation Rule sets on the Connected System Objects it provisions
- [Generated Values](generated-values.md): the values a generated Attribute Flow issues, and the decisions they can wait on
- [Connected Systems](connected-systems.md): PowerShell cmdlets for managing Connected Systems
- [Metaverse](metaverse.md): find and read the Metaverse Objects the rules synchronise
