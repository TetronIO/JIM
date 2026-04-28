---
title: Metaverse
---

# Metaverse

The **metaverse** is JIM's central identity store. It contains object types (the schema), attributes (the fields), and the identity objects themselves. All synchronisation flows through the metaverse: import rules bring data in from [connected systems](connected-systems.md), and export rules push data out.

The metaverse schema is administrator-defined. JIM does not impose a fixed schema, so you can model any identity domain that fits your organisation, from the conventional `person` and `group` types through to bespoke types like `serviceAccount`, `mailbox`, or `device`.

## Object types

**Object types** define the schema categories in the metaverse. Typical examples are `person` and `group`, but you can define any types your organisation needs. Each object type has its own attribute set and configurable deletion behaviour.

### Deletion behaviour

Each object type has its own rules for when its objects should be deleted from the metaverse. Common choices:

- **Immediate**<br /> The object is deleted as soon as all its connector links are removed.
- **Grace period**<br /> The object enters a pending-deletion state and is removed after a configurable period, giving administrators time to intervene if a deletion was triggered in error.

The grace period is the right default for production: it protects against transient source-system glitches that would otherwise wipe identities out.

## Attributes

**Attributes** define the fields available on metaverse objects. Examples include `displayName`, `mail`, and `employeeId`. Attributes can be:

- **Single-valued or multi-valued**<br /> A multi-valued attribute holds a list of values (e.g. group memberships, email aliases).
- **Of various data types**<br /> String, integer, datetime, boolean, reference (a link to another metaverse object), and so on.

Attributes are scoped to the object types that use them: when you add an attribute to the metaverse, you choose which object types it applies to. The same attribute name can carry different meanings on different object types if you genuinely need that, though in practice most attributes are reused identically across types where they apply.

## Objects

**Objects** are the identity records: a single `person`, `group`, or whatever object types you have defined. Each object has a type, attribute values, and may be linked to one or more Connected System Objects in connected systems. Those links are how data flows between the external systems and the metaverse during synchronisation.

## Pending deletions

Pending deletions track metaverse objects awaiting final deletion after all their connector space links have been removed. The grace period (configured per object type) gives administrators time to intervene before deletion is finalised.

JIM exposes both the list of currently pending deletions and a summary view, which is useful for spotting unexpected mass-deletion events early.

## Searching the metaverse

The metaverse supports filtering and a fast named-search API. The named-search API is driven by [predefined searches](predefined-searches.md), which let administrators create reusable search definitions that the portal and integrations can call by URI.

## Manage the metaverse

- **JIM portal**<br /> Metaverse area of the admin UI for objects, object types, attributes, and pending deletions
- **PowerShell**<br /> [Metaverse cmdlets](../powershell/metaverse.md) (`Get-JIMMetaverseObject`, `Get-JIMMetaverseObjectType`, `Get-JIMMetaverseAttribute`, etc.)
- **REST API**<br /> Metaverse endpoints in the [interactive API reference](../api/index.md)

## See also

- [Concepts: Architecture](../concepts/architecture.md) -- how the metaverse fits into JIM's hub-and-spoke architecture
- [Concepts: Synchronisation Pipeline](../concepts/synchronisation-pipeline.md) -- how data flows through the metaverse during import, sync, and export
- [Concepts: JML Lifecycle](../concepts/jml-lifecycle.md) -- joiner/mover/leaver lifecycle and how it relates to metaverse object state
- [Synchronisation Rules](synchronisation-rules.md) -- how data flows in and out of the metaverse
- [Predefined Searches](predefined-searches.md) -- named, reusable searches over metaverse objects
