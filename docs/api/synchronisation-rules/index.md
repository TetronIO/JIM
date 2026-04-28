---
title: Synchronisation Rules
---

# Synchronisation Rules

A Synchronisation Rule defines how objects flow between a connected system and the JIM metaverse. Each rule maps a connected system object type to a metaverse object type, specifies a direction (import or export), and contains the attribute mappings that control how data is transformed in flight.

Synchronisation rules are the core configuration mechanism for identity synchronisation. They determine which objects are joined, projected, or provisioned, and how attributes are shaped during the process.

> Endpoint reference for this resource is in the [Scalar API reference](../index.md#where-to-find-what). This page covers the model and the common end-to-end workflows.

## Key Concepts

**Direction.** A rule is either Import or Export.

- **Import** rules flow data from the connected system into the metaverse. They can optionally project new metaverse objects when no match is found for an incoming connector space object.
- **Export** rules flow data from the metaverse out to the connected system. They can optionally provision new objects in the connected system, and support scoping criteria to control which metaverse objects are eligible for export.

**Attribute mappings.** Define how individual attributes are transformed between systems. Mappings can be direct (one attribute to another), multi-source (combining several attributes), or expression-based using JIM's [expressions](../../concepts/expressions.md) language.

**Scoping criteria.** Control which objects are in scope for a rule. For export rules, criteria evaluate metaverse object attributes; for import rules, criteria evaluate connected system object attributes. Criteria are organised into groups with AND/OR logic and support nested groups for complex conditions.

**Object matching rules.** Determine how connector space objects are matched to existing metaverse objects during synchronisation. Matching can be configured at the connected system level (simple mode, shared across all rules for that system) or per synchronisation rule (advanced mode).

**Enforce state.** When set on an export rule, JIM detects and remediates attribute drift in the connected system: if an exported attribute is changed externally, the next sync run will pull it back to the metaverse-derived value.

## Common Workflows

**Setting up an import rule:**

1. Create a synchronisation rule with direction `Import`, choosing the source object type in the connected system and the target metaverse object type
2. Add attribute mappings to flow values from connector space attributes onto the corresponding metaverse attributes
3. Configure object matching rules so incoming objects join to the right metaverse objects
4. Decide whether to project new metaverse objects when no match exists, and enable that on the rule

**Setting up an export rule with scoping:**

1. Create a synchronisation rule with direction `Export`, with `provisionToConnectedSystem` enabled if you want JIM to create objects in the target system
2. Add attribute mappings to flow values from metaverse attributes onto connector space attributes
3. Add scoping criteria so only the relevant metaverse objects are exported (for example, only `person` objects whose `department` is `IT`)
4. Configure object matching rules for the export direction
5. Decide whether to enforce state (i.e. detect and correct drift in the target system)

## See also

- [Concepts: Synchronisation Rule](../../concepts/synchronisation-rules.md) -- conceptual overview of rules and how they fit into the pipeline
- [Concepts: Synchronisation Pipeline](../../concepts/synchronisation-pipeline.md) -- how import, sync, and export stages relate
- [Concepts: Expressions](../../concepts/expressions.md) -- the expression language used in attribute mappings
- [PowerShell: Synchronisation Rules](../../powershell/synchronisation-rules.md) -- cmdlets that wrap these endpoints
