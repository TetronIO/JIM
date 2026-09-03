---
title: Run Profiles
---

# Run Profiles

A **Run Profile** defines a synchronisation operation that can be executed against a [Connected System](connected-systems.md). Each Run Profile specifies the type of operation (import, sync, or export), a batch size, and (where applicable) a target partition or file path.

Run Profiles are the building blocks of [schedules](schedules.md): each schedule step typically references a Run Profile to execute. Run Profiles can also be executed directly for one-off operations.

## Run types

A Run Profile is one of:

- **Full Import**<br /> Read every object from the Connected System and replace the existing connector space view.
- **Delta Import**<br /> Read only the objects that have changed since the last import. Faster, and only available where the connector supports change tracking.
- **Full Synchronisation**<br /> Evaluate every connector space object against the Synchronisation Rules; produce projections, joins, Attribute Flows, and Pending Exports.
- **Delta Synchronisation**<br /> Evaluate only objects with pending changes since the last sync. Faster.
- **Export**<br /> Flush Pending Exports out to the Connected System.

## Batch size

Controls how many objects are processed per batch during execution. Larger batches reduce overhead per object but cost more memory and increase the blast radius if a batch fails. Sensible defaults differ per connector; tune as needed for your scale.

## Partition and file path

For connectors that expose multiple partitions (for example LDAP) or that operate on files (the file connector), the Run Profile pins the operation to a specific scope. A connector can have several Run Profiles of the same run type, each scoped to a different partition or file.

A Run Profile that targets no partition reads from every partition currently selected on the Connected System, so it follows your selections automatically.

### When a targeted partition is deselected

Selecting a partition on the Connected System's Partitions & Containers tab is how you tell JIM which parts of a directory it manages, and that decision binds every Run Profile. If you deselect a partition that a Run Profile targets, that Run Profile becomes **inoperable**: JIM refuses to run it, naming the Run Profile and the partition, rather than reading scope you have withdrawn. The Run Profiles tab marks it **Not selected** beside the partition name, the REST API returns `targetsDeselectedPartition` on the Run Profile, and `Get-JIMRunProfile` surfaces the same property, so you can find every affected Run Profile before a scheduled run reaches one:

```powershell
Get-JIMRunProfile -ConnectedSystemId 1 | Where-Object targetsDeselectedPartition
```

To resolve it, either select the partition again, or edit the Run Profile to target a partition that is selected. Deleting the Run Profile is also valid where the partition has genuinely been retired.

!!! warning "Deselecting a partition or container changes what a Full Import considers deleted"

    A Full Import treats any object it does not find as deleted from the Connected System, which marks the corresponding Connected System Objects obsolete and, on the next synchronisation, disconnects them and recalls their contributed attribute values. Narrowing import scope therefore has consequences well beyond "JIM stops reading these objects". Review the scope change before saving it.

## Verification Mode (Full Import)

Full Import automatically skips loading and comparing objects whose content has not changed since the last import, using a stored content hash (see [how Full Import detects unchanged objects](../concepts/synchronisation-pipeline.md#how-full-import-detects-unchanged-objects)). This is transparent and needs no configuration.

**Verification Mode** is an optional toggle on a Full Import Run Profile that temporarily disables this optimisation: every object is fully compared regardless of its stored hash, and JIM reports an error if a stored hash matched but the comparison still found a change. Use it to validate the optimisation after an upgrade, or to investigate a suspected discrepancy; leave it off for everyday imports, since it forgoes the performance benefit. The toggle only applies to Full Import Run Profiles; enabling it on any other run type is rejected.

## Safeguards

An **Export** Run Profile can carry a limit on how many creates, updates and deletes a single run may attempt against the Connected System: **Max creates**, **Max updates** and **Max deletes**. Each is optional and independent; leave any of them blank for no limit, or set one to `0` to attempt none of that change type at all. The three limits are only valid on an Export Run Profile; setting one on any other run type is rejected.

When a run reaches a limit, JIM stops attempting further changes of that type and leaves the rest exactly where they were: still Pending, untouched, ready for the next Export run. Nothing else in the run is affected; other change types continue up to their own limits (or without one). The Activity for a capped run completes as **Complete with warning**, naming the limit reached and how many changes of that type remain pending, and the Activity's `exportCreatesWithheld` / `exportUpdatesWithheld` / `exportDeletesWithheld` counters record exactly how many were withheld (see [Activities](activities.md)). Resuming needs no action beyond running the Export Run Profile again: the withheld changes are picked up in the ordinary order.

To clear a limit, set it back to no value:

```powershell
Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 12 -MaxDeletes $null
```

**Recommended values:** set **Max deletes** to a small share of the target Connected System's population (for example, a few percent) on any Export Run Profile writing to a production directory; a broken import filter or an unintended Synchronisation Rule change can then only deprovision a bounded number of accounts before the run stops and warns you, rather than working through the whole directory. Leave **Max creates** and **Max updates** blank until a new Connected System's initial load has finished, since that first export is legitimately a mass create; consider capping them afterwards for the same reason as deletes.

## Asynchronous execution

Triggering a Run Profile returns an activity ID. The actual work runs on the worker process and is monitored via [activities](activities.md). For long-running runs, polling the activity gives you live progress counters; the per-object execution items let you drill into individual failures after the fact.

## Common workflows

**Setting up Run Profiles for a new Connected System:**

1. Create the Connected System and import its schema
2. Create the Run Profiles you need. Typically: a delta import, a delta sync, and an export. Add full variants too if you want the option of a periodic ground-truth refresh.
3. Either add them as steps to a [schedule](schedules.md) for automated execution, or run them on demand for one-off operations

**Running a one-off import:**

1. Find the right Run Profile for the Connected System
2. Execute the Run Profile and capture the returned activity ID
3. Watch the activity to monitor progress and inspect the result

## Manage Run Profiles

- **JIM portal**<br /> Run Profiles tab on a Connected System in the admin UI
- **PowerShell**<br /> [Run Profile cmdlets](../powershell/run-profiles.md) (`Get-JIMRunProfile`, `New-JIMRunProfile`, `Invoke-JIMRunProfile`, etc.)
- **REST API**<br /> Run Profile endpoints in the [interactive API reference](../../api/reference/)

## See also

- [Connected Systems](connected-systems.md) -- Run Profiles belong to a Connected System
- [Schedules](schedules.md) -- automated execution of Run Profiles
- [Activities](activities.md) -- monitoring Run Profile execution
- [Concepts: Synchronisation Pipeline](../concepts/synchronisation-pipeline.md) -- what each run type does in detail
