---
title: Connectors
description: What a Connector is, how Connectors fit into JIM, the Connectors JIM ships with, and what each can do.
type: concept
audience: evaluator
---

# Connectors

Connectors are adapters that enable JIM to communicate with external systems. Each connector handles the protocol-specific details of connecting to, reading from, and writing to a particular type of data source or target, whether that is a directory service, a file, a database, or an API.

Connectors abstract away the complexity of each external system, presenting a consistent interface to JIM's synchronisation engine. This means the core synchronisation logic does not need to know the specifics of how to talk to any particular system; it works with connectors through a standard set of operations.

## :material-sitemap-outline: How Connectors Fit in the Architecture

JIM uses a hub-and-spoke architecture with the **Metaverse** at the centre. Connectors sit at the edges, bridging the gap between external systems and JIM's internal data model.

When a connector imports data from an external system, it does not write directly to the Metaverse. Instead, it populates the **Connector Space**: a staging area that holds local representations of external objects called **Connected System Objects**. During synchronisation, Connected System Objects are joined or projected to **Metaverse Objects** based on configured Synchronisation Rules. When exporting, the process reverses: changes flow from the Metaverse through Synchronisation Rules to Connected System Objects, and then the connector pushes those changes back to the external system.

--8<-- "assets/diagrams/hub-and-spoke.svg"

<p class="jim-diagram-caption">Connectors sit at JIM's edge, carrying data between Connected Systems and the synchronisation pipeline; every flow passes through the Metaverse, never directly between systems.<span class="jimdg-caption-motion"> Moving dots trace identity data in flight.</span></p>

Each Connected System in JIM has:

- **Connection settings**<br /> How to reach the external system (hostname, credentials, file path, etc.)
- **Schema**<br /> The object types and attributes available in the external system.
- **Connector Space**<br /> The staging area holding imported Connected System Objects.
- **Run Profiles**<br /> Configured operations (full import, delta import, export).
- **Synchronisation Rules**<br /> Rules governing how data flows between the Connected System and the Metaverse.

For more detail on these concepts, see [Connected Systems](../configuration/connected-systems.md).

## :material-power-plug-outline: Available Connectors

JIM ships with the following built-in connectors. **Passwords** means the connector can set passwords, so it can receive initial passwords, on-demand password changes and Password Synchronisation. **Probe** means that when a synchronisation generates a value, such as an account name, the connector can ask its Connected System directly whether the value is already in use, which catches accounts JIM does not import (see [Checking availability in target systems](../configuration/generated-values-in-target-systems.md#checking-availability-in-target-systems)).

| Connector | Description | Capabilities | Probe |
|-----------|-------------|--------------|-------|
| [JIM File Connector](jim-file-connector.md) | CSV and delimited text files | Full Import, Export | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } |
| [JIM LDAP Connector](jim-ldap-connector.md) | Active Directory, Samba AD, OpenLDAP, 389 Directory Server, and other RFC 4512-compliant directories | Full Import, Delta Import, Export, [Passwords](../concepts/passwords.md) | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } |
| [JIM SCIM 2.0 Client Connector](jim-scim-connector.md) | Any system exposing a SCIM 2.0 service provider interface (RFC 7643/7644) | Full Import, Delta Import, Export | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } |
| [JIM SCIM 2.0 Service Provider Connector](jim-scim-service-provider-connector.md) | Identity providers and other SCIM 2.0 clients that push Users and Groups to JIM's own SCIM endpoint ([Preview](../administration/preview-features.md)) | Full Import, Delta Import | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } |
| [JIM SQL Connector](jim-sql-connector.md) | Microsoft SQL Server and Oracle Database, through fully managed ADO.NET drivers | Full Import, Delta Import, Export | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } |

A Connected System whose connector cannot probe is still checked against JIM's own records of it.

## :material-map-marker-question-outline: Other Systems

For a system or database engine none of these connectors reaches, see the [Roadmap](../reference/roadmap.md).

## :material-puzzle-outline: Custom Connectors

JIM runs the Connectors built into it, and does not load a Connector from outside its own release, so a Connector for another system is added to JIM itself. The [Roadmap](../reference/roadmap.md) lists the Connectors planned; to ask for another, tell us in the [Ideas category of GitHub Discussions](https://github.com/TetronIO/JIM/discussions/categories/ideas).
