---
title: Security
---

# Security

The Security API exposes role definitions and role-membership management. Roles determine what authenticated principals (users and API keys) can do within JIM.

> Endpoint reference for this resource is in the [interactive API reference](../index.md#where-to-find-what). This page covers the concepts.

## Key Concepts

**Roles.** A role is a named permission grant. The current model is coarse-grained: most administrative endpoints require the `Administrator` role. Future releases will introduce finer-grained roles; the API surface here is forward-compatible.

**Built-in roles.** Roles marked `builtIn` ship with JIM and cannot be deleted. They can have members added or removed like any other role.

**Static membership.** A role's static members are the metaverse objects (typically `person` objects) explicitly assigned to the role. Membership is established by adding a metaverse object as a member, and removed by deleting that membership.

**API key roles.** API keys carry roles directly (rather than via metaverse-object membership). This means rotating a person's role membership doesn't affect already-issued API keys, and revoking an API key doesn't change a person's role membership.

## Administrator-lockout safety

The API includes two safety checks specifically for the Administrator role; both reject the request rather than risk locking everyone out:

- You cannot remove **yourself** from the Administrator role
- You cannot remove the **last remaining member** of the Administrator role

If you genuinely need to transfer admin to a different identity, add the new admin first, then remove the old one.

## See also

- [API Keys](../api-keys/index.md) -- API keys carry roles directly; the same security model applies
- [Authentication](../authentication.md) -- how authenticated principals are identified
- [PowerShell: Security](../../powershell/security.md) -- cmdlets that wrap these endpoints
