---
title: Run Profiles
description: What each run type does, and the settings that scope a run, verify an import and stop a run becoming a mass change.
type: object
audience: administrator
validated: v0.16.0
---

# Run Profiles

A **Run Profile** defines a synchronisation operation that can be executed against a [Connected System](connected-systems.md). Each Run Profile specifies the type of operation (import, sync, or export), a batch size, and (where applicable) a target partition or file path.

```jim-diagram
archetype: comparison
title: Full and Delta runs compared
desc: "A Full Import reads every object from the Connected System, and an object it no longer returns is deleted; a Delta Import reads only the changes since the last import, and needs a connector that tracks changes. A Full Synchronisation evaluates every Connected System Object, and can be previewed before it runs; a Delta Synchronisation evaluates only objects with pending changes. Export flushes the Pending Exports either way: it has no Full or Delta form."
columns:
- {label: Full, sub: 'every object, every time'}
- {label: Delta, sub: 'only what changed, faster'}
rows:
- label: Import
  cells:
  - {text: reads every object, sub: one no longer returned is deleted, meter: 12}
  - {text: reads changes since the last import, sub: needs a connector that tracks changes, meter: 3}
- label: Synchronisation
  cells:
  - {text: evaluates every Connected System Object, sub: previewable before it runs, meter: 12}
  - {text: evaluates objects with pending changes, meter: 3}
footer: 'Export flushes the Pending Exports either way: it has no Full or Delta form'
```

Run Profiles are the building blocks of [Schedules](schedules.md): each step of a Schedule runs a Run Profile. A Run Profile can also be run directly, for a one-off operation.

## :material-play-circle-outline: Run types

A Run Profile is one of:

- **Full Import**<br /> Read every object from the Connected System into its Connector Space; an object it no longer returns is treated as deleted.
- **Delta Import**<br /> Read only the objects that have changed since the last import. Faster, and only available where the connector supports change tracking.
- **Full Synchronisation**<br /> Evaluate every Connected System Object against the Synchronisation Rules, whether or not it has changed; produce projections, joins, Attribute Flows, and Pending Exports. Its preview action shows what it would do without running it; see [Previewing a Full Synchronisation](connected-systems.md#previewing-a-full-synchronisation).
- **Delta Synchronisation**<br /> Evaluate only objects with pending changes since the last sync. Faster.
- **Export**<br /> Flush Pending Exports out to the Connected System.

A synchronisation works through the Connected System's Object Types in dependency order, so people come before the groups they belong to, and membership resolves as each group is processed. Where the schema does not name an attribute's target type (LDAP `member` and `manager`), a Full Synchronisation reads which types its values point at; a Delta Synchronisation assumes any type. References within one type (managers, nested groups) can still need the **Resolving cross-page references** step. A reference to an object no Synchronisation Rule joins (a contact, a member out of scope) does not flow; the rest of the membership does. The service log records the order in a line starting `Full Synchronisation type order:` or `Delta Synchronisation type order:`.

## :material-file-document-outline: Page size

How many objects an import asks the Connected System for at a time: the page of an LDAP paged search, a SQL query or a SCIM request. The default is 100. A larger page means fewer round trips to the Connected System and more memory held at once. The File Connector reads its file whole, so the setting does nothing there, and synchronisation is paged by the `Sync.PageSize` [Service Setting](../administration/configuration.md#service-settings) instead.

## :material-folder-outline: Partition and file path

For a Connector that exposes partitions (the LDAP Connector) or reads files (the File Connector), the Run Profile pins the operation to one partition or file. A Connected System can have several Run Profiles of the same run type, each scoped to a different partition or file.

A Run Profile that targets no partition reads from every partition currently selected on the Connected System, so it follows your selections automatically.

### When a targeted partition is deselected

Selecting a partition on the Connected System's Scope tab is how you tell JIM which parts of a directory it manages, and that decision binds every Run Profile. If you deselect a partition that a Run Profile targets, that Run Profile becomes **inoperable**: JIM refuses to run it, naming the Run Profile and the partition, rather than reading scope you have withdrawn. The Run Profiles tab marks it **Not selected** beside the partition name, the REST API returns `targetsDeselectedPartition` on the Run Profile, and `Get-JIMRunProfile` surfaces the same property, so you can find every affected Run Profile before a scheduled run reaches one:

```powershell
Get-JIMRunProfile -ConnectedSystemId 1 | Where-Object targetsDeselectedPartition
```

To resolve it, either select the partition again, or edit the Run Profile to target a partition that is selected. Deleting the Run Profile is also valid where the partition has genuinely been retired.

!!! warning "Deselecting a partition or container changes what a Full Import considers deleted"

    A Full Import treats any object it does not find as deleted from the Connected System, which marks the corresponding Connected System Objects obsolete and, on the next synchronisation, disconnects them and recalls their contributed attribute values. Narrowing import scope therefore has consequences well beyond "JIM stops reading these objects". Review the scope change before saving it.

## :material-eye-check-outline: Verification Mode (Full Import)

Full Import automatically skips loading and comparing objects whose content has not changed since the last import, using a stored content hash (see [how Full Import detects unchanged objects](../concepts/synchronisation-pipeline.md#how-full-import-detects-unchanged-objects)). This is transparent and needs no configuration.

**Verification Mode** is an optional toggle on a Full Import Run Profile that temporarily disables this optimisation: every object is fully compared regardless of its stored hash, and JIM reports an error if a stored hash matched but the comparison still found a change. Use it to validate the optimisation after an upgrade, or to investigate a suspected discrepancy; leave it off for everyday imports, since it forgoes the performance benefit. The toggle only applies to Full Import Run Profiles; enabling it on any other run type is rejected.

## :material-swap-horizontal: Allow Object Type Changes (Full Import)

JIM recognises a Connected System Object by its External ID within one Object Type. When an object JIM already holds starts arriving under a different Object Type (an Object Type selection change, a schema refresh, or an edit in the directory), the import does not treat it as a new object: it **refuses the change**. The object's Run Profile execution item records an **Object Type Changed** error naming both Object Types, nothing is created, and the existing Connected System Object is kept exactly as it is, including through deletion detection, so nothing is disconnected or deprovisioned (unless an earlier import had already marked it obsolete, in which case the next synchronisation disconnects it as it would have anyway). The Activity completes with a warning, even when the refused objects are all it recorded. This applies to Full and Delta Imports alike, for every Connector that can tell an Object Type change from a new object: of the built-in Connectors, the [LDAP Connector](../connectors/jim-ldap-connector.md), whose External IDs are unique across the whole directory.

**Allow Object Type Changes** is how you tell JIM a change is intended. On a Full Import Run Profile that allows it, JIM imports each such object as a new Connected System Object of its new type, and deletion detection retires the old one: it is marked obsolete, and the next synchronisation disconnects it from its Metaverse Object under the usual [Deletion Rules](metaverse.md#deletion-behaviour). Grace periods apply to that disconnection as to any other. [Deletion limits](#full-import) do not hold the retirement back: the import returned the object, under its new type, so it is not a missing object, and holding the old one back would leave two Connected System Objects for one directory object. A replacement is only ever created alongside the retirement of what it replaces: where the old object cannot be retired (it is in a partition this Run Profile does not import, or its Object Type is deselected with an enabled Synchronisation Rule still bound to it), the new one is not created and the object is reported as an Object Type Changed error. The setting is only valid on a Full Import Run Profile, because a Full Import is the run that returns every object and so the only one whose deletion detection can retire the old ones; enabling it on any other run type is rejected. For Connectors whose External IDs are only unique within an Object Type (SQL, File, SCIM 2.0 Client), an object under another Object Type is a different object, and the setting has no effect.

Leave it off on the Run Profiles your Schedules run. As with a [mass change past a limit](#letting-a-legitimate-mass-change-through), the safer pattern is a **second Full Import Run Profile with the setting on, run by hand** once you have prepared for the change. [When an object changes Object Type](connected-system-schema.md#when-an-object-changes-object-type) explains what causes a change and the order to do things in.

## :material-shield-lock-outline: Safeguards

### Export

An **Export** Run Profile can carry a limit on how many creates, updates and deletes a single run may attempt against the Connected System: **Max creates**, **Max updates** and **Max deletes**. Each is optional and independent; leave any of them blank for no limit, or set one to `0` to refuse that change type outright. The three limits are only valid on an Export Run Profile; setting one on any other run type is rejected.

**A run that would exceed a limit attempts none of that change type.** JIM counts how many of each change type are pending at the start of the run; if a type's count is more than its limit, JIM attempts none of that type at all this run and leaves every one of them exactly where they were: still Pending, untouched. There is no partial attempt: JIM never processes up to the limit and stops partway. Other change types are unaffected and continue normally, whether they carry their own limit or none. The Activity for a run that withheld anything completes as **Complete with warning**, naming the limit, how many were pending, and what to do next, and the Activity's `exportCreatesWithheld` / `exportUpdatesWithheld` / `exportDeletesWithheld` counters record exactly how many were withheld (see [Activities](activities.md)). Resuming needs no action beyond raising or clearing the limit, or reducing what is pending, then running the Export Run Profile again.

To clear a limit, set it back to no value:

```powershell
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 12 -MaxDeletes $null
```

**Recommended values:** set **Max deletes** to a small share of the target Connected System's population (for example, a few percent) on any Export Run Profile writing to a production directory; a broken import filter or an unintended Synchronisation Rule change can then withhold the whole deprovisioning attempt and warn you, rather than working through the whole directory. Leave **Max creates** and **Max updates** blank until a new Connected System's initial load has finished, since that first export is legitimately a mass create; consider capping them afterwards for the same reason as deletes.

### Letting a legitimate mass change through

A limit exists to stop a run nobody meant to be this big; it is not meant to be raised every time a genuine bulk operation comes along. If you know in advance that a run will legitimately exceed a limit (a planned bulk deprovisioning, a large onboarding batch), run a **second Export Run Profile against the same Connected System with no limit set**, and trigger it by hand rather than adding it to a Schedule. This gets the large, deliberate change through without touching the limited Run Profile that protects your regular scheduled runs. The same applies to an import's deletion limits: a second **Full Import** or **Delta Import** Run Profile with no deletion limit, run by hand, lets a deliberate bulk deletion through. For a Delta Import this is also how you let through deletions a limited run has withheld (see [Delta Import](#delta-import)).

Raising or clearing the limited Run Profile's own limit works too, but is the weaker option: it is easy to forget to put the limit back afterwards, and until you do, the safeguard is gone for every run that Run Profile makes, scheduled or not. A second, unlimited Run Profile kept out of Schedules cannot be forgotten in the same way, because it is never run except when you choose to run it.

### Full Import

A **Full Import** Run Profile can carry a limit on how many Connected System Objects a single run's deletion detection may newly mark as deleted: **Max detected deletions** (a count) and **Max detected deletions percent** (a share, 0 to 100, of the Connected System Objects in the run's scope when it starts, not counting those already marked as deleted). Each is optional and independent; leave either blank for no limit, or set one to `0` to refuse to mark anything as deleted. Both are valid on a Full Import Run Profile and on a [Delta Import](#delta-import) Run Profile; setting either on any other run type is rejected.

**A run that would exceed either limit marks nothing as deleted.** JIM works out which Connected System Objects would newly be marked as deleted (excluding objects this run already saw, including those it saw under another Object Type and [refused to change](#allow-object-type-changes-full-import), objects retired by an Object Type change the Run Profile allows, and objects already marked from an earlier run); if that count is more than Max detected deletions, or more than Max detected deletions percent of the Connected System Objects in scope that are not already marked as deleted, JIM marks none of them: every one stays exactly as it was. Objects the import did see are still created and updated as normal; only the marking of missing objects as deleted is withheld. There is no partial marking. The Activity for a refused detection completes as **Complete with warning**, naming the limit, how many objects were found missing and what share that is, and what to do next, and the Activity's `detectedDeletionsWithheld` counter records exactly how many were withheld (see [Activities](activities.md)). Resuming needs no action beyond raising or clearing the limit, or fixing the Connected System's scope or the connector's filters, then running the Full Import again.

A refused detection also means the run does not count as a successful Full Import for the [post-clear reconciliation gate](connector-space.md#clearing-the-connector-space): the Connected System's last successful Full Import time is not stamped, so the stranded-value sweep stays shut until a Full Import passes.

**Recommended values:** set **Max detected deletions percent** to a small share (for example, 5 to 10%) on a Full Import Run Profile against a large Connected System; a broken filter or base DN dropping a plausible-looking fraction of the population is easier to catch as a share than as a raw count. For a small Connected System, where a single genuine departure is already a large share of it, prefer **Max detected deletions** as a count instead: a percentage limit trips (or fails to trip) on rounding rather than on anything meaningful once the population is only a handful of objects.

### Delta Import

A Delta Import does not look for missing objects; it marks as deleted the objects the Connected System reports deleted since the last import. The same two limits, **Max detected deletions** and **Max detected deletions percent**, guard those deletions on a **Delta Import** Run Profile. They count the Connected System Objects the run would newly mark as deleted across every page it reads. A deletion the Connected System reports again for an object already marked, a deletion of an object JIM does not hold, and an object deleted and then re-added within the same run are not counted. The percentage is measured against the Connected System Objects in the run's scope that are not already marked as deleted, before it saves anything.

**A run that would exceed either limit marks nothing as deleted.** Every object it would have marked stays exactly as it was. Objects the run created and updated are still applied as normal. The Activity completes as **Complete with warning**, naming the limit, how many deletions were reported and what share that is, and its `detectedDeletionsWithheld` counter records how many were withheld (see [Activities](activities.md)).

Unlike a Full Import, a Delta Import only reads each change once, so a withheld deletion must not be lost. A run that withholds its deletions keeps the change-tracking position it started from, and each following Delta Import reads the same deletions again and withholds them again, with the same warning, until you act on it:

- If the deletions are a mistake, fix them in the Connected System (restore the deleted objects). The next Delta Import then reads the restored objects too.
- If the deletions are intended, raise the limit, or run a second Delta Import Run Profile without these limits by hand (see [Letting a legitimate mass change through](#letting-a-legitimate-mass-change-through)). It reads the same changes, because the change-tracking position belongs to the Connected System, not the Run Profile.
- Alternatively, run a Full Import: its own deletion limits then apply.

Each Delta Import re-reads everything since that position until the deletions are let through or fixed, so act on the warning promptly.

**Recommended values:** set a limit on a Delta Import Run Profile that runs on a frequent Schedule against a Connected System that can delete objects in bulk, such as a directory where a whole OU can be deleted by mistake. A count of what a normal interval's leavers look like, with headroom, is usually easier to reason about than a percentage.

## :material-timer-sand: Asynchronous execution

Starting a Run Profile returns an Activity ID straight away. The work runs on the Worker, and its [Activity](activities.md) shows the run's progress as it goes, in the portal or with `Get-JIMActivity -Follow`; afterwards, its execution items give each object's outcome.

## :material-playlist-check: Common workflows

**Setting up Run Profiles for a new Connected System:**

1. Create the Connected System and import its schema
2. Create the Run Profiles you need: a Full Import, a Full Synchronisation and an Export at minimum, since the first run has to look at everything rather than only what changes. Add delta variants for ongoing operation, and keep the full variants for periodic ground-truth refreshes.
3. Use the full variants to bring the Connected System in, in the order [Initialising JIM](../concepts/synchronisation-pipeline.md#initialising-jim) sets out, before switching to delta; then either add the delta Run Profiles as steps to a [schedule](schedules.md) for automated execution, or run them on demand for one-off operations

**Running a one-off import:**

1. Find the right Run Profile for the Connected System
2. Run it, and note the Activity ID it returns
3. Watch the Activity to follow its progress and inspect the result

## :material-cog-outline: Manage Run Profiles

- **JIM portal**<br /> Run Profiles tab on a Connected System in the admin UI
- **PowerShell**<br /> [Run Profile cmdlets](../powershell/run-profiles.md) (`Get-JIMRunProfile`, `New-JIMRunProfile`, `Start-JIMRunProfile`, etc.)
- **REST API**<br /> Run Profile endpoints in the [interactive API reference](../../api/reference/)

## :material-book-open-variant-outline: Related

- [Connected Systems](connected-systems.md): Run Profiles belong to a Connected System
- [Schedules](schedules.md): automated execution of Run Profiles
- [Activities](activities.md): monitoring Run Profile execution
- [Concepts: Synchronisation Pipeline](../concepts/synchronisation-pipeline.md): what each run type does in detail
