---
title: Roles
---

# Roles

**Roles** determine what authenticated principals (users and [API keys](api-keys.md)) can do within JIM. The current model is coarse-grained: most administrative operations require the **Administrator** role. Future releases will introduce finer-grained roles; the underlying model is forward-compatible.

## Built-in roles

Roles marked **built-in** ship with JIM and cannot be deleted. They can have members added or removed like any other role.

## Static membership

A role's static members are the Metaverse Objects (typically `person` objects) explicitly assigned to the role. Membership is established by adding a Metaverse Object as a member, and removed by deleting that membership.

API keys carry roles directly rather than via metaverse-object membership. This means rotating a person's role membership doesn't affect already-issued API keys, and revoking an API key doesn't change a person's role membership. Manage the two surfaces independently.

## Administrator-lockout safety

Two safety checks protect specifically the Administrator role; both reject the request rather than risk locking everyone out:

- You cannot remove **yourself** from the Administrator role
- You cannot remove the **last remaining member** of the Administrator role

If you genuinely need to transfer admin to a different identity, add the new admin first, then remove the old one.

## Common workflows

**Granting administrator access to a new user:**

1. Confirm the user has a corresponding Metaverse Object (created via synchronisation from a source system, or manually)
2. Add the Metaverse Object as a member of the Administrator role

**Transferring administrator access:**

1. Add the new admin to the Administrator role
2. Verify the new admin can sign in and perform an admin operation
3. Remove the old admin from the Administrator role

## Manage Roles

- **JIM portal**<br /> Roles area of the admin UI
- **PowerShell**<br /> [Security cmdlets](../powershell/security.md) (`Get-JIMRole`, `Add-JIMRoleMember`, `Remove-JIMRoleMember`, etc.)
- **REST API**<br /> Security endpoints in the [interactive API reference](../../api/reference/)

## See also

- [API Keys](api-keys.md) -- API keys carry roles directly; the same model applies
- [API Authentication](../api/authentication.md) -- how authenticated principals are identified
