---
title: Roles
description: JIM's built-in Roles, how users and API Keys hold them, and how JIM stops you locking every administrator out.
type: object
audience: administrator
---

# Roles

**Roles** determine what authenticated principals (users and [API keys](api-keys.md)) can do within JIM. The current model is coarse-grained: most administrative operations require the **Administrator** role.

```jim-diagram
archetype: decision
title: When JIM refuses to remove a Role member
desc: 'Removing a member from any Role but Administrator goes ahead. For the Administrator role, JIM rejects the request if you are removing yourself, or if the member is the last remaining one; otherwise the member is removed. To transfer administrator access, add the new administrator first, then remove the old one.'
start: {label: Remove a Role member}
questions:
- {id: admin, label: 'The Administrator role?'}
- {id: self, label: 'Removing yourself?'}
- {id: last, label: 'The last remaining member?'}
outcomes:
- {id: removed, label: Removed, sub: the membership is deleted, good: true}
- {id: notself, label: Rejected, sub: no one can remove themselves}
- {id: notlast, label: Rejected, sub: 'add the new admin first, then remove the old'}
- {id: removedadmin, label: Removed, sub: other administrators remain, good: true}
answers:
- {from: admin, to: removed, label: 'no'}
- {from: admin, to: self, label: 'yes'}
- {from: self, to: notself, label: 'yes'}
- {from: self, to: last, label: 'no'}
- {from: last, to: notlast, label: 'yes'}
- {from: last, to: removedadmin, label: 'no'}
```

## :material-account-lock-outline: Built-in roles

Roles marked **built-in** ship with JIM and cannot be deleted. They can have members added or removed like any other role.

## :material-account-group-outline: Static membership

A role's static members are the Metaverse Objects (typically `User` objects) explicitly assigned to the role. Membership is established by adding a Metaverse Object as a member, and removed by deleting that membership.

API keys carry roles directly rather than through a Metaverse Object's membership. This means rotating a person's role membership doesn't affect already-issued API keys, and revoking an API key doesn't change a person's role membership. Manage the two surfaces independently.

## :material-shield-lock-outline: Administrator-lockout safety

Two safety checks protect specifically the Administrator role; both reject the request rather than risk locking everyone out:

- You cannot remove **yourself** from the Administrator role
- You cannot remove the **last remaining member** of the Administrator role

If you genuinely need to transfer admin to a different identity, add the new admin first, then remove the old one.

## :material-history: Change history

Every change to a Role is recorded in [Configuration Change History](activities.md#configuration-change-history): a versioned snapshot captures the Role's definition and its static membership together, so each add or remove records who made the change, when, and an optional reason, alongside the resulting member list. The built-in Administrator Role's creation is recorded as System-attributed version 1 from first startup, so its history has a starting point even though nothing created it interactively.

There is no Role management UI, so retrieve a Role's history with `Get-JIMConfigurationChangeHistory -Type Role`, the REST API, or the Activity list in the admin portal. Renaming a member does not appear in the Role's own history; that change belongs to the member's history instead, since only membership (who is present) is part of a Role's configuration.

## :material-playlist-check: Common workflows

**Granting administrator access to a new user:**

1. Confirm the user has a corresponding Metaverse Object (created via synchronisation from a source system, or manually)
2. Add the Metaverse Object as a member of the Administrator role

**Transferring administrator access:**

1. Add the new admin to the Administrator role
2. Verify the new admin can sign in and perform an admin operation
3. Remove the old admin from the Administrator role

## :material-cog-outline: Manage Roles

- **JIM portal**<br /> No Roles area; use PowerShell or the REST API
- **PowerShell**<br /> [Security cmdlets](../powershell/security.md) (`Get-JIMRole`, `Add-JIMRoleMember`, `Remove-JIMRoleMember`, etc.)
- **REST API**<br /> Security endpoints in the [interactive API reference](../../api/reference/)

## :material-book-open-variant-outline: Related

- [API Keys](api-keys.md): API keys carry roles directly; the same model applies
- [API Authentication](../api/authentication.md): how authenticated principals are identified
