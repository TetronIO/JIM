---
title: Security
---

# Security

Cmdlets for managing security roles and role membership in JIM. Roles define permissions that can be assigned to users or API keys.

## Get-JIMRole

Retrieves security role definitions from JIM.

### Syntax

```powershell
Get-JIMRole [-Id <int>]
Get-JIMRole [-Name <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | No | | Get a specific role by its unique identifier. |
| `Name` | `string` | No | | Filter roles by name. Supports wildcards (e.g., `"Admin*"`). |

### Output

Role objects with `id`, `name`, `builtIn`, `created`, and `staticMemberCount` properties.

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
New-JIMApiKey -Name "Admin Key" -RoleIds @($adminRole.id) -PassThru
```

---

## Get-JIMRoleMember

Retrieves Metaverse Objects assigned to a security role.

### Syntax

```powershell
Get-JIMRoleMember -RoleId <int>
Get-JIMRoleMember -InputObject <PSCustomObject>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `RoleId` | `int` | Yes* | | The unique identifier of the role. |
| `InputObject` | `PSCustomObject` | Yes* | | Role object from the pipeline (e.g., from `Get-JIMRole`). |

*One of `RoleId` or `InputObject` is required.

### Output

Metaverse object members with `id`, `displayName`, `typeId`, and `typeName` properties.

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
        Role    = $role.name
        Members = ($members | ForEach-Object { $_.displayName }) -join ", "
    }
}
```

---

## Get-JIMMetaverseObjectRole

Lists the security roles a Metaverse Object is a member of.

### Syntax

```powershell
Get-JIMMetaverseObjectRole -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | The unique identifier of the Metaverse Object. Accepts pipeline input by property name (e.g. from `Get-JIMMetaverseObject`). |

### Output

Role objects with `id`, `name`, `builtIn`, `created`, and `staticMemberCount` properties. Returns nothing if the object is not a member of any role.

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
            Member = $member.displayName
            Roles  = ($roles | ForEach-Object { $_.name }) -join ", "
        }
    }
```

---

## Add-JIMRoleMember

Adds a Metaverse Object to a security role.

### Syntax

```powershell
Add-JIMRoleMember -RoleId <int> -MetaverseObjectId <guid>
Add-JIMRoleMember -RoleId <int> -InputObject <PSCustomObject>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `RoleId` | `int` | Yes | | The unique identifier of the role to add the member to. |
| `MetaverseObjectId` | `guid` | Yes* | | The unique identifier of the Metaverse Object. |
| `InputObject` | `PSCustomObject` | Yes* | | Metaverse object from the pipeline (e.g., from `Get-JIMMetaverseObject`). |

*One of `MetaverseObjectId` or `InputObject` is required.

### Examples

```powershell title="Add a member to the Administrator role"
Add-JIMRoleMember -RoleId 1 -MetaverseObjectId "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Add a member via pipeline"
Get-JIMMetaverseObject -Id "a1b2c3d4-..." | Add-JIMRoleMember -RoleId 1
```

```powershell title="Look up role by name and add a member"
$adminRole = Get-JIMRole -Name "Administrator"
Add-JIMRoleMember -RoleId $adminRole.id -MetaverseObjectId "a1b2c3d4-..."
```

---

## Remove-JIMRoleMember

Removes a Metaverse Object from a security role.

### Syntax

```powershell
Remove-JIMRoleMember -RoleId <int> -MetaverseObjectId <guid> [-Force]
Remove-JIMRoleMember -RoleId <int> -InputObject <PSCustomObject> [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `RoleId` | `int` | Yes | | The unique identifier of the role to remove the member from. |
| `MetaverseObjectId` | `guid` | Yes* | | The unique identifier of the Metaverse Object. |
| `InputObject` | `PSCustomObject` | Yes* | | Metaverse object from the pipeline. |
| `Force` | `switch` | No | `$false` | Suppresses confirmation prompts. |

*One of `MetaverseObjectId` or `InputObject` is required.

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
    Where-Object { $_.displayName -eq "Bob" } |
    Remove-JIMRoleMember -RoleId 2 -Force
```

---

## See also

- [Roles](../configuration/roles.md): role definitions, static membership, and administrator-lockout safety
- [API Keys](api-keys.md)
