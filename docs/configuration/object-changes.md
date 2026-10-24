---
title: Object Changes
description: "Search recorded changes to every Metaverse Object by time, Metaverse Object Type, name and how an attribute changed."
type: object
audience: administrator
---

# Object Changes

JIM records each change it makes to a Metaverse Object: which attributes changed, the values added and removed, and the Synchronisation Rule, user or API key that made the change. A Metaverse Object's own **Changes** tab shows that history for one object. The **Object Changes** page (Activity, then **Object Changes**) searches it across every object, to answer questions such as "who was disabled this month?" or "who moved out of Finance last week?" without knowing which objects to open.

## What you can search by

| Filter | What it matches |
|---|---|
| **Changed between** | Changes made from the start of the first day to the end of the last, in your local time |
| **Metaverse Object Type** | Changes to objects of that type, including objects since deleted |
| Search box | Changes to objects whose display name contains the text; not case-sensitive |
| Attribute conditions | Changes to the named attribute, optionally to or from a value (below) |

## Attribute conditions

Select **Add Condition**, choose an **Attribute**, then a **Condition**:

- **changed to** matches a value being added. For a single-valued attribute, that is the attribute changing to it.
- **changed from** matches a value being removed: the attribute changing away from it.
- **changed (to or from)** matches either.

Leave **Value** empty to match any value. A value must be of the attribute's data type:

| Attribute type | How a value matches |
|---|---|
| Text | The whole value, not case-sensitive. `*` matches any run of characters, so `Fin*` matches Finance and Financial Control. |
| Reference | The referenced Metaverse Object's display name, matched like text. |
| Number, Long Number, Decimal | The number. |
| Date and time | `yyyy-MM-dd` matches the whole of that day (UTC); a date and time matches that second. |
| Boolean | `true` or `false`. |
| GUID | The GUID. |
| Binary | No value: Binary values are not recorded, so only **changed (to or from)** with an empty value matches. |

A value that is not of the attribute's type is refused with a message saying why, rather than matching nothing.

Changing Employee Status from Active to Disabled records Disabled as added and Active as removed. So **Employee Status changed to Disabled** finds the account being disabled, and **Employee Status changed from Disabled** finds it being re-enabled later.

### More than one condition

With two or more conditions, choose how they combine:

- **All conditions**: every condition is met by the same change, to one Metaverse Object in one Activity. **Employee Status changed to Disabled** with **Department changed to Leavers** finds leavers processed in one synchronisation, not someone disabled in March who moved department in June.
- **Any condition**: at least one condition is met.

A search can combine up to ten conditions.

## Reading the results

Each row is one change to one Metaverse Object, newest first:

- **Metaverse Object**: the object, linked to its page. An object since deleted is shown dimmed, with a dashed outline and a bin icon, and is named as it was when it was deleted; hover over it to see when. It has no page to open; its full history is on the [Deleted Objects](deleted-objects.md) page.
- **Change**: what kind of change it was, such as Attribute Flow, Created or Deleted.
- **Attributes**: the attributes the change touched, with the attributes your conditions named first. A single-valued attribute shows its old value struck through and its new value; a multi-valued one shows values added and removed. A deletion is labelled **Final values** and shows the values the object held when it was deleted.
- **Made by**: the Synchronisation Rule, or the user or API key, that made the change. A change JIM made itself, such as creating the initial administrator at first sign-in, shows **System**.
- **Activity**: a link to the [Activity](activities.md) the change was made in: a synchronisation, a scheduled deletion, a sign-in or data generation. A change has no link once history retention has removed its Activity.

Select a row to open the change in a panel beside the list: every attribute it changed, with old and new values in full, who made it and a link to its Activity. For an object that still exists, **Open Metaverse Object** goes to the object. Selecting another row shows that change instead; close the panel with its close button or Esc.

The filters are kept in the page's address, so a search can be bookmarked or shared.

## What cannot be found

- **Changes made while change tracking was off.** The **Track MVO changes** <!-- conformance: allow LANG-4 "the label the portal shows" --> Service Setting decides whether JIM records Metaverse Object changes at all; it is on by default. See [Service Settings](../administration/configuration.md#service-settings).
- **Changes older than the retention period.** The **History retention period** Service Setting (`History.RetentionPeriod`, 90 days by default) applies, and the nightly **History Retention Cleanup** Schedule removes older changes.
- **Changes to Connected System Objects.** The page searches the Metaverse. A Connected System Object's changes are on its own page in the [Connector Space](connected-systems.md#the-connector-space).

## Who can use it

Anyone who can sign in to the portal can open the page, as they can a Metaverse Object's **Changes** tab. The REST API that serves the same search requires the Administrator role.

## Search from a script

`Search-JIMMetaverseObjectChange` runs the same search; see [History](../powershell/history.md#search-jimmetaverseobjectchange). The REST API serves it from `POST /api/v1/history/metaverse-object-changes/search`.

## Related

- [Metaverse Objects](metaverse-objects.md): one object's own change history
- [Deleted Objects](deleted-objects.md): the record JIM keeps of each deleted object
- [Activities](activities.md): the record of each run that made the changes
