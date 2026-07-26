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

## Verification Mode (Full Import)

Full Import automatically skips loading and comparing objects whose content has not changed since the last import, using a stored content hash (see [how Full Import detects unchanged objects](../concepts/synchronisation-pipeline.md#how-full-import-detects-unchanged-objects)). This is transparent and needs no configuration.

**Verification Mode** is an optional toggle on a Full Import Run Profile that temporarily disables this optimisation: every object is fully compared regardless of its stored hash, and JIM reports an error if a stored hash matched but the comparison still found a change. Use it to validate the optimisation after an upgrade, or to investigate a suspected discrepancy; leave it off for everyday imports, since it forgoes the performance benefit. The toggle only applies to Full Import Run Profiles; enabling it on any other run type is rejected.

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
