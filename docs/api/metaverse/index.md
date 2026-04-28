---
title: Metaverse
---

# Metaverse

The Metaverse is JIM's central identity store. It contains object types (the schema), attributes (the fields), and the identity objects themselves. All synchronisation flows through the metaverse: import rules bring data in from connected systems, and export rules push data out.

> Endpoint reference for this resource is in the [Scalar API reference](../index.md#where-to-find-what). This page covers the concepts.

## Key Concepts

**Object types** define the schema categories in the metaverse (typical examples: `person`, `group`). Each object type has its own attribute set and configurable deletion rules (e.g. immediate deletion, or a grace period before pending deletion is finalised).

**Attributes** define the fields available on metaverse objects (e.g. `displayName`, `mail`, `employeeId`). Attributes can be single-valued or multi-valued, and support several data types (string, integer, datetime, boolean, reference, etc.). The set of attributes on an object type is administrator-defined; JIM does not impose a fixed schema.

**Objects** are the identity records. Each object has a type, attribute values, and may be linked to one or more connector space objects in connected systems. The links are how data flows between the external systems and the metaverse during synchronisation.

**Pending deletions** track metaverse objects awaiting final deletion after all their connector space links have been removed. The grace period (configured per object type) gives administrators time to intervene if a deletion was triggered in error.

**Search.** The metaverse supports filtering and a fast named-search API driven by [Predefined Searches](../predefined-searches/index.md).

## See also

- [Concepts: Architecture](../../concepts/architecture.md) -- how the metaverse fits into JIM's architecture
- [Concepts: Synchronisation Pipeline](../../concepts/synchronisation-pipeline.md) -- how data flows through the metaverse during import, sync, and export
- [Concepts: JML Lifecycle](../../concepts/jml-lifecycle.md) -- joiner/mover/leaver lifecycle and how it relates to metaverse object state
- [Predefined Searches](../predefined-searches/index.md) -- named searches over metaverse objects
- [PowerShell: Metaverse](../../powershell/metaverse.md) -- cmdlets that wrap these endpoints
