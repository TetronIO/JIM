---
title: Object Naming
description: How JIM decides what to call an object on every screen, from its naming attributes down to an identifier.
type: concept
audience: administrator
---

# Object Naming

Wherever JIM shows you an object (a search result, a Pending Export, a causality chain, a change history entry) it has to decide what to call it. Identity data rarely offers a single reliable answer: an Active Directory group carries `cn` but often no `displayName`, a CSV feed might offer neither, and some objects genuinely have no name at all.

```jim-diagram
archetype: comparison
title: One naming rule, applied to both kinds of object
desc: "JIM names an object by trying an ordered list of naming attributes, then falling back to an identifier; the first that holds an actual value wins, and a blank or whitespace-only value counts as absent. A Connected System Object tries displayName, then cn, then name, then its External ID, and then its Secondary External ID, which is the Distinguished Name for LDAP systems; its attribute names are matched ignoring case, because the schema belongs to your Connected System. A Metaverse Object tries Display Name, then Common Name, then its ID; its attribute names are matched exactly, because the Metaverse schema is JIM's own."
columns:
- {label: Connected System Objects}
- {label: Metaverse Objects}
rows:
- label: Naming attributes, in order
  cells:
  - {text: "`displayName`, `cn`, `name`"}
  - {text: "Display Name, Common Name"}
- label: Then an identifier
  cells:
  - {text: "External ID, then Secondary External ID", sub: "the Distinguished Name, for LDAP systems"}
  - {text: the object's ID}
- label: Attribute names matched
  cells:
  - {text: ignoring case, sub: the schema is your system's}
  - {text: exactly, sub: the schema is JIM's own}
footer: "The first that holds a value wins; a blank or whitespace-only value counts as absent"
```

JIM resolves this the same way everywhere: **try an ordered list of naming attributes, then fall back to an identifier.** One rule, applied consistently, so the same object reads the same way on every screen.

## :material-sort-variant: The order

### Connected System Objects

| Order | Source |
|-------|--------|
| 1 | `displayName` attribute |
| 2 | `cn` attribute |
| 3 | `name` attribute |
| 4 | External ID |
| 5 | Secondary External ID (the Distinguished Name, for LDAP systems) |

The first of these that holds an actual value wins. Attribute names are matched **case-insensitively**, because the schema belongs to your Connected System rather than to JIM, and directory products differ on casing.

An empty or whitespace-only value counts as absent, so an object whose `displayName` is blank falls through to its `cn` rather than displaying nothing.

### Metaverse Objects

| Order | Source |
|-------|--------|
| 1 | Display Name attribute |
| 2 | Common Name attribute |
| 3 | The object's ID |

Metaverse attribute names are matched exactly, because the [Metaverse schema](../configuration/metaverse.md) is JIM's own and its names are known.

## :material-account-group-outline: Why groups behave differently to users

This ordering matters most for groups. A person object almost always arrives with a `displayName`, so it is named by rule 1 and nothing else applies. Group objects in LDAP and Active Directory frequently have only `cn`, so they are named by rule 2.

Before this ordering existed, such groups fell straight through to their External ID and appeared as raw identifiers:

```text
1f16ccb0-1f01-1041-8be1-eb9f4cb3f25e
```

They now appear as, for example, `Project-GlobalGateway`.

## :material-identifier: Names alongside identifiers

Some views show an object's name **and** its External ID together, so you can confirm which object you are looking at:

```text
Project-GlobalGateway (1f16ccb0-1f01-1041-8be1-eb9f4cb3f25e)
```

Where an object carries none of the naming attributes, its name resolves to the External ID and both halves would read the same. JIM shows the value once in that case rather than repeating it.

## :material-text-search: Sorting and searching

Lists sort by the same resolved name they display, so the sort order matches what you see rather than an underlying attribute you do not.

Searching an object list matches **any** of the naming attributes, not just the one being displayed. Typing a group's `cn` finds it even where the list is showing a `displayName`.

## :material-lightbulb-outline: Getting better names

JIM never invents a name; it only surfaces what your data holds. If objects are displaying as identifiers, the fix is in the data rather than in JIM:

- **For Connected System Objects**, confirm the naming attribute is [selected in the Connected System's schema](../configuration/connected-systems.md). An attribute that is present in the directory but not selected for import is not available to name the object.
- **For Metaverse Objects**, add an [Attribute Flow](../configuration/synchronisation-rules.md) that populates Display Name (or Common Name) on the object type in question. Objects with neither will display their ID.
