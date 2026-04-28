---
title: Run Profiles
---

# Run Profiles

A Run Profile defines a synchronisation operation that can be executed against a connected system. Each run profile specifies the type of operation (import, sync, or export), a batch size, and optionally a target partition or file path.

Run profiles are the building blocks of [schedules](../schedules/index.md): each schedule step typically references a run profile to execute. They can also be executed directly via the API for one-off operations.

> Endpoint reference for this resource is in the [interactive API reference](../index.md#where-to-find-what). This page covers the model and the workflows.

## Key Concepts

**Run types.** A run profile is one of:

- **Full Import** -- read every object from the connected system and replace the existing connector space view
- **Delta Import** -- read only the objects that have changed since the last import (faster; only available where the connector supports change tracking)
- **Full Synchronisation** -- evaluate every connector space object against the synchronisation rules; produce projections, joins, attribute flows, and pending exports
- **Delta Synchronisation** -- evaluate only objects with pending changes since the last sync (faster)
- **Export** -- flush pending exports out to the connected system

**Batch size.** Controls how many objects are processed per batch during execution. Larger batches reduce overhead per object but cost more memory and increase failure blast radius. Sensible defaults differ per connector; tune as needed.

**Partition / file path.** For connectors that expose multiple partitions (LDAP) or that operate on files (the file connector), the run profile pins the operation to a specific scope.

**Asynchronous execution.** Triggering a run profile returns an activity ID. The actual work runs on the worker and is monitored via [Activities](../activities/index.md).

## Common Workflows

**Setting up run profiles for a new connected system:**

1. Create the connected system and import its schema
2. Create the run profiles you need (typically: a delta import, a delta sync, and an export; full variants too if you want a periodic ground-truth refresh)
3. Either add them as steps to a [schedule](../schedules/index.md) for automated execution, or call execute directly for one-off runs

**Running a one-off import:**

1. List run profiles for the connected system to find the right one
2. Execute the run profile -- the API returns an activity ID
3. Poll Activities to monitor progress and retrieve the result

## See also

- [Connected Systems](../connected-systems/index.md) -- run profiles belong to a connected system
- [Schedules](../schedules/index.md) -- automated execution of run profiles
- [Activities](../activities/index.md) -- monitoring run profile execution
- [Concepts: Synchronisation Pipeline](../../concepts/synchronisation-pipeline.md) -- what each run type does
- [PowerShell: Run Profiles](../../powershell/run-profiles.md) -- cmdlets that wrap these endpoints
