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

### When a step fails

What happens when a step fails is set once for the whole Schedule, with exceptions per step.

**The Schedule's setting.** On the Steps tab of the Schedule editor, above the step list, **When a step fails** is one of:

- **Stop the Schedule** (the default)<br /> The steps after the failed one do not run, each says why, and the Schedule Execution ends **Failed**.
- **Continue the Schedule**<br /> The remaining steps still run, and the Schedule Execution ends **Complete With Error** rather than **Complete**, so the failure is not hidden (see [Seeing how a run ended](#seeing-how-a-run-ended)). This is how many traditional ILM solutions run their schedules.

**Each step's setting.** In the step editor, **When this step fails** is one of:

- **Follow the Schedule** (the default for new steps)<br /> The step does whatever the Schedule is set to do. The option shows the Schedule's current setting, for example "Follow the Schedule (currently: continue)". Because new steps follow the Schedule, a Schedule set to continue also covers steps you add to it later.
- **Stop the Schedule**<br /> This step stops the Schedule when it fails, even if the Schedule is set to continue. Use it for a step whose failure makes later steps unsafe, such as an import that the following synchronisation depends on.
- **Continue the Schedule**<br /> The Schedule carries on when this step fails, even if the Schedule is set to stop. Use it for a step that shouldn't block the rest of the run, such as an optional reporting export.

**Seeing it at a glance.** Every row in the step list, including each step of a parallel group, shows what that step will do and where the setting comes from: for example "Continues on failure · From the Schedule" or "Stops on failure · Set on this step". Changing the Schedule's setting updates every step that follows it straight away, before you save.

JIM decides what a failed step does at the moment it fails, using that step's setting or, when it follows the Schedule, the Schedule's. A change you save while a Schedule is running therefore applies to the next step that fails.

The setting also covers a step that JIM cannot queue when the Schedule starts, for example because its Connected System is being deleted, or its Run Profile targets a partition that is no longer selected. JIM queues every step before any of them runs, so:

- **If that step stops the Schedule**, the Schedule does not start at all. No step runs, the execution is marked **Failed** with a message naming the step and why it could not be queued, and the steps already queued show as **Cancelled** with the reason "Not run: the Schedule could not start."
- **If that step lets the Schedule continue**, it is recorded as **Failed** with the reason ("Could not be queued: ...") and the rest of the Schedule runs without it. The execution then ends **Complete With Error**.

Where steps run in parallel, only a step that actually failed decides: the Schedule stops if a failed step stops it, and carries on if every failed step lets it continue, whatever the steps beside it that succeeded are set to.

Upgrading JIM changes nothing about how your existing Schedules behave: every existing Schedule is set to **Stop the Schedule**, steps that had **Continue on failure** switched on are set to **Continue the Schedule**, and every other step follows the Schedule.

From automation, the Schedule's setting is `-OnStepFailure` on `New-JIMSchedule` and `Set-JIMSchedule`, and a step's is `-OnFailure` on `Add-JIMScheduleStep` and `Set-JIMScheduleStep`; the REST API carries the same settings as `onStepFailure` and `onFailure`. See the [Schedules cmdlets](../powershell/schedules.md) for details.

Nothing is lost when a schedule stops or cannot start: the steps that did not run are simply attempted again on its next run, and a cron schedule stays on its timetable rather than retrying every few seconds. Fix the cause before the next scheduled run (for example, remove the step for a Connected System that is being deleted, or select the partition its Run Profile targets again), or run the schedule manually once it is fixed.

## Executions

Each schedule run produces a **Schedule Execution** record with per-step progress. Active and historical executions can be listed, retrieved, and (for active ones) cancelled.

A Schedule Execution typically appears as a parent activity with one child activity per step; this lets you walk down a schedule's execution tree from a single high-level record into the per-step detail.

### Seeing how a run ended

The Schedules list shows each schedule's last run and, beside it, how that run *ended*. A run that stopped on a failure names the step it stopped on ("Failed on step 3"), so you can tell at a glance whether last night's schedule did what it was supposed to. A schedule that has never run says so.

A run that reached its end but had one or more steps fail along the way, because they were set to let the Schedule continue, ends **Complete With Error** rather than **Complete**, and the list names the steps that failed ("Complete With Error on steps 2 and 3"). A clean run and a run that carried on past a failure therefore never look the same.

Expanding a schedule's row lists its recent executions with their outcomes, how long each took, and how many of its steps ran. This is the quickest way to tell a one-off failure from a step that has been failing all week.

The same last-run outcome is available to automation. `Get-JIMSchedule` and the Schedules list REST endpoint carry it on each Schedule as `LastExecutionId`, `LastExecutionStatus`, `LastExecutionCurrentStepIndex`, `LastExecutionTotalSteps`, `LastExecutionFailedStepIndices`, `LastExecutionCompletedAt` and `LastExecutionErrorMessage`, so a monitoring script can ask whether last night's run succeeded without walking the execution history. `LastExecutionStatus` reads `CompleteWithError` for a run that continued past a failure, so a script that alerts on anything other than `Complete` catches it. See the [Schedules cmdlets](../powershell/schedules.md#get-jimschedule) for the field-by-field description.

### The Schedule Execution view

Selecting an execution opens a view of that single run:

- Every step, in the order it ran, with its outcome and duration.
- Steps that share a step index are shown as a group, because they ran in parallel.
- A link from each step to the [Activity](activities.md) that produced it, where the per-object detail and any error live.
- The error that stopped the run, where one did.
- For a run that ended **Complete With Error**, a warning naming each step that failed and saying it was set to let the Schedule continue, for example "The Schedule finished, but step 3, Active Directory - Export, failed. It is set to let the Schedule continue."
- Beside a failed step that let the run continue, a marker whose tooltip says whether the step was set to continue itself or followed the Schedule.
- For a step that could not be queued when the schedule started, the reason, under the step's name.

A step that was waiting its turn when the run stopped shows as **Cancelled**, with the reason beneath its name, so you can tell why without cross-referencing the rest of the run:

- "Not run: an earlier step stopped the Schedule." An earlier step failed, and it was set to stop the schedule when it fails.
- "Not run: the Schedule could not start." A step could not be queued, so none of the schedule ran.
- "Not run: the Schedule Execution was cancelled." Someone cancelled the run before this step's turn came.

A step that was already running when the run was cancelled shows as **Cancelled** with no reason, because it did run. A step the run never reached at all shows as **Pending**.

An execution that is still in progress refreshes as it goes, and can be cancelled from this view, including while it is still starting (shown as **Queued**). A cancellation always stands: a step that finishes after you cancel the run does not change how it ended, and nothing further is started. Starting a schedule normally takes seconds; if a run is still **Queued** five minutes later (most likely because a JIM service stopped part-way through starting it), JIM marks it **Failed** with the message "The Schedule did not finish starting, most likely because a JIM service stopped part-way through. No steps ran."

The same reasons are available to automation: each step returned by `Get-JIMScheduleExecution -Id` and the Schedule Execution REST endpoint carries a `CancellationReason`. See the [Schedules cmdlets](../powershell/schedules.md#get-jimscheduleexecution) for the field-by-field description.

### Watching one run

While a schedule is running, its tasks are grouped under a header in **Admin > Operations > Queue**, and that header draws the whole schedule as a rail: one marker per step, in the order they run, with the step names underneath and the step it has reached named beside them ("Step 2 of 5").

The rail shows the whole schedule, not just the part still queued. A task is removed from the queue the moment it finishes, so steps already done would otherwise disappear as the run progressed; their outcomes are read from their Activities instead. A step that failed stays visible as a failed marker, including when the group is collapsed to a single row.

A step running several tasks at once is drawn as one divided marker, a wedge per task, each carrying that task's own outcome. So a step where one of two parallel imports has failed while the other is still running reads as exactly that, rather than as a single colour for the whole step. The wedges are ordered by outcome rather than by task, so a failure always starts at the top of the marker and stays visible however wide the schedule fans out.

A step's position counts step *groups*: steps that run concurrently share one position, so a schedule of six steps where two run together is five steps long.

## Enabled flag

Disabled schedules don't fire on their cron trigger and don't appear as eligible for manual run. This is useful for temporarily pausing a schedule during maintenance without losing its definition.

## Built-in schedules

JIM ships with a small number of **built-in schedules** that are part of the product rather than something you create. They behave like any other schedule with one difference: their name is fixed and they cannot be deleted. You can still change **when** they run and enable or disable them.

- **Temporal Scope Reconciliation**<br /> Keeps [relative-date scope filters](synchronisation-rules.md) live for objects whose source data isn't changing. Both the import and export hot paths skip an object that hasn't changed since the last run, so a leaver whose end date has just passed, or a joiner whose start date has just arrived, would otherwise never be re-evaluated until something else about them changed. This schedule periodically re-checks those time-driven scope transitions and routes the affected objects back through the normal synchronisation engine, so date-driven deprovisioning and staged provisioning happen on their own. It runs hourly by default; lower the interval for tighter timing, or raise it to reduce background work.

- **History Retention Cleanup**<br /> Removes history that has had the retention period set for its kind: Connected System Object and Metaverse Object change history, configuration change previews, Activities, [initial-password records](../concepts/passwords.md), and [Pending Password Changes](../concepts/passwords.md) that reached a terminal state. Each kind is governed by its own `History.*` [Service Setting](../administration/configuration.md#service-settings), so audit history that must be kept for years is not aged out alongside high-volume synchronisation history. Records still being worked are never removed, however old. It runs daily at 02:30 by default; each pass is capped by `History.CleanupBatchSize`, so a deployment with a large backlog drains over several runs rather than in one long transaction. The Activity for each run says exactly what it removed, so "is retention working?" is answered by looking, not inferring.

What you can and can't do with a built-in schedule:

- ✅ **Re-time it**<br /> Change the trigger pattern or interval (for example, run the reconciler every 15 minutes instead of hourly).
- ✅ **Enable or disable it**<br /> Pause it during a maintenance window and re-enable it afterwards.
- ❌ **Rename it**<br /> The name is fixed so the schedule stays recognisable and JIM can keep it maintained across upgrades.
- ❌ **Delete it**<br /> Built-in schedules are part of the product; disable one instead if you don't want it to run.
- ❌ **Change its steps**<br /> The step composition is defined and maintained by JIM; the Steps tab is read-only for a built-in schedule.
- ❌ **Change what happens when a step fails**<br /> A built-in Schedule stops when a step fails, and its steps follow it; JIM manages both, so **When a step fails** is read-only in the editor.

The portal reflects this: a built-in schedule's name and steps are read-only in the editor, and its delete action is replaced with a lock. These rules are enforced everywhere, not just in the portal: the PowerShell module and REST API reject a rename, delete, step change or change to what happens when a step fails, and the underlying application layer is the authoritative backstop.

## Auditing

Every configuration change to a schedule is recorded in the immutable audit log (Activities): creating, editing, enabling, disabling, re-timing and deleting are each captured as an Activity attributed to whoever made the change, the same audit trail JIM keeps for Connected Systems and Synchronisation Rules. Running a schedule is tracked separately, as a schedule execution and its per-step Activities.

## Change history

Every change to a Schedule's configuration (including its Steps) is recorded as a versioned snapshot, the same way as for [Synchronisation Rules and Connected Systems](activities.md#configuration-change-history). The **History** tab in the Schedule editor shows the timeline of changes, each as a field-by-field "before and after", and lets you compare any two versions. A step's SQL connection string is treated as a secret: a change to it is recorded, but its value is never stored or shown.

The same history is available from the `Get-JIMConfigurationChangeHistory` [cmdlet](../powershell/history.md) (`-Type Schedule -Id <guid>`) and the Schedule `change-history` REST endpoints in the [interactive API reference](../../api/reference/).

When changing a schedule from automation you can record a reason alongside the change, exactly as for Synchronisation Rules and Connected Systems: pass `-ChangeReason` on the Schedule write cmdlets (`New-`, `Set-`, `Remove-`, `Enable-`, `Disable-JIMSchedule` and the step cmdlets), or the optional `changeReason` field/query parameter on the REST write endpoints. The reason shows with the change and on its Activity.

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
