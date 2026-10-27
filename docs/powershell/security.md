---
title: Security
description: "The cmdlets that list roles, their members and the roles a Metaverse Object holds, and add or remove role members."
type: reference
audience: automation-author
---

# Security

Cmdlets for managing security roles and role membership in JIM. Roles define permissions that can be assigned to users or API keys.

## :material-account-lock-outline: Get-JIMRole

Retrieves security role definitions from JIM.

<!-- BEGIN GENERATED: Get-JIMRole -->
### Syntax

```powershell
# List (default)
Get-JIMRole [-Name <string>]

# ById
Get-JIMRole -Id <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | Returns the Role with this ID. Accepts pipeline input by property name. |
| `Name` | `string` | No (List) |  | Only Roles with this name. Supports wildcards. |

### Output

One object per Role.

**By default**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The Role's identifier. |
| `Name` | `string` | The Role's name. |
| `BuiltIn` | `bool` | Whether JIM created the Role when it was set up. A built-in Role cannot be deleted. |
| `Created` | `datetime` | When the Role was created (UTC). |
| `StaticMemberCount` | `int` | How many Metaverse Objects have been made members of the Role. |

**With `-Id`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The Role's identifier. |
| `Name` | `string` | The Role's name. |
| `BuiltIn` | `bool` | Whether JIM created the Role when it was set up. A built-in Role cannot be deleted. |
| `Created` | `datetime` | When the Role was created (UTC). |
| `StaticMemberCount` | `int` | How many Metaverse Objects have been made members of the Role. |
<!-- END GENERATED: Get-JIMRole -->

### Examples

```powershell title="List all roles"
Get-JIMRole
```

```powershell title="Get a role by ID"
Get-JIMRole -Id 1
```

```powershell title="Get the Administrator role by name"
Get-JIMRole -Name "Administrator"
```

```powershell title="Find the Administrator role ID for use with New-JIMApiKey"
$adminRole = Get-JIMRole -Name "Administrator"
New-JIMApiKey -Name "Admin Key" -RoleIds @($adminRole.Id) -PassThru
```

---

## :material-account-multiple-outline: Get-JIMRoleMember

Retrieves Metaverse Objects assigned to a security role.

<!-- BEGIN GENERATED: Get-JIMRoleMember -->
### Syntax

```powershell
# ById (default)
Get-JIMRoleMember -RoleId <int>

# ByInputObject
Get-JIMRoleMember -InputObject <PSCustomObject>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `RoleId` | `int` | Yes (ById) |  | The ID of the Role. Accepts pipeline input by property name, `RoleId` or `Id`. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A Role from the pipeline, for example from `Get-JIMRole`. Accepts pipeline input. |

### Output

One object per member of the Role.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The unique identifier (GUID) of the Metaverse Object. |
| `DisplayName` | `string` | The display name of the Metaverse Object. |
| `Type` | `object` | The Metaverse Object Type, with its Id and Name. It has `Id` and `Name`. |
<!-- END GENERATED: Get-JIMRoleMember -->

### Examples

```powershell title="List members of a role by ID"
Get-JIMRoleMember -RoleId 1
```

```powershell title="List Administrator role members via pipeline"
Get-JIMRole -Name "Administrator" | Get-JIMRoleMember
```

```powershell title="List all roles with their members"
Get-JIMRole | ForEach-Object {
    $role = $_
    $members = $_ | Get-JIMRoleMember
    [PSCustomObject]@{
        Role    = $role.Name
        Members = ($members | ForEach-Object { $_.DisplayName }) -join ", "
    }
}
```

---

## :material-account-key-outline: Get-JIMMetaverseObjectRole

Lists the security roles a Metaverse Object is a member of.

<!-- BEGIN GENERATED: Get-JIMMetaverseObjectRole -->
### Syntax

```powershell
Get-JIMMetaverseObjectRole -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The ID of the Metaverse Object. Accepts pipeline input by property name. |

### Output

One object per Role the Metaverse Object is a member of; nothing when it is in none.

Properties as under [Get-JIMRole](#get-jimrole).
<!-- END GENERATED: Get-JIMMetaverseObjectRole -->

### Examples

```powershell title="List the roles a specific object is a member of"
Get-JIMMetaverseObjectRole -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Find an object by attribute and list its roles"
Get-JIMMetaverseObject -AttributeName 'Account Name' -AttributeValue 'jsmith' |
    Get-JIMMetaverseObjectRole
```

```powershell title="Audit the role memberships of every administrator"
Get-JIMRole -Name "Administrator" |
    Get-JIMRoleMember |
    ForEach-Object {
        $member = $_
        $roles = $_ | Get-JIMMetaverseObjectRole
        [PSCustomObject]@{
            Member = $member.DisplayName
            Roles  = ($roles | ForEach-Object { $_.Name }) -join ", "
        }
    }
```

---

## :material-account-plus-outline: Add-JIMRoleMember

Adds a Metaverse Object to a security role.

<!-- BEGIN GENERATED: Add-JIMRoleMember -->
### Syntax

```powershell
# ById (default)
Add-JIMRoleMember -RoleId <int> -MetaverseObjectId <guid> [-ChangeReason <string>] [-WhatIf] [-Confirm]

# ByInputObject
Add-JIMRoleMember -RoleId <int> -InputObject <PSCustomObject> [-ChangeReason <string>] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `RoleId` | `int` | Yes |  | The ID of the Role to add the member to. |
| `MetaverseObjectId` | `guid` | Yes (ById) |  | The ID of the Metaverse Object to add. Accepts pipeline input by property name, `MetaverseObjectId` or `Id`. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A Metaverse Object from the pipeline, for example from `Get-JIMMetaverseObject`. Accepts pipeline input. |
| `ChangeReason` | `string` | No |  | Why the change was made, recorded on the audit Activity and shown in the Role's Configuration Change History. |

### Output

None.
<!-- END GENERATED: Add-JIMRoleMember -->

### Examples

```powershell title="Add a member to the Administrator role"
Add-JIMRoleMember -RoleId 1 -MetaverseObjectId "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Add a member via pipeline"
Get-JIMMetaverseObject -Id "a1b2c3d4-..." | Add-JIMRoleMember -RoleId 1
```

```powershell title="Look up role by name and add a member"
$adminRole = Get-JIMRole -Name "Administrator"
Add-JIMRoleMember -RoleId $adminRole.Id -MetaverseObjectId "a1b2c3d4-..."
```

```powershell title="Add a member and record a reason"
Add-JIMRoleMember -RoleId 1 -MetaverseObjectId "a1b2c3d4-..." -ChangeReason "Onboarding new administrator (CHG0127)"
```

---

## :material-account-remove-outline: Remove-JIMRoleMember

Removes a Metaverse Object from a security role.

<!-- BEGIN GENERATED: Remove-JIMRoleMember -->
### Syntax

```powershell
# ById (default)
Remove-JIMRoleMember -RoleId <int> -MetaverseObjectId <guid> [-Force] [-ChangeReason <string>] [-WhatIf] [-Confirm]

# ByInputObject
Remove-JIMRoleMember -RoleId <int> -InputObject <PSCustomObject> [-Force] [-ChangeReason <string>] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `RoleId` | `int` | Yes |  | The ID of the Role to remove the member from. |
| `MetaverseObjectId` | `guid` | Yes (ById) |  | The ID of the Metaverse Object to remove. Accepts pipeline input by property name, `MetaverseObjectId` or `Id`. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A Metaverse Object from the pipeline, for example from `Get-JIMMetaverseObject`. Accepts pipeline input. |
| `Force` | `switch` | No |  | Skips the confirmation prompt. |
| `ChangeReason` | `string` | No |  | Why the change was made, recorded on the audit Activity and shown in the Role's Configuration Change History. |

### Output

None.
<!-- END GENERATED: Remove-JIMRoleMember -->

!!! warning "Safety Checks"
    The API enforces safety checks to prevent lockout:

    - You cannot remove **yourself** from the Administrator role
    - You cannot remove the **last member** of the Administrator role

### Examples

```powershell title="Remove a member (with confirmation)"
Remove-JIMRoleMember -RoleId 1 -MetaverseObjectId "a1b2c3d4-..."
```

```powershell title="Remove a member (skip confirmation)"
Remove-JIMRoleMember -RoleId 1 -MetaverseObjectId "a1b2c3d4-..." -Force
```

```powershell title="Remove a specific member by display name"
Get-JIMRoleMember -RoleId 2 |
    Where-Object { $_.DisplayName -eq "Bob" } |
    Remove-JIMRoleMember -RoleId 2 -Force
```

```powershell title="Remove a member and record a reason"
Remove-JIMRoleMember -RoleId 1 -MetaverseObjectId "a1b2c3d4-..." -Force -ChangeReason "Offboarding (CHG0128)"
```

---

## :material-book-open-variant-outline: See also

- [Roles](../configuration/roles.md): role definitions, static membership, administrator-lockout safety, and Configuration Change History
- [API Keys](api-keys.md)
- [History](history.md): `Get-JIMConfigurationChangeHistory -Type Role`
