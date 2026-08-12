# Connectors

## What are Connectors?

Connectors are adapters that enable JIM to communicate with external systems. Each connector handles the protocol-specific details of connecting to, reading from, and writing to a particular type of data source or target -- whether that is a directory service, a file, a database, or an API.

Connectors abstract away the complexity of each external system, presenting a consistent interface to JIM's synchronisation engine. This means the core synchronisation logic does not need to know the specifics of how to talk to any particular system -- it simply works with connectors through a standard set of operations.

## How Connectors Fit in the Architecture

JIM uses a hub-and-spoke architecture with the **metaverse** at the centre. Connectors sit at the edges, bridging the gap between external systems and JIM's internal data model.

When a connector imports data from an external system, it does not write directly to the metaverse. Instead, it populates the **connector space** -- a staging area that holds local representations of external objects called **Connected System Objects (CSOs)**. During synchronisation, CSOs are joined or projected to **Metaverse Objects (MVOs)** based on configured Synchronisation Rules. When exporting, the process reverses: changes flow from the metaverse through Synchronisation Rules to CSOs, and then the connector pushes those changes back to the external system.

--8<-- "assets/diagrams/hub-and-spoke.svg"

<p class="jim-diagram-caption">Connectors sit at JIM's edge, carrying data between Connected Systems and the synchronisation pipeline; every flow passes through the Metaverse, never directly between systems. Dashed elements are not yet available.<span class="jimdg-caption-motion"> Moving dots trace identity data in flight.</span></p>

Each Connected System in JIM has:

- **Connection settings**<br /> How to reach the external system (hostname, credentials, file path, etc.)
- **Schema**<br /> The object types and attributes available in the external system.
- **Connector space**<br /> The staging area holding imported CSOs.
- **Run Profiles**<br /> Configured operations (full import, delta import, export).
- **Synchronisation Rules**<br /> Rules governing how data flows between the Connected System and the metaverse.

For more detail on these concepts, see [Connected Systems](../configuration/connected-systems.md).

## 🛠️ Available Connectors

JIM ships with the following built-in connectors:

| Connector | Description | Capabilities |
|-----------|-------------|--------------|
| [JIM File Connector](jim-file-connector.md) | CSV and delimited text files | Full Import, Export |
| [JIM LDAP Connector](jim-ldap-connector.md) | Active Directory, OpenLDAP, 389 Directory Server, and other RFC 4512-compliant directories | Full Import, Delta Import, Export |
| [JIM SCIM 2.0 Client Connector](jim-scim-connector.md) | Any system exposing a SCIM 2.0 service provider interface (RFC 7643/7644) | Full Import, Delta Import, Export |
| JIM SQL Connector | Microsoft SQL Server and Oracle Database, through fully managed ADO.NET drivers | Full Import, Delta Import, Export |

The JIM SQL Connector is selectable when creating a Connected System. Its settings document themselves:
each one explains what it is for and what goes wrong if it is not right. A full guide, covering the Object
Types document and a worked example for each supported database, is still being written.

One Connected System covers several tables and views at once. Each Object Type names its own table or
view, the columns forming its anchor, any column carrying another object's anchor as a reference, and any
related table whose rows gather onto the parent as a multi-valued attribute. Date and time columns that
carry no offset are interpreted in the Database Time Zone declared on the Connected System, and that
interpretation is inverted on export; columns stating their own offset are left alone.

## 🗺️ Upcoming Connectors

PostgreSQL and MySQL support is planned for the JIM SQL Connector, so that one connector covers those
engines too rather than one connector per database engine. PowerShell and REST API connectors are planned.
See the [Roadmap](../reference/roadmap.md) for the full picture.

## 🧩 Custom Connectors

JIM's connector framework is extensible. If none of the built-in connectors meet your requirements, you can develop a custom connector that implements JIM's connector interfaces. For guidance on building your own connector, see [Writing Connectors](../developer/connectors.md).
