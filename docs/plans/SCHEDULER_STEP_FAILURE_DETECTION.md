# Plan: Scheduler Step Failure Detection (Activity-Based)

- **Status:** Phase 1 Implemented
- **Milestone:** MVP
- **Branch:** `feature/scheduler-step-failure-detection`

## Problem

When the JIM scheduler executes a multi-step schedule, individual run profile steps can fail with `FailedWithError` or `CompleteWithError`, yet the schedule execution is incorrectly marked as "Completed". This means:

1. **Users see false success** -- schedule executions appear to complete normally despite step failures
2. **Integration tests don't catch regressions** -- Scenario 6 passes when it should fail
3. **ContinueOnFailure is broken** -- the setting has no effect because failures are never detected

### Root Cause

The scheduler's `CheckAndAdvanceExecutionAsync` method queries **worker tasks** to determine if steps completed and whether any failed. However, the worker **deletes** worker tasks from the database immediately upon completion (regardless of success/failure). By the time the scheduler polls (every 30 seconds), the tasks are gone:

```
Timeline:
  T+0s    Worker picks up task, starts processing
  T+5s    Worker completes task (success OR failure)
  T+5s    Worker DELETES task from database     <-- task is gone
  T+30s   Scheduler polls CheckAndAdvanceExecutionAsync
  T+30s   Query returns empty list              <-- no tasks to check
  T+30s   anyFailed = false                     <-- bug: failure not detected
  T+30s   Execution marked "Completed"          <-- wrong
```

### Secondary Bug

The parallel step `ContinueOnFailure` check used `FirstOrDefault()` to find a single step at the current index. For parallel step groups (multiple steps sharing the same index), this could pick the wrong step and miss the `ContinueOnFailure = false` setting.

## Solution: Activity-Based Step Tracking

Instead of relying on ephemeral worker tasks, the scheduler now queries **Activities** -- the immutable audit records that persist forever -- to determine step outcomes.

### Key Insight

Activities are already the source of truth for operation outcomes (status, timing, errors, per-object execution items). Worker tasks are merely a queue coordination mechanism. By adding `ScheduleExecutionId` and `ScheduleStepIndex` to the Activity model, the scheduler can find step outcomes reliably regardless of worker task lifecycle.

### Design Principles

- **No state duplication** -- Activity is already the source of truth; we just add a lookup key
- **No worker task lifecycle changes** -- worker tasks remain ephemeral (created, processed, deleted)
- **Follows existing patterns** -- `ScheduleExecutionId` on Activity mirrors existing context fields (`ConnectedSystemId`, `SyncRuleId`, `MetaverseObjectId`)
- **Enables future UX** -- the Activity-to-ScheduleExecution link supports drill-down in the UI

## Phase 1: Core Fix (Complete)

### Changes Made

#### Model Layer

- **`src/JIM.Models/Activities/Activity.cs`** -- Added `ScheduleExecutionId` (Guid?) and `ScheduleStepIndex` (int?) properties
- **`src/JIM.PostgresData/Migrations/AddScheduleContextToActivity`** -- EF migration for the two new nullable columns

#### Data Layer

- **`src/JIM.Data/Repositories/IActivityRepository.cs`** -- Added `GetActivitiesByScheduleExecutionAsync` and `GetActivitiesByScheduleExecutionStepAsync`
- **`src/JIM.PostgresData/Repositories/ActivitiesRepository.cs`** -- Implemented the two new query methods

#### Application Layer

- **`src/JIM.Application/Servers/TaskingServer.cs`** -- `CreateActivityFromWorkerTaskAsync` now copies `ScheduleExecutionId` and `ScheduleStepIndex` from the WorkerTask to the Activity before persisting
- **`src/JIM.Application/Servers/SchedulerServer.cs`** -- `CheckAndAdvanceExecutionAsync` rewritten:
  1. Check for active worker tasks first (Queued/Processing = still running)
  2. Query Activities for step outcomes (survives worker task deletion)
  3. Check ALL parallel steps at an index for `ContinueOnFailure` (not just `FirstOrDefault`)

#### API Layer

- **`src/JIM.Web/Controllers/Api/ScheduleExecutionsController.cs`** -- `GetByIdAsync` queries Activities for step status, with worker tasks as secondary source for in-progress steps. `GetStepStatus` updated to prefer Activity status over inferred position-based status.
- **`src/JIM.Web/Models/Api/ScheduleExecutionDtos.cs`** -- Added `ActivityId` and `ActivityStatus` to `ScheduleExecutionStepDto`

#### Integration Tests

- **`src/JIM.PowerShell/Public/RunProfiles/Start-JIMRunProfile.ps1`** -- Changed `Write-Error` to `throw` for fail-fast error propagation
- **`test/integration/utils/Test-Helpers.ps1`** -- Added `Assert-ScheduleExecutionSuccess` function that validates both overall execution status AND each step's activity status
- **`test/integration/scenarios/Invoke-Scenario6-SchedulerService.ps1`** -- Uses `Assert-ScheduleExecutionSuccess` at 3 validation points (manual trigger, multi-step, parallel)

#### Unit Tests

- **`test/JIM.Web.Api.Tests/ScheduleExecutionsControllerTests.cs`** -- Updated existing tests for Activity repository mock, added 3 new tests:
  - `GetByIdAsync_FailedActivity_ShowsFailedStatusAsync`
  - `GetByIdAsync_ActiveWorkerTask_ShowsProcessingStatusAsync`
  - `GetByIdAsync_MultipleSteps_ShowsCorrectStatusFromActivitiesAsync`

### How It Works Now

```
Timeline (fixed):
  T+0s    Scheduler queues WorkerTask (with ScheduleExecutionId, ScheduleStepIndex)
  T+0s    TaskingServer creates Activity (copies ScheduleExecutionId, ScheduleStepIndex)
  T+1s    Worker picks up task, starts processing
  T+5s    Worker completes task -- Activity updated to FailedWithError
  T+5s    Worker deletes task from database
  T+30s   Scheduler polls CheckAndAdvanceExecutionAsync
  T+30s   No active worker tasks found
  T+30s   Query Activities for step: finds Activity with FailedWithError
  T+30s   anyFailed = true, ContinueOnFailure = false
  T+30s   Execution marked "Failed" with error message     <-- correct
```

## Phase 2: UX Enhancements (Future -- No Commitment)

The Activity-to-ScheduleExecution link created in Phase 1 enables several UX improvements. These are independent of each other and can be implemented in any order.

### 2a. Schedule Execution Detail View

**What:** When viewing a schedule execution, show each step with its activity status and a link to the activity detail page.

**Where:** New page or expanded section on the Operations > Schedules tab.

**Data available:** The `GET /api/v1/schedule-executions/{id}` endpoint already returns `Steps[]` with `ActivityId`, `ActivityStatus`, `ErrorMessage`, `StartedAt`, `CompletedAt`. The UI just needs to render it.

**Wireframe:**
```
Schedule Execution: "Delta Sync" -- Failed
Started: 2026-02-13 06:00:00 UTC | Completed: 2026-02-13 06:05:32 UTC

+-------+-------------------------+-------------------+----------+----------+
| Step  | Name                    | Status            | Duration | Activity |
+-------+-------------------------+-------------------+----------+----------+
| 0     | HR Full Import          | Completed         | 2m 15s   | [View]   |
| 1     | Badge Full Import       | Completed         | 1m 42s   | [View]   |
| 2     | LDAP Delta Import       | Failed            | 0m 03s   | [View]   |
| 3     | Inbound Sync            | Pending           | --       | --       |
| 4     | AD Export               | Pending           | --       | --       |
+-------+-------------------------+-------------------+----------+----------+

Error: Step 'LDAP Delta Import' failed and ContinueOnFailure is false.
```

### 2b. History Tab -- Schedule Filter

**What:** Add a filter on the Operations > History tab: "Show only activities from schedule X" or "Show only scheduled activities".

**Where:** `OperationsHistoryTab.razor` -- add a filter chip/dropdown.

**Query:** Filter activities where `ScheduleExecutionId IS NOT NULL`, optionally joined to `ScheduleExecution.ScheduleId` for schedule-level filtering.

### 2c. Activity Detail -- Schedule Context

**What:** On the activity detail page, if the activity is part of a schedule execution, show a context banner: "Part of schedule execution [link] -- Step 3 of 5".

**Where:** Activity detail page/component.

**Data available:** `Activity.ScheduleExecutionId` and `Activity.ScheduleStepIndex` are already populated. Need to look up `ScheduleExecution.ScheduleName` for display.

### 2d. Schedules Tab -- Execution History

**What:** Add a "View execution history" button on each schedule in the Schedules tab, showing recent runs with pass/fail status.

**Where:** `OperationsSchedulesTab.razor` -- expand each schedule row or add a sub-view.

**Data available:** The `GET /api/v1/schedule-executions?scheduleId={id}` endpoint already supports this with pagination.

### 2e. Queue Tab -- Schedule Attribution

**What:** When a worker task was queued by a schedule, show "Queued by schedule [name]" in the queue display.

**Where:** `OperationsQueueTab.razor` -- add a column or badge.

**Data available:** `WorkerTask.ScheduleExecutionId` is already populated. Need to join to `ScheduleExecution.ScheduleName` for display.

## Verification

1. `dotnet build JIM.sln` -- must succeed (verified)
2. `dotnet test JIM.sln` -- all 1,423 tests pass (verified)
3. Integration test Scenario 6 should now detect failed steps and fail the test
