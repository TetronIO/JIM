---
title: Activities
---

# Activities

An **activity** is a tracked operation in JIM. Every significant action creates an activity record with status, timing, and summary statistics: Run Profile executions, schema imports, data generation, certificate management, and configuration changes all produce activities.

Activities are the primary mechanism for monitoring synchronisation progress and troubleshooting issues. Run Profile activities additionally include detailed per-object execution items, so you can drill from a high-level "5 errors" counter down to the specific objects that failed.

## Lifecycle

Activities move through a small set of statuses:

- **In progress**<br /> Currently executing.
- **Complete**<br /> Finished successfully.
- **Complete with warning**<br /> Finished with non-fatal warnings.
- **Complete with error**<br /> Finished, but some individual objects had errors.
- **Failed with error**<br /> Failed due to a critical error before or during execution.
- **Cancelled**<br /> Cancelled before completion.

Most monitoring code only cares about whether the activity has reached a terminal status, and whether errors were recorded along the way.

## Initiated by

Every activity records who or what triggered it: a user (with their Metaverse Object reference), an API key, or the system itself (for example, a schedule). This is the audit trail.

## Summary statistics

Activities for Run Profile executions carry counters relevant to the operation type:

- **Imports**<br /> `Total Added`, `Total Updated`, `Total Deleted`.
- **Synchronisation**<br /> `Total Projected`, `Total Joined`, `Total Attribute Flows`, `Total Disconnected`, `Total Provisioned`.
- **Exports**<br /> `Total Exported`, `Total Deprovisioned`, `Total Pending Exports`.
- **All operations**<br /> `Total Errors`, `Total Activity Time`, `Execution Time`.

The exact field set depends on the operation; the [interactive API reference](../../api/reference/) documents the full schema.

## Execution items

For Run Profile activities, JIM stores a per-object record of what happened (with any error details) for the most recent run. These let you go from a high-level error counter to the specific Connected System Objects that failed and the reason for each failure. Execution items are the right place to look when diagnosing why a particular identity didn't sync as expected.

## Parent and child activities

A schedule execution typically appears as a parent activity with one child activity per step. Use the children listing to walk down a schedule's execution tree from the top-level run into the individual operations it triggered.

## Common workflows

**Monitoring a Run Profile execution:**

1. Trigger the Run Profile; capture the returned activity ID
2. Poll the activity to watch its status and progress counters until it reaches a terminal status
3. If it finished with errors, retrieve the execution items to inspect the per-object failures

**Reviewing recent operations:**

1. List activities, filtered by name, target type, or initiator as needed
2. Retrieve the activities you're interested in for full detail
3. For schedule executions specifically, walk the child activities to see what each step did

## Manage Activities

- **JIM portal**<br /> Activities area of the admin UI
- **PowerShell**<br /> [Activities cmdlets](../powershell/activities.md) (`Get-JIMActivity`, etc.)
- **REST API**<br /> Activities endpoints in the [interactive API reference](../../api/reference/)

## See also

- [Run Profiles](run-profiles.md) -- the operations that produce most activities
- [Schedules](schedules.md) -- the parent-and-child activity model originates here
