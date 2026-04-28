---
title: Connected Systems
---

# Connected Systems

A Connected System represents an external identity store that synchronises with the JIM metaverse. Examples include LDAP directories, HR databases, and file-based data sources.

Each connected system is associated with a [connector](../../connectors/index.md) that defines how JIM communicates with the external store, and contains a connector space of imported objects, a discovered schema, and an optional partition or container hierarchy.

> Endpoint reference for this resource (request and response schemas, query parameters, error codes) is in the [interactive API reference](../index.md#where-to-find-what). The page below covers concepts and end-to-end workflows.

## Key Concepts

**Connector space.** Every connected system has a connector space: the set of imported objects from that system, kept synchronised with their external counterparts. Objects in the connector space are joined or projected to objects in the metaverse during synchronisation.

**Schema.** Discovered from the external system on first contact. The schema lists the object types (e.g. `user`, `group`) and attributes available, and is the basis for configuring synchronisation rules.

**Partitions and containers.** For connectors that expose hierarchy (such as LDAP), partitions and containers control which subtrees JIM imports from. Both are imported alongside the schema and are individually selectable.

**Pending exports.** Changes destined for the connected system that have been computed by synchronisation but not yet written back. Run an export run profile to flush them.

## Common Workflows

**Setting up a new connected system:**

1. Choose the connector type (the connector defines how JIM talks to the external store)
2. Create the connected system with the chosen connector
3. Configure connector settings (credentials, base DN, file paths, etc.)
4. Import the schema to discover object types and attributes
5. Select the object types and attributes you care about
6. Configure partitions and containers if the connector exposes hierarchy
7. Create [run profiles](../run-profiles/index.md) for import, sync, and export operations
8. Add [synchronisation rules](../synchronisation-rules/index.md) to define how data flows between this system and the metaverse

**Removing a connected system:**

1. Run a deletion preview to understand the impact (which metaverse objects become disconnected, which sync rules become invalid)
2. Delete the connected system; the operation is asynchronous and runs as a background activity

## See also

- [Concepts: Connected Systems](../../concepts/connected-systems.md) -- conceptual overview and lifecycle
- [Run Profiles](../run-profiles/index.md) -- how operations are executed against a connected system
- [Synchronisation Rules](../synchronisation-rules/index.md) -- how data flows in and out
- [PowerShell: Connected Systems](../../powershell/connected-systems.md) -- cmdlets that wrap these endpoints
