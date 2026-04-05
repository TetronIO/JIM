---
title: Security
---

# Security

Cmdlets for querying security role definitions in JIM. Roles define permissions that can be assigned to users or API keys.

## Get-JIMRole

Retrieves security role definitions from JIM.

### Syntax

```powershell
Get-JIMRole [-Name <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | No | | Filter roles by name. Supports wildcards (e.g., `"Admin*"`). |

### Output

Role objects with `id`, `name`, and `description` properties.

### Examples

```powershell title="List all roles"
Get-JIMRole
```

```powershell title="Get the Administrator role"
Get-JIMRole -Name "Administrator"
```

```powershell title="Get role names and descriptions"
Get-JIMRole | Select-Object name, description
```

```powershell title="Find the Administrator role ID for use with New-JIMApiKey"
$adminRole = Get-JIMRole -Name "Administrator"
New-JIMApiKey -Name "Admin Key" -RoleIds @($adminRole.id) -PassThru
```

---

!!! info "Role Management"
    Roles are currently read-only and defined by the system. Role assignment to metaverse objects is tracked in [GitHub issue #467](https://github.com/TetronIO/JIM/issues/467).

## See also

- [API Security](../api/security/index.md)
- [API Keys](api-keys.md)
