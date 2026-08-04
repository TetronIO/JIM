# Object Naming

Wherever JIM shows you an object (a search result, a Pending Export, a causality chain, a change history entry) it has to decide what to call it. Identity data rarely offers a single reliable answer: an Active Directory group carries `cn` but often no `displayName`, a CSV feed might offer neither, and some objects genuinely have no name at all.

JIM resolves this the same way everywhere: **try an ordered list of naming attributes, then fall back to an identifier.** One rule, applied consistently, so the same object reads the same way on every screen.

## 🏷️ The order

### Connected System Objects

| Order | Source |
|-------|--------|
| 1 | `displayName` attribute |
| 2 | `cn` attribute |
| 3 | `name` attribute |
| 4 | External ID |
| 5 | Secondary External ID (the Distinguished Name, for LDAP systems) |

The first of these that holds an actual value wins. Attribute names are matched **case-insensitively**, because the schema belongs to your connected system rather than to JIM, and directory products differ on casing.

An empty or whitespace-only value counts as absent, so an object whose `displayName` is blank falls through to its `cn` rather than displaying nothing.

### Metaverse Objects

| Order | Source |
|-------|--------|
| 1 | Display Name attribute |
| 2 | Common Name attribute |
| 3 | The object's ID |

Metaverse attribute names are matched exactly, because the [Metaverse schema](../configuration/metaverse.md) is JIM's own and its names are known.

## 👥 Why groups behave differently to users

This ordering matters most for groups. A person object almost always arrives with a `displayName`, so it is named by rule 1 and nothing else applies. Group objects in LDAP and Active Directory frequently have only `cn`, so they are named by rule 2.

Before this ordering existed, such groups fell straight through to their External ID and appeared as raw identifiers:

```text
1f16ccb0-1f01-1041-8be1-eb9f4cb3f25e
```

They now appear as, for example, `Project-GlobalGateway`.

## 🔢 Names alongside identifiers

Some views show an object's name **and** its External ID together, so you can confirm which object you are looking at:

```text
Project-GlobalGateway (1f16ccb0-1f01-1041-8be1-eb9f4cb3f25e)
```

Where an object carries none of the naming attributes, its name resolves to the External ID and both halves would read the same. JIM shows the value once in that case rather than repeating it.

## 🔎 Sorting and searching

Lists sort by the same resolved name they display, so the sort order matches what you see rather than an underlying attribute you do not.

Searching an object list matches **any** of the naming attributes, not just the one being displayed. Typing a group's `cn` finds it even where the list is showing a `displayName`.

## 💡 Getting better names

JIM never invents a name; it only surfaces what your data holds. If objects are displaying as identifiers, the fix is in the data rather than in JIM:

- **For Connected System Objects**, confirm the naming attribute is [selected in the Connected System's schema](../configuration/connected-systems.md). An attribute that is present in the directory but not selected for import is not available to name the object.
- **For Metaverse Objects**, add an [Attribute Flow](../configuration/synchronisation-rules.md) that populates Display Name (or Common Name) on the object type in question. Objects with neither will display their ID.
