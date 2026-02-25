# Plan: Implement Scheduler Service (Issue #168)

- **Status:** Implemented
- **Milestone:** MVP
- **GitHub Issue:** [#168](https://github.com/TetronIO/JIM/issues/168)

## Overview

Implement a scheduler service to automate synchronisation workflows. A **Schedule** is a plan containing multiple steps (run profiles, scripts, executables) that execute as a cohesive unit - similar to enterprise sync schedules like "Delta Sync (Mon-Fri)" or "Full Sync (Sunday)".

### Key Concepts

- **Schedule**: A reusable plan defining a sequence of steps with timing (cron or manual)
- **ScheduleStep**: An individual task within a schedule (run profile, PowerShell, SQL, executable)
- **ScheduleExecution**: A running instance of a schedule, tracking progress through its steps
- **WorkerTask**: Individual tasks in the Worker queue (can be standalone or part of a ScheduleExecution)

### Example: Enterprise Delta Sync Schedule

A typical enterprise schedule with parallel imports, sequential syncs, and parallel exports:

```
Schedule: "Delta Sync" (Mon-Fri 6:00am)
==========================================

Step  Name                    Mode                 Execution
----  ----------------------  -------------------  -----------------------------------
1     HR DeltaImport          Sequential           Group 1 (starts parallel block)
2     Badge DeltaImport       ParallelWithPrevious Group 1 (concurrent with Step 1)
3     LDAP DeltaImport        ParallelWithPrevious Group 1 (concurrent with Steps 1-2)
      [Wait for all imports to complete]
4     HR DeltaSync            Sequential           Group 2 (waits for Group 1)
5     Badge DeltaSync         Sequential           Group 3 (waits for Group 2)
6     LDAP DeltaSync          Sequential           Group 4 (waits for Group 3)
      [Wait for all syncs to complete]
7     AD Export               Sequential           Group 5 (starts parallel block)
8     ServiceNow Export       ParallelWithPrevious Group 5 (concurrent with Step 7)
      [Wait for exports to complete]
9     AD ConfirmingImport     Sequential           Group 6 (starts parallel block)
10    ServiceNow ConfirmImport ParallelWithPrevious Group 6 (concurrent with Step 9)
      [Wait for confirming imports]
11    AD ConfirmingSync       Sequential           Group 7
12    ServiceNow ConfirmSync  Sequential           Group 8
```

**Execution Modes:**
- `Sequential` = Wait for all previous steps, then start a new execution group
- `ParallelWithPrevious` = Join the current group (run concurrently with previous step)

### Architecture

```
Scheduler Service                    Worker Service
      |                                    |
      | (checks due schedules)             | (processes queue)
      v                                    v
+------------------+              +------------------+
| Schedule         |              | WorkerTask Queue |
| - Steps[]        |   triggers   | - Task 1         |
| - Cron/Manual    | -----------> | - Task 2         |
+------------------+              | - Task 3         |
                                  +------------------+
                                         |
                                         v
                                  +------------------+
                                  | ScheduleExecution|
                                  | (tracks progress)|
                                  +------------------+
```

---

## Phase 1: Operations Page Redesign (Tabbed Interface) ✅ COMPLETE

### Goal
Transform the Operations page into a "Task Management Centre" with three tabs: Queue, History, and Schedules.

### Implementation

**1.1 Tab Structure**

```
+------------------------------------------------------------------+
| Operations                                                       |
+------------------------------------------------------------------+
| [Queue]  [History]  [Schedules]                                  |
+------------------------------------------------------------------+
| <Tab Content Area>                                               |
+------------------------------------------------------------------+
```

**1.2 Queue Tab - Hierarchical View**

Shows schedule executions and standalone tasks with collapsible groups:

```
+--------------------------------------------------------------------------+
| Execute Run Profile: [Connected System v] [Run Profile v] [Execute]      |
+--------------------------------------------------------------------------+
| v Delta Sync Schedule (Step 3/6)                        [Pause] [Cancel] |
|   +-- Y HR Import (Completed 2m ago)                                     |
|   +-- Y Badge Import (Completed 1m ago)                                  |
|   +-- * Delta Sync (Processing 45%)                          [Cancel]    |
|   +-- o AD Export (Waiting)                                              |
|   +-- o ServiceNow Export (Waiting)                                      |
|   +-- o Confirming Imports (Waiting)                                     |
+--------------------------------------------------------------------------+
| * Priority: Emergency User Disable (Queued)                  [Cancel]    |
+--------------------------------------------------------------------------+
| > Full Sync Schedule (Queued - waiting)                      [Cancel]    |
+--------------------------------------------------------------------------+
```

Legend: Y = Completed, * = Processing, o = Waiting (not yet queued)

**1.3 History Tab**

- Filtered Activities showing schedule executions and standalone tasks
- Click row -> slide-in panel (same pattern as Logs page)
- Shows: Schedule name, steps completed, duration, errors, initiated by

**1.4 Schedules Tab (Placeholder)**

- Initially shows "Coming soon" message
- Implemented in Phase 4

### Files to Modify
- `src/JIM.Web/Pages/Admin/Operations.razor` - Add MudTabs, hierarchical queue view
- `src/JIM.Web/wwwroot/css/site.css` - Add panel styles

### Files to Create
- `src/JIM.Web/Pages/Admin/Components/OperationsQueueTab.razor`
- `src/JIM.Web/Pages/Admin/Components/OperationsHistoryTab.razor`

---

## Phase 2: Scheduler Data Model ✅ COMPLETE

### Goal
Create the data model for schedules, steps, and executions.

### Entities

**2.1 Schedule (Definition)**

```csharp
public class Schedule
{
    public Guid Id { get; set; }
    public string Name { get; set; }  // e.g., "Delta Sync Schedule"
    public string? Description { get; set; }

    // Timing
    public ScheduleTriggerType TriggerType { get; set; }  // Cron or Manual
    public string? CronExpression { get; set; }  // e.g., "0 6 * * 1-5" (6am Mon-Fri)

    // State
    public bool IsEnabled { get; set; }
    public DateTime? LastRunTime { get; set; }
    public DateTime? NextRunTime { get; set; }

    // Steps
    public List<ScheduleStep> Steps { get; set; } = new();

    // Audit
    public DateTime Created { get; set; }
    public DateTime? Modified { get; set; }
}

public enum ScheduleTriggerType { Cron, Manual }
```

> **IMPORTANT: User-Friendly Scheduling UI**
>
> Users will NOT enter cron syntax directly. The UI must provide a user-friendly scheduling interface
> (dropdowns, checkboxes, time pickers) that translates the user's scheduling requirements to cron
> expressions under the hood. Example UI components:
> - Frequency selector: "Every day", "Specific days of week", "Specific days of month"
> - Time picker for run time(s)
> - Preview showing "Runs at 6:00 AM on Monday, Tuesday, Wednesday, Thursday, Friday"
> - Advanced users can optionally view/edit the generated cron expression

**2.2 ScheduleStep (Task Definition)**

```csharp
public class ScheduleStep
{
    public Guid Id { get; set; }
    public Guid ScheduleId { get; set; }
    public Schedule Schedule { get; set; }

    public int StepIndex { get; set; }  // Execution order
    public string Name { get; set; }  // Display name

    // Execution mode
    public StepExecutionMode ExecutionMode { get; set; }  // Sequential, ParallelWithPrevious

    // What to execute
    public StepType StepType { get; set; }
    public string Configuration { get; set; }  // JSON - type-specific config
}

public enum StepExecutionMode { Sequential, ParallelWithPrevious }

public enum StepType
{
    RunProfile,      // Execute a connected system run profile
    PowerShell,      // Execute a PowerShell script
    Executable,      // Execute an external program
    SqlScript        // Execute a SQL script (future)
}
```

**2.3 ScheduleExecution (Running Instance)**

```csharp
public class ScheduleExecution
{
    public Guid Id { get; set; }
    public Guid ScheduleId { get; set; }
    public Schedule Schedule { get; set; }

    // Progress
    public ScheduleExecutionStatus Status { get; set; }
    public int CurrentStepIndex { get; set; }
    public int TotalSteps { get; set; }

    // Timing
    public DateTime QueuedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Initiator (reuse existing pattern)
    public ActivityInitiatorType InitiatedByType { get; set; }
    public Guid? InitiatedById { get; set; }
    public string? InitiatedByName { get; set; }

    // Results
    public string? ErrorMessage { get; set; }
}

public enum ScheduleExecutionStatus
{
    Queued,
    InProgress,
    Completed,
    Failed,
    Cancelled,
    Paused
}
```

**2.4 WorkerTask Enhancement**

Add optional FK to link tasks to schedule executions:

```csharp
public class WorkerTask  // Existing, enhanced
{
    // ... existing fields ...

    // NEW: Link to schedule execution (nullable)
    public Guid? ScheduleExecutionId { get; set; }
    public ScheduleExecution? ScheduleExecution { get; set; }
    public int? ScheduleStepIndex { get; set; }
}
```

### Database Migration
- Add `Schedules` table
- Add `ScheduleSteps` table (FK to Schedules)
- Add `ScheduleExecutions` table (FK to Schedules)
- Add `ScheduleExecutionId` and `ScheduleStepIndex` columns to `WorkerTasks`

### Files to Create
- `src/JIM.Models/Scheduling/Schedule.cs`
- `src/JIM.Models/Scheduling/ScheduleStep.cs`
- `src/JIM.Models/Scheduling/ScheduleExecution.cs`
- `src/JIM.Models/Scheduling/SchedulingEnums.cs`
- `src/JIM.Data/Repositories/ISchedulingRepository.cs`
- `src/JIM.PostgresData/Repositories/SchedulingRepository.cs`

---

## Phase 3: Scheduler Service & Worker Integration ✅ COMPLETE

### Goal
Implement the scheduler service and integrate with the Worker for step progression.

### Scheduler Service Responsibilities

1. **Check for due schedules** (polling every 30 seconds)
2. **Prevent overlap** - Don't start if previous execution still running
3. **Create ScheduleExecution** and queue first step(s)
4. **Calculate next run time** after execution starts

```csharp
public class SchedulerService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAndStartDueSchedulesAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task CheckAndStartDueSchedulesAsync()
    {
        var dueSchedules = await _repo.GetDueSchedulesAsync(DateTime.UtcNow);
        foreach (var schedule in dueSchedules)
        {
            // Check for existing running execution
            if (await HasRunningExecutionAsync(schedule.Id))
                continue;

            // Create execution and queue first step(s)
            await StartScheduleExecutionAsync(schedule);
        }
    }
}
```

### Worker Integration - Step Progression

When a WorkerTask with `ScheduleExecutionId` completes:

```csharp
// In Worker, after task completion
if (task.ScheduleExecutionId.HasValue)
{
    await _schedulingServer.OnScheduleStepCompletedAsync(
        task.ScheduleExecutionId.Value,
        task.ScheduleStepIndex!.Value,
        success: true);
}
```

Step progression logic:

```csharp
public async Task OnScheduleStepCompletedAsync(Guid executionId, int stepIndex, bool success)
{
    var execution = await _repo.GetScheduleExecutionAsync(executionId);

    // Check if all parallel tasks for this step are complete
    var pendingTasks = await _repo.GetPendingTasksForStepAsync(executionId, stepIndex);
    if (pendingTasks.Any())
        return;  // Wait for siblings

    if (!success)
    {
        await FailScheduleExecutionAsync(execution);
        return;
    }

    // Find and queue next step(s)
    var nextSteps = GetNextSteps(execution.Schedule, stepIndex);
    if (!nextSteps.Any())
    {
        await CompleteScheduleExecutionAsync(execution);
        return;
    }

    await QueueStepsAsync(execution, nextSteps);
}
```

### Parallel Step Handling

When a step is marked `ParallelWithPrevious`, it runs concurrently with the previous step:

```
Step 1: HR Import (Sequential)           -> Creates 1 WorkerTask
Step 2: Badge Import (ParallelWithPrev)  -> Creates 1 WorkerTask (same StepIndex group)
Step 3: LDAP Import (ParallelWithPrev)   -> Creates 1 WorkerTask (same StepIndex group)
Step 4: Delta Sync (Sequential)          -> Waits for Steps 1-3, then creates 1 WorkerTask
```

All parallel steps share a logical "step group" - progression happens when ALL tasks in the group complete.

### Implementation Summary

**Database-Driven Callback Mechanism:**
- Worker completes tasks and updates status in database
- Scheduler polls for completed tasks (every 30 seconds)
- No direct inter-service communication required
- Resilient to service restarts

**Files Created:**
- `src/JIM.Application/Servers/SchedulerServer.cs` - Core scheduling logic
- `src/JIM.Scheduler/Scheduler.cs` - BackgroundService implementation

**Files Modified:**
- `src/JIM.Scheduler/JIM.Scheduler.csproj` - Updated to use Worker SDK pattern
- `src/JIM.Scheduler/Program.cs` - Generic Host setup
- `src/JIM.Application/JimApplication.cs` - Added SchedulerServer
- `src/JIM.Data/Repositories/ITaskingRepository.cs` - Added scheduler queries
- `src/JIM.PostgresData/Repositories/TaskingRepository.cs` - Implemented scheduler queries

**Dependencies Added:**
- `NCrontab 3.4.0` - Cron expression parsing (by Atif Aziz, Microsoft, Apache 2.0 license)

**Future Enhancement (Issue #307):**
- Transition to event-based architecture using PostgreSQL LISTEN/NOTIFY
- SignalR push for real-time UI updates

---

## Phase 4: Schedules Tab UI ✅ COMPLETE

### Goal
Implement the schedule management UI.

### Schedule List View

```
+------------------------------------------------------------------------+
| [+ New Schedule]                                         [Refresh]     |
+------------------------------------------------------------------------+
| Name              | Steps | Trigger         | Next Run   | Status      |
+------------------------------------------------------------------------+
| Delta Sync        | 8     | Mon-Fri 6:00am  | Tomorrow   | Enabled     |
| Full Sync         | 12    | Sunday 2:00am   | In 3 days  | Enabled     |
| HR Emergency Sync | 4     | Manual          | -          | Disabled    |
+------------------------------------------------------------------------+
```

### Schedule Editor

Multi-step wizard or tabbed dialog:

**Tab 1: General**
- Name, Description
- Trigger type (Scheduled / Manual)
- User-friendly scheduling interface (NOT raw cron input):
  - Frequency: "Every day", "Specific days of week", "Specific days of month"
  - Time picker(s) for when to run
  - Preview text showing human-readable schedule (e.g., "Runs at 6:00 AM on weekdays")
  - Optional: "Show cron expression" toggle for advanced users

**Tab 2: Steps**
- Drag-and-drop step ordering
- Add step button -> step type selector
- Each step shows: Type icon, name, execution mode toggle (sequential/parallel)

**Step Configuration by Type:**
- **Run Profile**: Connected System dropdown -> Run Profile dropdown
- **PowerShell**: Script path or inline script
- **Executable**: Program path, arguments, working directory

### Files Created
- `src/JIM.Web/Pages/Admin/Components/OperationsSchedulesTab.razor` - Schedule list with actions
- `src/JIM.Web/Pages/Admin/Components/ScheduleEditorDialog.razor` - Schedule editor with inline step editing

### Files Modified
- `src/JIM.Web/Pages/Admin/Operations.razor` - Enabled Schedules tab

### Implementation Notes
- User-friendly scheduling UI with frequency selection (daily, weekdays, weekends, weekly, hourly, every N minutes)
- Time picker for scheduled runs
- Human-readable preview of schedule (e.g., "Runs Monday through Friday at 6:00 AM")
- Inline step editor within the schedule dialog (rather than nested dialogs)
- Drag-and-drop step reordering using MudDropContainer
- Enable/disable toggle and manual "Run Now" actions
- Server-side pagination and search

---

## Phase 5: API Endpoints ✅ COMPLETE

### Endpoints

```
# Schedules (v1)
GET    /api/v1/schedules                       - List all schedules (paginated)
GET    /api/v1/schedules/{id}                  - Get schedule with steps
POST   /api/v1/schedules                       - Create schedule
PUT    /api/v1/schedules/{id}                  - Update schedule (replaces steps)
DELETE /api/v1/schedules/{id}                  - Delete schedule
POST   /api/v1/schedules/{id}/enable           - Enable schedule
POST   /api/v1/schedules/{id}/disable          - Disable schedule
POST   /api/v1/schedules/{id}/run              - Manually trigger execution

# Schedule Executions (v1)
GET    /api/v1/schedule-executions             - List executions (paginated, filterable by scheduleId)
GET    /api/v1/schedule-executions/{id}        - Get execution with step statuses
GET    /api/v1/schedule-executions/active      - Get currently running executions
POST   /api/v1/schedule-executions/{id}/cancel - Cancel running execution
```

### Files Created
- `src/JIM.Web/Controllers/Api/SchedulesController.cs` - Full CRUD + enable/disable/run
- `src/JIM.Web/Controllers/Api/ScheduleExecutionsController.cs` - List, detail, cancel, active
- `src/JIM.Web/Models/Api/ScheduleDtos.cs` - ScheduleDto, ScheduleDetailDto, ScheduleStepDto
- `src/JIM.Web/Models/Api/ScheduleRequestDtos.cs` - CreateScheduleRequest, UpdateScheduleRequest, ScheduleStepRequest
- `src/JIM.Web/Models/Api/ScheduleExecutionDtos.cs` - ScheduleExecutionDto, ScheduleExecutionDetailDto, ScheduleExecutionStepDto

### Data Model Change
- Migrated from JSON `Configuration` column to typed polymorphic properties on `ScheduleStep`
- Properties: `ConnectedSystemId`, `RunProfileId`, `ScriptPath`, `Arguments`, `ExecutablePath`, `WorkingDirectory`, `SqlConnectionString`, `SqlScriptPath`
- Migration: `ScheduleStepTypedConfiguration`

### API Design Notes
- Polymorphic step DTOs with `StepType` as discriminator
- Step properties are flattened - only relevant properties used per type
- PUT replaces entire step collection (simpler than partial updates)
- Pagination uses `PaginatedResponse<T>` pattern

---

## Phase 6: PowerShell Module ✅ COMPLETE

### Cmdlets

```powershell
# Schedule management
Get-JimSchedule [-Id <Guid>] [-Name <string>]
New-JimSchedule -Name <string> -TriggerType <Cron|Manual> [-CronExpression <string>]
Set-JimSchedule -Id <Guid> [-Name <string>] [-Enabled <bool>] ...
Remove-JimSchedule -Id <Guid> [-Force]
Enable-JimSchedule -Id <Guid>
Disable-JimSchedule -Id <Guid>
Start-JimSchedule -Id <Guid>  # Manually trigger

# Schedule steps
Add-JimScheduleStep -ScheduleId <Guid> -StepType <RunProfile|PowerShell|Executable> -Configuration <hashtable> [-Parallel]
Remove-JimScheduleStep -ScheduleId <Guid> -StepIndex <int>
Set-JimScheduleStepOrder -ScheduleId <Guid> -StepOrder <int[]>

# Executions
Get-JimScheduleExecution [-ScheduleId <Guid>] [-Status <string>]
Stop-JimScheduleExecution -Id <Guid>
```

### Example: Creating a Delta Sync Schedule

```powershell
# Create the schedule
$schedule = New-JimSchedule -Name "Delta Sync" -TriggerType Cron -CronExpression "0 6 * * 1-5"

# Add steps
$hrProfile = Get-JimRunProfile -ConnectedSystem "HR System" -Name "DeltaImport"
Add-JimScheduleStep -ScheduleId $schedule.Id -StepType RunProfile -Configuration @{
    RunProfileId = $hrProfile.Id
}

$badgeProfile = Get-JimRunProfile -ConnectedSystem "Badge System" -Name "DeltaImport"
Add-JimScheduleStep -ScheduleId $schedule.Id -StepType RunProfile -Configuration @{
    RunProfileId = $badgeProfile.Id
} -Parallel  # Runs in parallel with previous step

# Add sync step (sequential - waits for imports)
$syncProfile = Get-JimRunProfile -ConnectedSystem "HR System" -Name "DeltaSync"
Add-JimScheduleStep -ScheduleId $schedule.Id -StepType RunProfile -Configuration @{
    RunProfileId = $syncProfile.Id
}

# Enable the schedule
Enable-JimSchedule -Id $schedule.Id
```

### Files Created
- `src/JIM.PowerShell/Public/Schedules/Get-JIMSchedule.ps1`
- `src/JIM.PowerShell/Public/Schedules/New-JIMSchedule.ps1`
- `src/JIM.PowerShell/Public/Schedules/Set-JIMSchedule.ps1`
- `src/JIM.PowerShell/Public/Schedules/Remove-JIMSchedule.ps1`
- `src/JIM.PowerShell/Public/Schedules/Enable-JIMSchedule.ps1`
- `src/JIM.PowerShell/Public/Schedules/Disable-JIMSchedule.ps1`
- `src/JIM.PowerShell/Public/Schedules/Start-JIMSchedule.ps1`
- `src/JIM.PowerShell/Public/Schedules/Add-JIMScheduleStep.ps1`
- `src/JIM.PowerShell/Public/Schedules/Remove-JIMScheduleStep.ps1`
- `src/JIM.PowerShell/Public/Schedules/Get-JIMScheduleExecution.ps1`
- `src/JIM.PowerShell/Public/Schedules/Stop-JIMScheduleExecution.ps1`

### Files Modified
- `src/JIM.PowerShell/JIM.psd1` - Added 11 new cmdlets to FunctionsToExport

---

## Future Considerations (Post-MVP)

### Conditional Execution
- Only run sync steps if imports detected changes
- Skip export block if no changes flowed to target systems
- Reduces unnecessary processing in quiet periods

### Task Reordering
- Allow admins to reorder queued tasks within a schedule execution
- Insert priority standalone tasks between schedule steps

### Pause/Resume
- Pause a running schedule execution
- Resume from the paused step

### Non-RunProfile Step Types
- **PowerShell**: Execute scripts for pre/post processing
- **Executable**: Run external programs
- **SQL**: Execute database scripts

### Schedule Dependencies
- Schedule B only runs after Schedule A completes
- Useful for: Full sync completes -> trigger downstream notifications

---

## Testing Strategy

### Unit Tests
- Schedule creation/validation
- Step ordering logic
- Cron expression parsing
- Parallel step grouping
- Step progression logic

### Integration Tests
- End-to-end schedule execution
- Parallel step completion tracking
- Failure handling and rollback
- Overlap prevention

### Manual Testing
- Create schedule with mixed sequential/parallel steps
- Verify hierarchical queue view
- Test cancel mid-execution
- Verify history shows complete execution trail

---

## Success Criteria

1. **Schedule Definition**: Users can create schedules with multiple steps
2. **Parallel Execution**: Steps can run in parallel where appropriate
3. **Hierarchical Queue View**: Clear visibility of schedule progress
4. **Timing Control**: Cron-based scheduling with user-friendly UI (no raw cron input required)
5. **Overlap Prevention**: Same schedule doesn't stack up
6. **Audit Trail**: Full execution history via Activities
7. **API & PowerShell**: Automation support

---

## Implementation Order

1. **Phase 1**: Operations page tabbed interface with hierarchical queue view
2. **Phase 2**: Data model (Schedule, ScheduleStep, ScheduleExecution)
3. **Phase 3**: Scheduler service and Worker integration
4. **Phase 4**: Schedules tab UI
5. **Phase 5**: API endpoints
6. **Phase 6**: PowerShell module

**MVP Scope**: Phases 1-5 with RunProfile step type only
**Post-MVP**: PowerShell/Executable/SQL step types, pause/resume, reordering

---

## Verification

### Phase 1
1. `dotnet build && dotnet test` - Pass
2. Operations page shows three tabs
3. Queue tab renders hierarchical view (mock data initially)
4. History tab shows filtered Activities with slide-in panel

### Phase 2-3
1. Create schedule via direct DB/API
2. Scheduler detects and triggers execution
3. Worker processes steps in correct order
4. Parallel steps complete before sequential continues
5. ScheduleExecution tracks progress accurately

### Phase 4-6
1. Full UI workflow: create -> edit -> enable -> run -> monitor -> view history
2. API endpoints accessible via Swagger
3. PowerShell cmdlets functional
