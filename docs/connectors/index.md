# Connectors

## What are Connectors?

Connectors are adapters that enable JIM to communicate with external systems. Each connector handles the protocol-specific details of connecting to, reading from, and writing to a particular type of data source or target -- whether that is a directory service, a file, a database, or an API.

Connectors abstract away the complexity of each external system, presenting a consistent interface to JIM's synchronisation engine. This means the core synchronisation logic does not need to know the specifics of how to talk to any particular system -- it simply works with connectors through a standard set of operations.

## How Connectors Fit in the Architecture

JIM uses a hub-and-spoke architecture with the **metaverse** at the centre. Connectors sit at the edges, bridging the gap between external systems and JIM's internal data model.

When a connector imports data from an external system, it does not write directly to the metaverse. Instead, it populates the **connector space** -- a staging area that holds local representations of external objects called **Connected System Objects (CSOs)**. During synchronisation, CSOs are joined or projected to **Metaverse Objects (MVOs)** based on configured sync rules. When exporting, the process reverses: changes flow from the metaverse through sync rules to CSOs, and then the connector pushes those changes back to the external system.

```
+-----------+     +-------------------+     +-------------------+     +-----------+
|  External | --> |  Connector Space  | --> |    Metaverse      | --> |  External |
|  System A |     |  (CSOs)           |     |    (MVOs)         |     |  System B |
+-----------+     +-------------------+     +-------------------+     +-----------+
                       Import                    Sync                    Export
```

Each connected system in JIM has:

- **Connection settings** -- how to reach the external system (hostname, credentials, file path, etc.)
- **Schema** -- the object types and attributes available in the external system
- **Connector space** -- the staging area holding imported CSOs
- **Run profiles** -- configured operations (full import, delta import, export)
- **Sync rules** -- rules governing how data flows between the connected system and the metaverse

For more detail on these concepts, see [Connected Systems](../concepts/connected-systems.md).

## Available Connectors

JIM ships with the following built-in connectors:

| Connector | Description | Capabilities |
|-----------|-------------|--------------|
| [JIM File Connector](jim-file-connector.md) | CSV and delimited text files | Full Import, Export |
| [JIM LDAP Connector](jim-ldap-connector.md) | Active Directory, OpenLDAP, 389 Directory Server, and other RFC 4512-compliant directories | Full Import, Delta Import, Export |

## Planned Connectors

For planned connectors including SQL databases, PowerShell, SCIM 2.0, and REST APIs, see the [Roadmap](../reference/roadmap.md).

## Custom Connectors

JIM's connector framework is extensible. If none of the built-in connectors meet your requirements, you can develop a custom connector that implements JIM's connector interfaces. For guidance on building your own connector, see [Writing Connectors](../developer/connectors.md).
