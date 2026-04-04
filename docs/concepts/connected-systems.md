# Connected Systems

A **connected system** is any external directory, database, or file that JIM synchronises identity data with. Connected systems are the endpoints of the hub-and-spoke architecture -- they provide source data (e.g., an HR system) and receive provisioned data (e.g., Active Directory).

## What is a Connected System?

A connected system in JIM represents the configuration and state for a single external data source or target. It includes:

- **Connection details** -- how to reach the external system (server address, credentials, file path, etc.)
- **Schema** -- the object types and attributes available in the external system
- **Connector space** -- a staging area that holds a local copy of the external system's data
- **Run profiles** -- configured operations (import, export) that can be executed against the system
- **Sync rules** -- the rules that govern how data flows between this system and the metaverse

## Connector Space

The **connector space** is a critical concept in JIM's architecture. It acts as a staging area between the external system and the metaverse.

When JIM imports data from a connected system, it does not write directly to the metaverse. Instead, it creates or updates **Connected System Objects (CSOs)** in the connector space. These CSOs are local representations of the objects in the external system.

```
+-------------------+       +-------------------+       +-----------------+
|  External System  | ----> |  Connector Space  | ----> |   Metaverse     |
|  (e.g., AD)       |       |  (CSOs)           |       |   (MVOs)        |
+-------------------+       +-------------------+       +-----------------+
                                  Import                     Sync
```

This two-stage approach provides several benefits:

- **Isolation** -- problems during import do not corrupt the metaverse
- **Visibility** -- administrators can inspect imported data before synchronisation
- **Comparison** -- JIM can detect what has changed between imports
- **Rollback potential** -- the metaverse is only updated during the explicit sync phase

### Connected System Objects (CSOs)

A **CSO** is JIM's local representation of an object in an external system. Each CSO holds:

- **Distinguished name or anchor** -- a unique identifier that maps to the external object
- **Attributes** -- the attribute values as imported from the external system
- **Link to metaverse** -- if the CSO has been joined or projected, it links to a MetaverseObject (MVO)
- **Pending exports** -- changes queued to be sent back to the external system

CSOs have a lifecycle:

1. **Created** during import when a new object is discovered in the external system
2. **Updated** during subsequent imports when attribute values change
3. **Joined** or **projected** during synchronisation to link with an MVO
4. **Obsoleted** when the object no longer exists in the external system

## Partitions

A **partition** is a logical division within a connected system. Partitions allow you to scope imports and exports to specific subsets of the external system's data.

For example, in an LDAP directory, partitions typically correspond to organisational units (OUs) or containers. You might configure JIM to import only from `OU=Users,DC=company,DC=local` rather than the entire directory tree.

Partitions are particularly useful for:

- **Performance** -- importing only the data you need rather than the entire directory
- **Scoping** -- limiting JIM's visibility to specific parts of a connected system
- **Multi-tenant scenarios** -- different partitions can be handled by different sync rules

## Available Connectors

JIM ships with the following connectors:

### JIM File Connector

The File Connector imports from and exports to **CSV and delimited text files**. It supports:

- Configurable delimiters (comma, tab, pipe, semicolon, or custom)
- Header row detection
- Auto-confirm export (changes are written directly to the output file)
- Suitable for integrating with systems that produce flat-file extracts (HR exports, batch feeds, etc.)

### JIM LDAP Connector (Active Directory)

The LDAP Connector supports **Active Directory** and **Samba AD** directories. It provides:

- Full import and export (create, update, delete)
- SSL/TLS and StartTLS support
- Container creation during provisioning
- Schema discovery for available object types and attributes
- Active Directory-specific features (userAccountControl, FILETIME dates, etc.)

### JIM LDAP Connector (OpenLDAP / RFC 4512)

The OpenLDAP Connector supports **OpenLDAP**, **389 Directory Server**, and other **RFC 4512-compliant** directories. It includes:

- Parallel imports for large directories
- Delta import via the accesslog overlay
- Partition-scoped imports
- Full export support (create, update, delete)

## Planned Connectors

The connector framework is extensible, and additional connectors are planned for future releases:

| Connector | Description |
|-----------|-------------|
| **SCIM 2.0** | Standard protocol for cross-domain identity management |
| **SQL** | Database connector for SQL Server, PostgreSQL, MySQL, and Oracle |
| **PowerShell** | Custom script-based connector for bespoke integrations |
| **Web Services / REST** | Generic REST API connector with OAuth2 and API key authentication |

See the [GitHub Milestones](https://github.com/TetronIO/JIM/milestones) for the current roadmap and planned delivery timelines.
