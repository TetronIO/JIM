---
title: Connected Systems
---

# Connected Systems

A **Connected System** is any external directory, database, or file that JIM synchronises identity data with. Connected Systems are the endpoints of JIM's hub-and-spoke architecture: they provide source data (e.g. an HR system) and receive provisioned data (e.g. an LDAP directory).

Every Connected System is associated with a [connector](../connectors/index.md) that knows how to talk to its kind of external store, and holds a connector space of imported objects, a discovered schema, and (where applicable) a partition and container hierarchy.

## What a Connected System contains

- **Connection details**<br /> How to reach the external system: server address, credentials, file path, and other connector-specific settings. The Settings tab groups these into a collapsible accordion by category (Connectivity, General, Export, and so on) so dense connector configuration stays easy to scan.
- **Discovered schema**<br /> The object types and attributes available in the external system, populated on first contact.
- **Connector space**<br /> A staging area that holds JIM's local copy of the external system's data.
- **Run Profiles**<br /> Configured operations (import, sync, export) that can be executed against the system.
- **Synchronisation Rules**<br /> The rules that govern how data flows between this system and the metaverse.

## The connector space

The connector space is a critical concept. It is a staging area between the external system and the metaverse: when JIM imports data from a Connected System, it does not write directly to the metaverse. Instead, it creates or updates **Connected System Objects (CSOs)** in the connector space; the metaverse is only updated during the explicit synchronisation phase.

--8<-- "assets/diagrams/sync-pipeline.svg"

<p class="jim-diagram-caption">Imported data is staged in the connector space as Connected System Objects; the Metaverse is only touched during the synchronisation phase, and exports stage the same way in reverse.<span class="jimdg-caption-motion"> Moving dots trace data through the pipeline.</span></p>

This two-stage approach gives you:

- **Isolation**<br /> Problems during import do not corrupt the metaverse.
- **Visibility**<br /> Administrators can inspect imported data before it affects identities.
- **Comparison**<br /> JIM can detect what has changed between imports.
- **Rollback potential**<br /> The metaverse is only updated in the sync phase.

### Connected System Objects (CSOs)

A **CSO** is JIM's local representation of an object in an external system. Each CSO holds:

- **Distinguished name or anchor**<br /> A unique identifier that maps to the external object.
- **Attributes**<br /> The attribute values as imported from the external system.
- **Link to metaverse**<br /> If the CSO has been joined or projected, it links to a Metaverse Object (MVO).
- **Pending Exports**<br /> Changes queued to be sent back to the external system.

CSOs have a lifecycle:

1. **Created** during import when a new object is discovered in the external system
2. **Updated** during subsequent imports when attribute values change
3. **Joined** or **projected** during synchronisation, to link with an MVO
4. **Obsoleted** when the object no longer exists in the external system

## Partitions and containers

A **partition** is a top-level logical division of a connector space that mirrors a boundary defined by the external system. Partitions exist in JIM primarily to service LDAP-style directories and their naming contexts (NCs): the discrete directory trees that an LDAP server hosts. The separate domain partitions within an Active Directory forest, or the distinct naming contexts exposed by an OpenLDAP server, each surface as a partition in JIM.

Most Connected Systems do not support partitions. A flat file, a SQL table, or a SCIM endpoint has no concept of multiple naming contexts, so its connector space has no partitions.

Inside a partition, or directly inside the connector space of a connector that does not support partitions, you can have **containers**. Containers are a separate, lower-order logical construct that sits beneath partitions; they exist mainly to support LDAP organisational units (OUs) and similar hierarchical groupings. Containers can be nested arbitrarily deep, and JIM loads the full hierarchy so administrators can select nested containers (for example `OU=Contractors,OU=Users,DC=company,DC=local`) for import or export.

!!! note "Partitions and OUs are different concepts"
    Partitions and organisational units (OUs) are distinct. A partition is a top-level boundary on the external system; an OU is a sub-tree within a partition and is modelled in JIM as a container.

| Construct | Scope | Example | Available on |
|-----------|-------|---------|--------------|
| **Partition** | Top-level boundary defined by the external system; discovered, not invented, by JIM | An Active Directory domain naming context (`DC=company,DC=local`) | LDAP-style connectors only |
| **Container** | Sub-tree within a partition, or within the connector space of a non-partitioned system | An OU (`OU=Users,DC=company,DC=local`) | Most connectors that expose hierarchy |

In practice, selecting a partition brings an entire naming context into scope, while selecting containers narrows what is imported within that partition (or within the connector space for connectors that have no partitions).

## Unresolved reference handling

When an import stages a reference attribute value (for example a group member's Distinguished Name) that does not correspond to any object in the connector space, JIM cannot resolve the reference. The most common cause is the referenced object sitting outside the configured [Container Scope](#partitions-and-containers), which can be entirely deliberate: excluding foreign or out-of-remit objects from import is a normal scoping decision.

Each Connected System has an **Unresolved Reference Handling** setting that controls what happens when this occurs during import:

| Mode | Behaviour |
|------|-----------|
| **Error** (default) | Each affected object's Run Profile execution item is marked with an Unresolved Reference error, and the Activity completes with a warning status showing the errored items. Choose this when every reference is expected to resolve. |
| **Warn** | No per-object errors are raised. The Activity completes with a warning carrying a summary of how many references could not be resolved. Choose this when unresolved references are worth a glance but should not read as failures. |
| **Ignore** | No per-object errors and no Activity warning; the import completes successfully. Choose this when unresolved references are expected and benign. |

Whichever mode is selected, genuine data-quality issues remain discoverable:

- **Connected System Objects**<br /> Unresolved reference values stay stored on the affected objects, so they can be inspected on the object's detail page at any time.
- **PowerShell**<br /> `Get-JIMConnectedSystemUnresolvedReferenceCount` reports how many unresolved references a Connected System currently holds.
- **Service log**<br /> Every unresolved reference is logged (at Warning level in Warn mode, Debug level in Ignore mode), along with a summary count at the end of reference resolution.

Set the mode from the **Import Behaviour** panel on the Connected System's Settings tab, with `Set-JIMConnectedSystem -UnresolvedReferenceHandling`, or via the REST API.

## Pending Exports

Changes destined for the Connected System that have been computed by synchronisation but not yet written back. Run an export Run Profile to flush them. Inspecting Pending Exports is the right place to look when you want to know "what is JIM about to change in this system?"

## Common workflows

**Setting up a new Connected System:**

1. Choose the connector type (the connector defines how JIM talks to the external store)
2. Create the Connected System with the chosen connector
3. Configure connector settings (credentials, base DN, file paths, etc.)
4. Import the schema to discover object types and attributes
5. Select the object types and attributes you care about
6. Configure partitions and containers if the connector exposes hierarchy
7. Create [Run Profiles](run-profiles.md) for import, sync, and export operations
8. Add [Synchronisation Rules](synchronisation-rules.md) to define how data flows between this system and the metaverse

**Removing a Connected System:**

1. Run a deletion preview to understand the impact (which Metaverse Objects become disconnected, which Synchronisation Rules become invalid)
2. Delete the Connected System. Small systems are removed immediately; larger systems, or a system with a running sync, are queued and run as a background activity.

Deleting a Connected System records a final snapshot of its configuration in the [configuration change history](activities.md#configuration-change-history), so a decommissioned system's last-known state, and who removed it, remain auditable after it is gone. You can attach an optional reason in the admin portal delete dialog, with `Remove-JIMConnectedSystem -ChangeReason`, or via the REST API. As with all such snapshots, connector secrets are recorded as changed but never stored.

## Manage Connected Systems

- **JIM portal**<br /> Connected Systems area of the admin UI
- **PowerShell**<br /> [Connected Systems cmdlets](../powershell/connected-systems.md) (`Get-JIMConnectedSystem`, `New-JIMConnectedSystem`, `Set-JIMConnectedSystem`, etc.)
- **REST API**<br /> Connected Systems endpoints in the [interactive API reference](../../api/reference/)

## See also

- [Connectors](../connectors/index.md) -- the connector types JIM ships with, and what each one does
- [Concepts: Architecture](../concepts/architecture.md) -- how Connected Systems fit into JIM's hub-and-spoke model
- [Run Profiles](run-profiles.md) -- the operations executed against a Connected System
- [Synchronisation Rules](synchronisation-rules.md) -- how data flows between a Connected System and the metaverse
