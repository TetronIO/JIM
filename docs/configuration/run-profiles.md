---
title: Run Profiles
---

# Run Profiles

A **run profile** defines a synchronisation operation that can be executed against a [connected system](connected-systems.md). Each run profile specifies the type of operation (import, sync, or export), a batch size, and (where applicable) a target partition or file path.

Run profiles are the building blocks of [schedules](schedules.md): each schedule step typically references a run profile to execute. Run profiles can also be executed directly for one-off operations.

## Run types

A run profile is one of:

- **Full Import**<br /> Read every object from the connected system and replace the existing connector space view.
- **Delta Import**<br /> Read only the objects that have changed since the last import. Faster, and only available where the connector supports change tracking.
- **Full Synchronisation**<br /> Evaluate every connector space object against the synchronisation rules; produce projections, joins, attribute flows, and pending exports.
- **Delta Synchronisation**<br /> Evaluate only objects with pending changes since the last sync. Faster.
- **Export**<br /> Flush pending exports out to the connected system.

## Batch size

Controls how many objects are processed per batch during execution. Larger batches reduce overhead per object but cost more memory and increase the blast radius if a batch fails. Sensible defaults differ per connector; tune as needed for your scale.

## Partition and file path

For connectors that expose multiple partitions (for example LDAP) or that operate on files (the file connector), the run profile pins the operation to a specific scope. A connector can have several run profiles of the same run type, each scoped to a different partition or file.

## Asynchronous execution

Triggering a run profile returns an activity ID. The actual work runs on the worker process and is monitored via [activities](activities.md). For long-running runs, polling the activity gives you live progress counters; the per-object execution items let you drill into individual failures after the fact.

## Common workflows

**Setting up run profiles for a new connected system:**

1. Create the connected system and import its schema
2. Create the run profiles you need. Typically: a delta import, a delta sync, and an export. Add full variants too if you want the option of a periodic ground-truth refresh.
3. Either add them as steps to a [schedule](schedules.md) for automated execution, or run them on demand for one-off operations

**Running a one-off import:**

1. Find the right run profile for the connected system
2. Execute the run profile and capture the returned activity ID
3. Watch the activity to monitor progress and inspect the result

## Manage Run Profiles

- **JIM portal**<br /> Run Profiles tab on a connected system in the admin UI
- **PowerShell**<br /> [Run Profile cmdlets](../powershell/run-profiles.md) (`Get-JIMRunProfile`, `New-JIMRunProfile`, `Invoke-JIMRunProfile`, etc.)
- **REST API**<br /> Run Profile endpoints in the [interactive API reference](../api/index.md)

## See also

- [Connected Systems](connected-systems.md) -- run profiles belong to a connected system
- [Schedules](schedules.md) -- automated execution of run profiles
- [Activities](activities.md) -- monitoring run profile execution
- [Concepts: Synchronisation Pipeline](../concepts/synchronisation-pipeline.md) -- what each run type does in detail
