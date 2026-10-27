---
title: Deleted Objects
description: "Find a Connected System Object or Metaverse Object JIM deleted, see who or what deleted it and when, and read its history up to the deletion."
type: object
audience: administrator
---

# Deleted Objects

When JIM deletes a Connected System Object or a Metaverse Object, it keeps a deletion record: the object's last values, when it went, and who or what removed it. The **Deleted Objects** page (Administration, then **Deleted Objects**) is where you find those records, to answer "where did this account go?" long after the object itself has gone. A record is for audit; JIM cannot bring a deleted object back.

## :material-file-tree-outline: What a deletion record keeps

| | Connected System Object | Metaverse Object |
|---|---|---|
| Identity | External ID, display name, Object Type and Connected System | Display name and Object Type |
| When | The time it was deleted | The time it was deleted |
| Who | The user or API key that started the deletion, from its Activity, and a link to the run's execution item | The user or API key that started the deletion |
| Values | Every attribute value it held, recorded as removed | Every attribute value it held, recorded as removed, with the Synchronisation Rule that contributed each one |

A Metaverse Object deleted by housekeeping after a [grace period](metaverse.md#grace-period) names whoever started the deletion that scheduled it, not housekeeping.

The record keeps the object's earlier change history too. Opening a record shows every change JIM recorded for that object, up to and including its deletion.

## :material-delete-clock-outline: When no record is kept

- **Change tracking is off.** The Service Settings **Track CSO changes** <!-- conformance: allow LANG-4 "the label the portal shows" --> and **Track MVO changes** <!-- conformance: allow LANG-4 "the label the portal shows" --> decide whether JIM records changes, deletions included, for each kind of object. Both are on by default. With one off, JIM deletes that kind of object without a record. See [Service Settings](../administration/configuration.md#service-settings).
- **The deletion came before tracking was on.** Nothing was recorded at the time, so there is nothing to find.
- **The change history went with the objects.** [Clearing a Connector Space](connector-space.md#clearing-the-connector-space) deletes its Connected System Objects' change history, deletion records included, unless you choose to keep it. Deleting a Connected System with **Delete change history for deleted objects** ticked does the same (over the REST API, `deleteChangeHistory=true`); left unticked, the default, the history stays. See [Removing a Connected System](removing-a-connected-system.md).

## :material-timer-outline: How long records are kept

Deletion records are change history, so the **History retention period** Service Setting (`History.RetentionPeriod`, 90 days by default) applies to them. The built-in **History Retention Cleanup** Schedule removes change history older than that each night, and `Invoke-JIMHistoryCleanup` runs the same clean-up on demand. Each change ages out on its own date, so the oldest changes before a deletion can go before the deletion record itself does.

## :material-magnify: Find a deleted object

The page has a tab for each kind of object: **Deleted Connected System Objects** and **Deleted Metaverse Objects**.

1. Choose the tab, then narrow the list with its filters: **Connected System** or **Object Type**, and a **From Date** and **To Date**. **To Date** includes the whole of that day.
2. Select **Search**. The filters apply only when you do.
3. Type in the search box to narrow the results as you type: **Search External ID** on the Connected System Object tab, **Search Display Name** on the Metaverse Object tab. Neither is case-sensitive.
4. Select **View**, or the row, to open the object's change history, ending with its deletion.

Both lists show the most recent deletions first and load more as you scroll. The Metaverse Object tab also shows who deleted each object, in **Deleted By**.

A [Causality](causality.md) view that reaches a deleted object links straight to its record here.

## :material-account-lock-outline: Who can use it

Only administrators can open the page, and the REST API that serves it requires the Administrator role too.

## :material-console: Read records from a script

`Get-JIMDeletedObject` returns the same records, filtered the same way; see [History](../powershell/history.md#get-jimdeletedobject). The REST API serves them from `GET /api/v1/history/deleted-objects/cso` and `GET /api/v1/history/deleted-objects/mvo`.

## :material-book-open-variant-outline: Related

- [Metaverse](metaverse.md#deletion-behaviour): when JIM deletes a Metaverse Object
- [Deprovisioning](deprovisioning.md): when JIM deletes an object in a Connected System
- [Activities](activities.md): the record of each run that deleted objects
