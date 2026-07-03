---
title: Schedules
---

# Schedules

A **schedule** defines an automated sequence of operations that JIM executes on a trigger (cron-based or manual). Each schedule contains ordered steps that can run sequentially or in parallel, and supports several step types: [Run Profile](run-profiles.md) execution, PowerShell scripts, external executables, and SQL scripts.

Schedules are the primary mechanism for automating identity synchronisation workflows. A typical example is a nightly schedule that imports from each Connected System, runs synchronisation, and exports the results.

## Triggers

A schedule is one of two trigger types:

- **Cron**<br /> Runs automatically on a recurring pattern.
- **Manual**<br /> Runs only when explicitly triggered.

## Pattern types

Cron schedules support three authoring modes:

- **Specific times**<br /> Pick days and times of day (e.g. weekdays at 06:00 and 18:00). JIM derives the cron expression from your selection.
- **Interval**<br /> Run every N minutes or hours within a daily window (e.g. every 15 minutes between 08:00 and 18:00 on weekdays).
- **Custom**<br /> Supply a raw cron expression for full control.

The first two modes cover the vast majority of cases without requiring administrators to think in cron syntax. Custom is the escape hatch for the unusual schedules they don't.

## Steps

Each step has an execution mode and a step type.

### Execution mode

- **Sequential**<br /> The step runs after the previous one finishes.
- **Parallel with previous**<br /> The step runs at the same time as the previous one.

A `stepIndex` orders steps; multiple steps with the same index run in parallel.

### Step types

- **Run Profile**<br /> Execute a [Run Profile](run-profiles.md) against a Connected System.
- **PowerShell**<br /> Run a PowerShell script with arguments.
- **Executable**<br /> Run an external executable.
- **SQL script**<br /> Run a SQL script against a configured database connection.

### Continue on failure

Set per step. By default, a failing step halts the schedule. Turn this on for steps where downstream work should proceed regardless (for example, an optional reporting step that shouldn't block the rest of the run).

## Executions

Each schedule run produces a **schedule execution** record with per-step progress. Active and historical executions can be listed, retrieved, and (for active ones) cancelled.

A schedule execution typically appears as a parent activity with one child activity per step; this lets you walk down a schedule's execution tree from a single high-level record into the per-step detail.

## Enabled flag

Disabled schedules don't fire on their cron trigger and don't appear as eligible for manual run. This is useful for temporarily pausing a schedule during maintenance without losing its definition.

## Change history

Every change to a Schedule's configuration (including its Steps) is recorded as a versioned snapshot, the same way as for [Synchronisation Rules and Connected Systems](activities.md#configuration-change-history). The **History** tab in the Schedule editor shows the timeline of changes, each as a field-by-field "before and after", and lets you compare any two versions. A step's SQL connection string is treated as a secret: a change to it is recorded, but its value is never stored or shown.

The same history is available from the `Get-JIMConfigurationChangeHistory` [cmdlet](../powershell/history.md) (`-Type Schedule -Id <guid>`) and the Schedule `change-history` REST endpoints in the [interactive API reference](../../api/reference/).

## Common workflows

**Setting up an automated nightly sync:**

1. Create a schedule with a cron trigger, a specific-times pattern, and the schedule enabled
2. Add ordered steps for each operation: imports first, then syncs, then exports (typically sequential)
3. Verify by triggering a manual run before the first scheduled fire
4. Monitor the resulting execution to confirm each step completes as expected

**Running an ad-hoc sync via a manual schedule:**

1. Create a schedule with a manual trigger
2. Add the steps for the operations you want
3. Run it when you need it; monitor the execution

**Pausing a schedule during a maintenance window:**

1. Disable the schedule
2. Do the maintenance work
3. Re-enable the schedule; the next scheduled fire-time picks up automatically

## Manage Schedules

- **JIM portal**<br /> Schedules area of the admin UI
- **PowerShell**<br /> [Schedules cmdlets](../powershell/schedules.md) (`Get-JIMSchedule`, `New-JIMSchedule`, `Invoke-JIMSchedule`, etc.)
- **REST API**<br /> Schedules and schedule execution endpoints in the [interactive API reference](../../api/reference/)

## See also

- [Run Profiles](run-profiles.md) -- the operations executed by Run Profile steps
- [Activities](activities.md) -- each schedule execution produces a parent activity with child activities per step
