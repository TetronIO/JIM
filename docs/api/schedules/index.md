---
title: Schedules
---

# Schedules

A Schedule defines an automated sequence of operations that JIM executes on a trigger (cron-based or manual). Each schedule contains ordered steps that can run sequentially or in parallel, supporting [run profile](../run-profiles/index.md) execution, PowerShell scripts, external executables, and SQL scripts.

Schedules are the primary mechanism for automating identity synchronisation workflows -- e.g. a nightly schedule that imports from each connected system, runs synchronisation, and exports the results.

> Endpoint reference for this resource (cron pattern fields, step type fields, status enums, full request and response shapes) is in the [interactive API reference](../index.md#where-to-find-what). This page covers the model and the common workflows.

## Key Concepts

**Trigger.** A schedule is either `Cron` (runs automatically on a recurring pattern) or `Manual` (runs only when explicitly triggered).

**Pattern types.** Cron schedules support three authoring modes:

- **Specific times** -- pick days and times of day (e.g. weekdays at 06:00 and 18:00). JIM derives the cron expression from your selection.
- **Interval** -- run every N minutes or hours within a daily window (e.g. every 15 minutes between 08:00 and 18:00 on weekdays)
- **Custom** -- supply a raw cron expression for full control

**Steps.** Each step has an execution mode (`Sequential` or `ParallelWithPrevious`) and a step type:

- **RunProfile** -- execute a connected system run profile
- **PowerShell** -- run a PowerShell script with arguments
- **Executable** -- run an external executable
- **SqlScript** -- run a SQL script against a configured connection

A `stepIndex` orders steps; multiple steps with the same index run in parallel.

**Continue-on-failure.** Set per step. By default, a failing step halts the schedule; turn this on for steps where downstream work should proceed regardless.

**Executions.** Each schedule run produces a schedule execution record with per-step progress. Active and historical executions can be listed, retrieved, and (for active ones) cancelled.

**Enabled flag.** Disabled schedules don't fire on their cron trigger and don't appear as eligible for manual run; useful for temporarily pausing a schedule during maintenance without losing its definition.

## Common Workflows

**Setting up an automated nightly sync:**

1. Create a schedule with a cron trigger, specific-times pattern, and `isEnabled = true`
2. Add ordered steps for each operation: imports, then syncs, then exports (typically sequential)
3. Verify by triggering a manual run before the first scheduled fire
4. Monitor the resulting execution to confirm each step completes as expected

**Running an ad-hoc sync via a manual schedule:**

1. Create a schedule with trigger `Manual`
2. Add the steps for the operations you want
3. Call run when you need it; monitor the execution that comes back

**Pausing a schedule during a maintenance window:**

1. Disable the schedule
2. Do the maintenance work
3. Re-enable the schedule; the next scheduled fire-time picks up automatically

## See also

- [Run Profiles](../run-profiles/index.md) -- the operations executed by `RunProfile` steps
- [Activities](../activities/index.md) -- each schedule execution produces a parent activity with child activities per step
- [PowerShell: Schedules](../../powershell/schedules.md) -- cmdlets that wrap these endpoints
