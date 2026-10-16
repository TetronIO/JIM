---
title: LDAP Connector Auxiliary Object Classes
description: "Merge a directory's auxiliary object classes into an Object Type, and how the LDAP Connector discovers and writes them."
type: object
audience: administrator
---

# LDAP Connector Auxiliary Object Classes

On an RFC 4512 directory (OpenLDAP, 389 Directory Server), an auxiliary object class attaches to an individual entry rather than to a structural class in the schema. `inetOrgPerson` plus `posixAccount` is near-universal in OpenLDAP estates, and nothing in the schema says the two go together: it is a fact about each entry.

JIM therefore asks you which auxiliary classes an Object Type should carry, and offers everything it can find out as a suggestion rather than acting on it.

This section applies only to directories that publish an RFC 4512 subschema subentry. **Active Directory resolves its own auxiliary classes into each structural class**, so its Object Types already carry those attributes and none of the controls below appear.

## Merging a class into an Object Type

On a Connected System's **Schema** tab, open a structural Object Type's sub-tab. Its **Auxiliary Classes** panel shows what the Object Type is made of: its own class, then each auxiliary class merged into it as a chip you can remove, and an **Add auxiliary class** button.

The button opens a dialog listing the directory's auxiliary classes, with:

- a switch saying whether it is merged into this Object Type
- how many attributes merging it would contribute, which opens to list them by name, marking the ones the class requires and any credential attribute (which can never be selected, however the class is merged)
- a chip for each reason JIM has to suggest it (see [Suggestions](#suggestions) below)

The dialog opens on the **Suggested** classes where there are any, with **In use** (what a discovery run saw) and **All** one click away, and a search box for a class you know the name of. Change as many switches as you like, then **Apply** records the whole set at once.

A merged class's attributes join the Object Type's attribute table at the next **Refresh Schema**, carrying the class's name in the existing **Class** column, and you select and flow them like any other attribute.

Only what you enable is persisted, so a schema refresh can never silently change what an Object Type carries. A class the directory has removed disappears from the list, and the removal surfaces through the existing schema refresh confirmation rather than quietly.

## Suggestions

Two things can suggest a class, and neither ever applies itself:

| Suggestion | What it means |
|------------|---------------|
| **DIT Content Rule** | The directory publishes a rule (RFC 4512 section 4.1.6) saying this class may attach to entries of this structural class. It is a statement of what is permitted, not of what is in use. Most directories publish no such rules at all, so its absence says nothing. |
| **In use on N entries** | A discovery run read entries and saw this class on N of them. |

Every auxiliary class in the schema is listed whether or not anything suggests it, so a class you know the name of can always be found and enabled.

## Discovery

The classes the dialog lists come from the schema. **Discover classes in use**, at the foot of the same dialog, is a separate read, of the directory's entries, recording which auxiliary classes they actually carry. It changes no configuration.

| Scope | Reads | Trade-off |
|-------|-------|-----------|
| **Quick sample** | The first N entries of each Object Type (5,000 by default) | Fast, and enough to find the classes a population uses consistently. A directory returns entries in its own order, so a rarely used class can be missed; a quick sample can never prove a class unused. |
| **Full scan** | Every entry in scope, requesting only `objectClass` | Complete, and the only scope whose answer of "this class is not in use" means anything. It can take a long time on a large directory. |

LDAP cannot random-sample: paged searches return entries in server order, so a uniform "read 10%" would cost the same as reading everything. These two scopes are the honest options.

A run is queued as a worker task and reports against an Activity, so you can watch its progress and cancel it like any other long-running operation. A cancelled run keeps what it found: those classes are genuinely in use, and the ones it never reached are unknown. One run at a time per Connected System, because a full scan reads every object and two would double the load on a directory that is probably still serving authentication.

The panel shows whichever applies: never run, running (with a link to its Activity and a **Cancel**), last completed, or cancelled with partial results.

## How objectClass is written on export

JIM composes `objectClass` itself and refuses it as an Attribute Flow target, in the same way it refuses credential attributes. No hand-written flow can know which auxiliary classes a given entry needs, because that follows from which of the merged classes' attributes actually have values on that entry, and that differs entry by entry.

- **On create**, JIM writes the Object Type's own class plus every merged auxiliary class whose attributes the create is writing. A create that flows no `posixAccount` attribute does not claim `posixAccount`.
- **On update**, an entry that lacks a merged class gains it in the same modify that first flows one of that class's attributes. There is no blanket pass stamping the class onto every entry: adding a class without satisfying its MUSTs is itself an `objectClassViolation`, so convergence follows the flows.
- **MUSTs are enforced when the class is added**, not before. If the entry plus the outgoing change would not satisfy the class's required attributes, the export is refused with an error naming exactly what is missing, rather than being sent for the directory to reject in its own terms.

An auxiliary class's MUST appears as **Optional** in the attribute table, deliberately: entries that do not carry the class legitimately lack it.

**Select `objectClass` for import on any Object Type carrying merged classes.** JIM decides what an update must add by comparing the classes an entry already carries against the ones its outgoing attributes imply, and the entry's current classes come from the imported `objectClass` values. Without them, an update re-asserts classes the entry already has, and the directory refuses the modify. The attribute is read-only, so selecting it affects imports alone.

**Only some entries should carry a class?** Scope it with Synchronisation Rules. The class is only ever added where flows produce its attributes, so no second Connected System and no split container scoping is needed.

## Objects whose identity is an auxiliary class

Some populations are defined by an auxiliary class attached to a generic structural carrier, rather than by a structural class of their own. With **Include Auxiliary Classes** enabled on the **Settings** tab, those classes appear as Object Types in their own right and import as they always have.

To let JIM **create** them, the Object Type's sub-tab carries a **Provisioning** panel with one field: the **Structural Carrier Class**. Every entry in a directory carries exactly one structural class, so JIM has to be told what to write alongside the auxiliary one. On create it writes both: `objectClass: account` and `objectClass: posixAccount`, for example.

Until a carrier is named, JIM imports objects of the type and refuses to create them, and the panel says so.

## Automation

Every control above is available to the REST API and PowerShell:

| Task | REST | PowerShell |
|------|------|------------|
| List the classes on offer | `GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/object-types/{objectTypeId}/auxiliary-classes` | `Get-JIMConnectedSystemAuxiliaryClass` |
| Set which classes are merged | `PUT /api/v1/synchronisation/connected-systems/{connectedSystemId}/object-types/{objectTypeId}/auxiliary-classes` | `Set-JIMConnectedSystemAuxiliaryClass` |
| Set the Structural Carrier Class | `PUT /api/v1/synchronisation/connected-systems/{connectedSystemId}/object-types/{objectTypeId}/structural-carrier` | `Set-JIMConnectedSystemStructuralCarrierClass` |
| Start a discovery run | `POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/auxiliary-class-discovery` | `Start-JIMConnectedSystemAuxiliaryClassDiscovery` |
| Read the last discovery run | `GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/auxiliary-class-discovery` | `Get-JIMConnectedSystemAuxiliaryClassDiscovery` |

Setting the merged classes replaces the whole set, so read the current one first if you mean to add to it:

```powershell
$merged = (Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -MergedOnly).ObjectTypeId
Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId 1 -ObjectTypeId 5 -AuxiliaryClassObjectTypeId ($merged + 12)
```

See [PowerShell: Connected Systems](../powershell/connected-systems.md) for the full cmdlet reference.
