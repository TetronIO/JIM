---
title: Activities
---

# Activities

An Activity is a tracked operation in JIM. Every significant action -- run profile executions, schema imports, data generation, certificate management, configuration changes -- creates an activity record with status, timing, and summary statistics.

Activities are the primary mechanism for monitoring synchronisation progress and troubleshooting issues. Run profile activities additionally include detailed per-object execution items.

> Endpoint reference for this resource (full field tables, status enums, target types, query parameters) is in the [Scalar API reference](../index.md#where-to-find-what). This page covers the model at a conceptual level and the common workflows.

## Key Concepts

**Lifecycle.** Activities move through a small set of statuses: `InProgress`, then one of `Complete`, `CompleteWithWarning`, `CompleteWithError`, `FailedWithError`, or `Cancelled`. Most monitoring code only cares about whether the activity has reached a terminal status, and whether errors were recorded along the way.

**Initiated-by.** Every activity records who or what triggered it: a user (with metaverse object ID), an API key, or the system itself (e.g. a schedule). This is the audit trail.

**Summary statistics.** Activities for run profile executions carry counters relevant to the operation type: imports record `totalAdded` / `totalUpdated` / `totalDeleted`, sync stages add `totalProjected` / `totalJoined` / `totalAttributeFlows`, exports add `totalExported` / `totalDeprovisioned`, and so on. The exact field set depends on the operation; the Scalar reference documents the full schema.

**Execution items.** For run profile activities, JIM stores a per-object record of what happened (with any error details) for the most recent run. These let you drill from a high-level "5 errors" counter down to the specific connector space objects that failed.

**Parent and child activities.** A schedule execution typically appears as a parent activity with one child activity per step. Use the children listing to walk down a schedule's execution tree.

## Common Workflows

**Monitoring a run profile execution:**

1. Trigger the run profile -- the API returns an activity ID
2. Poll the activity to watch `status` and progress counters until it reaches a terminal status
3. If it finished with errors, retrieve the execution items to inspect the per-object failures

**Reviewing recent operations:**

1. List activities, filtered by name, target type, or initiator as needed
2. Retrieve the activities you're interested in for full detail
3. For schedule executions specifically, walk the child activities to see what each step did

## See also

- [Run Profiles](../run-profiles/index.md) -- the operations that produce most activities
- [Schedules](../schedules/index.md) -- the parent-and-child activity model originates here
- [PowerShell: Activities](../../powershell/activities.md) -- cmdlets that wrap these endpoints
