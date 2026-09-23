# Schedule Execution Lifecycle

> Last updated: 2026-09-23, JIM v0.15.0

This diagram shows how schedules are triggered, how step groups are queued and advanced, and how the scheduler and worker collaborate to drive multi-step execution to completion.

JIM seeds two built-in Schedules, converging them on every startup (`SeedingServer.BuiltInSchedules`):

- **Temporal Scope Reconciliation** (#85): hourly, cron `0 * * * *`; its single step is of type `TemporalScopeReconciliation` and queues a `TemporalScopeReconciliationWorkerTask`.
- **History Retention Cleanup** (#1118): daily and off-peak, cron `30 2 * * *`; its single step is of type `HistoryRetentionCleanup` and queues a `HistoryRetentionCleanupWorkerTask`, which removes change history, Activities, initial-password records and terminal Pending Password Changes past their retention periods. The Worker reads every retention period from its Service Setting when the task runs, so changing one takes effect on the next pass without the Schedule being touched. This replaces the six-hourly cleanup the Worker used to run during housekeeping.

Both run the same queue-and-advance machinery as any other Schedule; the only difference is the step type they queue (see Step Group Queuing Detail below).

## Three-Service Collaboration

JIM uses three services that collaborate on scheduled execution:

| Service | Role | Polling Interval |
|---------|------|-----------------|
| **JIM.Scheduler** | Detects due schedules, creates executions, queues tasks, recovery | 30 seconds, woken instantly by task-completion notifications |
| **JIM.Worker** | Executes tasks, drives step advancement on completion | 2 seconds |
| **JIM.Web** | Manual run requests (creates worker tasks directly) | On-demand |

The Scheduler does not rely on the 30-second cycle alone: a database trigger publishes a PostgreSQL `NOTIFY` whenever a Worker Task changes, and the Scheduler listens on a dedicated connection. When a task belonging to a Schedule Execution reaches a terminal state, the Scheduler wakes within about half a second and runs its next cycle immediately; the 30-second interval remains as the fallback for missed notifications (#307). The wait is served in 5-second slices, and the Scheduler writes its service heartbeat to the database between them (#1636), so the Operations page does not read a healthy but idle Scheduler as overdue.

## Scheduler Polling Cycle

```mermaid
flowchart TD
    Start([Scheduler Polling Cycle]) --> WaitDb[Wait for the application to be ready<br/>Worker has migrated and seeded<br/>Retry every 2 seconds, writing heartbeat]
    WaitDb --> Listen[Start listening for<br/>Worker Task change notifications]
    Listen --> PollLoop{Shutdown<br/>requested?}

    PollLoop -->|Yes| End([Scheduler Stopped])
    PollLoop -->|No| Heartbeat[Touch /tmp/healthcheck<br/>Write service heartbeat]
    Heartbeat --> Step1[Step 1: Process due schedules<br/>See Due Schedule Processing below<br/>Starting a schedule advances its NextRunTime]

    Step1 --> Step2[Step 2: Give a next run time to any<br/>cron schedule that has none yet<br/>new, newly enabled, or switched from manual]
    Step2 --> Step3[Step 3: Recover stuck executions<br/>Safety net for worker crashes<br/>See Recovery section below]
    Step3 --> Step4[Step 4: Recover stale worker tasks<br/>Heartbeat-based crash detection]
    Step4 --> Sleep[Wait up to 30 seconds<br/>in heartbeat-sized slices<br/>Woken early by task-completion<br/>notifications, then 500 ms settle]
    Sleep --> PollLoop
```

Due schedules are processed before the next-run-time bootstrap, and must stay that way: both read `NextRunTime`, and running the bootstrap first is how cron-triggered Schedules came to be swallowed on the cycle they became due. The bootstrap now only touches Schedules with no `NextRunTime` at all.

## Due Schedule Processing

```mermaid
flowchart TD
    GetDue[Get enabled schedules where<br/>NextRunTime <= UtcNow] --> Loop{More due<br/>schedules?}
    Loop -->|No| Done([Done])
    Loop -->|Yes| CheckOverlap{Active execution<br/>already exists?}

    CheckOverlap -->|Yes| SkipLog[Log warning: schedule<br/>already running, skip]
    SkipLog --> Loop

    CheckOverlap -->|No| HasSteps{Schedule has<br/>any steps?}
    HasSteps -->|No| NoSteps[Log warning, no execution created]
    NoSteps --> CalcNext
    HasSteps -->|Yes| StartExec[StartScheduleExecutionAsync]
    StartExec --> CreateExec[Create ScheduleExecution<br/>Status = InProgress<br/>CurrentStepIndex = 0<br/>TotalSteps = number of step groups]
    CreateExec --> UpdateLastRun[Update Schedule.LastRunTime]

    UpdateLastRun --> QueueAll[Queue ALL step groups upfront]
    QueueAll --> StepLoop{More step<br/>indices?}

    StepLoop -->|Yes| IsFirst{First step<br/>index?}
    IsFirst -->|Yes| QueueQueued[Queue tasks with<br/>Status = Queued<br/>Ready to run immediately]
    IsFirst -->|No| QueueWaiting[Queue tasks with<br/>Status = WaitingForPreviousStep<br/>Visible on queue but blocked]
    QueueQueued --> StepLoop
    QueueWaiting --> StepLoop

    StepLoop -->|No| CalcNext[Calculate and set<br/>next cron run time]
    CalcNext --> Loop

    QueueAll -.->|A step could not be queued| QueueFailed[Execution Status = Failed<br/>ErrorMessage names the step and why<br/>Error logged; NextRunTime is not advanced,<br/>so the schedule is due again next cycle]
    QueueFailed --> Loop
```

## Step Group Queuing Detail

Steps with the same `StepIndex` form a parallel group and execute concurrently.

```mermaid
flowchart TD
    QueueGroup([Queue Step Group<br/>at StepIndex N]) --> GetSteps[Get all steps at this index<br/>May be 1 sequential or many parallel]
    GetSteps --> IsParallel{Multiple steps<br/>at same index?}
    IsParallel -->|Yes| LogParallel[Log parallel group<br/>with step count]
    IsParallel -->|No| QueueStep

    LogParallel --> ForEach{More steps<br/>at index?}
    QueueStep --> ForEach

    ForEach -->|No| Done([Done])
    ForEach -->|Yes| CheckType{Step<br/>type?}

    CheckType -->|RunProfile| CreateSyncTask[Create SynchronisationWorkerTask<br/>Set ConnectedSystemId + RunProfileId<br/>Set ExecutionMode: Parallel/Sequential<br/>Set ContinueOnFailure from step<br/>Link to ScheduleExecution]
    CheckType -->|TemporalScopeReconciliation| CreateTemporalTask[QueueTemporalScopeReconciliationStepAsync<br/>Create TemporalScopeReconciliationWorkerTask<br/>No per-instance configuration<br/>Link to ScheduleExecution]
    CheckType -->|HistoryRetentionCleanup| CreateRetentionTask[QueueHistoryRetentionCleanupStepAsync<br/>Create HistoryRetentionCleanupWorkerTask<br/>No per-instance configuration<br/>Link to ScheduleExecution]
    CheckType -->|PowerShell<br/>Executable<br/>SqlScript| NotImpl[Log warning:<br/>not yet implemented<br/>Skip step]

    CreateSyncTask --> CreateActivity[TaskingServer.CreateWorkerTaskAsync<br/>Creates Activity with initiator triad<br/>and the producing Schedule's identity<br/>Associates Activity with WorkerTask]
    CreateTemporalTask --> CreateActivity
    CreateRetentionTask --> CreateActivity
    CreateActivity --> Created{Task<br/>created?}
    Created -->|Yes| ForEach
    Created -->|No: e.g. the Connected System<br/>is being deleted, or its Run Profile<br/>targets a deselected partition| FailExecution[Mark the execution Failed<br/>with the step name and reason<br/>Stop queueing]
    NotImpl --> ForEach
```

## Worker-Driven Step Advancement

After the worker completes a task, it drives schedule advancement via `TryAdvanceScheduleExecutionAsync`. This is the primary advancement mechanism (the scheduler has a safety net for the case where the worker crashes between task completion and advancement).

```mermaid
flowchart TD
    TaskDone([Worker task completes]) --> DeleteTask[Delete WorkerTask from database<br/>Activity persists as audit record]
    DeleteTask --> IsScheduled{Task linked to<br/>ScheduleExecution?}
    IsScheduled -->|No| Done([Done])
    IsScheduled -->|Yes| CheckRemaining[Count remaining tasks<br/>at this step index]

    CheckRemaining --> StillActive{Remaining<br/>tasks > 0?}
    StillActive -->|Yes| Wait([Wait for other<br/>parallel tasks to finish])

    StillActive -->|No| LastTask[This was the last task<br/>in the step group]
    LastTask --> CheckFailures[Query Activities for this step<br/>Check for FailedWithError<br/>CompleteWithError or Cancelled]

    CheckFailures --> AnyFailed{Any activities<br/>failed?}

    %% --- Happy path ---
    AnyFailed -->|No| FindNext[Find next WaitingForPreviousStep<br/>step index]
    FindNext --> HasNext{Next step<br/>exists?}
    HasNext -->|No| ExecComplete[Execution complete<br/>Status = Complete<br/>CompletedAt = UtcNow]
    ExecComplete --> Done

    HasNext -->|Yes| Advance[Transition next step group:<br/>WaitingForPreviousStep --> Queued<br/>Update CurrentStepIndex]
    Advance --> WorkerPicksUp([Worker picks up<br/>newly queued tasks<br/>on next poll cycle])

    %% --- Failure path ---
    AnyFailed -->|Yes| LoadSteps[Load Schedule Steps<br/>at this index]
    LoadSteps --> CheckContinue{Any step has<br/>ContinueOnFailure<br/>= false?}
    CheckContinue -->|No| FindNext
    CheckContinue -->|Yes| FailExec[Execution failed<br/>Status = Failed<br/>ErrorMessage = step name + reason]
    FailExec --> Cleanup[Delete all remaining<br/>WaitingForPreviousStep tasks]
    Cleanup --> Done
```

## Recovery Mechanisms

Three safety nets ensure schedules complete even when services crash.

```mermaid
flowchart TD
    subgraph "1. Worker Startup Recovery"
        WS([Worker starts]) --> RecoverAll[RecoverStaleWorkerTasksAsync<br/>TimeSpan.Zero<br/>ALL Processing tasks are<br/>orphaned at startup]
        RecoverAll --> ReQueue1[Fail associated Activities<br/>Delete the task rows<br/>Stuck-execution recovery then<br/>advances or fails the execution<br/>per ContinueOnFailure]
    end

    subgraph "2. Scheduler: Stuck Execution Recovery"
        SE([Every 30 seconds]) --> GetActive[Get InProgress executions]
        GetActive --> ForEach{For each<br/>execution}
        ForEach --> CheckTasks{Has Queued or<br/>Processing tasks?}
        CheckTasks -->|Yes| Normal([Normal operation<br/>Worker is handling it])
        CheckTasks -->|No| HasWaiting{Has Waiting<br/>tasks?}
        HasWaiting -->|Yes| SafetyNet[Worker likely crashed after<br/>completing a step<br/>Run CheckAndAdvanceExecutionAsync<br/>to advance to next step]
        HasWaiting -->|No, zero tasks| Complete[No tasks at all<br/>Mark execution complete]
    end

    subgraph "3. Scheduler: Stale Task Recovery"
        ST([Every 30 seconds]) --> FindStale[Find Processing tasks where<br/>Heartbeat older than<br/>stale threshold]
        FindStale --> HasStale{Stale tasks<br/>found?}
        HasStale -->|No| Skip([Skip])
        HasStale -->|Yes| ReQueue2[Fail associated Activities<br/>Delete the stale task rows<br/>freeing the queue]
    end
```

## Execution State Diagram

```mermaid
stateDiagram-v2
    [*] --> InProgress: Scheduler creates execution<br/>Queues all step groups

    InProgress --> InProgress: Worker completes step<br/>Advances to next step group

    InProgress --> Complete: Last step group completes<br/>No more waiting tasks

    InProgress --> Failed: Step group has failures<br/>ContinueOnFailure = false

    InProgress --> Cancelled: User cancels execution<br/>All tasks deleted

    Complete --> [*]
    Failed --> [*]
    Cancelled --> [*]
```

## Step Display Status

The portal's Schedule Execution detail page and `GET /api/v1/schedule-executions/{id}` both show a status per step (`ScheduleExecutionStepStatus`, #1196). It is derived rather than stored, by one shared rule (`ScheduleStepReading.StatusOf`) that the Operations queue's step group header also uses, so the surfaces cannot disagree about a step that is finishing as they are asked:

```mermaid
flowchart TD
    Step([Derive a step's status]) --> HasTask{Worker Task<br/>still exists?}
    HasTask -->|Yes| FromTask[From the task:<br/>Queued, Processing, Cancelling<br/>or Waiting]
    HasTask -->|No| HasActivity{Activity<br/>exists?}
    HasActivity -->|Yes| FromActivity[From the Activity:<br/>Processing, Completed,<br/>Completed with Warning,<br/>Completed with Error,<br/>Failed or Cancelled]
    HasActivity -->|No| Position{Step index vs<br/>CurrentStepIndex}
    Position -->|Earlier| Completed[Completed]
    Position -->|Current, execution<br/>InProgress| Waiting[Waiting]
    Position -->|Otherwise| Pending[Pending<br/>never reached, or will not run]
```

A live Worker Task wins because its Activity is necessarily still in progress; the Activity is the durable record once the task has been deleted. Each Activity a Schedule produces also carries the Schedule's id and name, denormalised when the task is queued, so its attribution survives the Schedule being deleted.

## Example: Multi-Step Schedule

A typical schedule with sequential and parallel steps:

**Schedule: "Nightly HR Sync"**

| Index | Steps | Execution |
|-------|-------|-----------|
| 0 | HR System - Full Import | Sequential |
| 1 | HR System - Full Sync | Sequential |
| 2 | AD - Export, LDAP - Export | Parallel (2 tasks) |
| 3 | AD - Confirming Import, LDAP - Confirming Import | Parallel (2 tasks) |

**Timeline:**

1. Scheduler creates execution, queues ALL 6 tasks
   - Index 0: 1 task as Queued
   - Index 1: 1 task as WaitingForPreviousStep
   - Index 2: 2 tasks as WaitingForPreviousStep
   - Index 3: 2 tasks as WaitingForPreviousStep
2. Worker picks up index 0 task, executes Full Import
3. Worker completes → TryAdvance → transitions index 1 to Queued
4. Worker picks up index 1 task, executes Full Sync
5. Worker completes → TryAdvance → transitions index 2 (2 tasks) to Queued
6. Worker dispatches BOTH index 2 tasks in parallel (AD Export + LDAP Export)
7. First export completes → TryAdvance → remaining count > 0, wait
8. Second export completes → TryAdvance → transitions index 3 to Queued
9. Worker dispatches BOTH index 3 tasks in parallel
10. Both confirming imports complete → TryAdvance → no more steps
11. Execution marked Complete

## Key Design Decisions

- **All steps queued upfront**<br /> The scheduler creates all worker tasks at execution start, with subsequent steps as `WaitingForPreviousStep`. This makes the full execution plan visible in the task queue from the beginning.

- **Worker drives advancement**<br /> Step transitions are driven by the worker (via `TryAdvanceScheduleExecutionAsync`) for minimal latency. The scheduler provides a safety net for crash recovery only.

- **Activity-based outcome detection**<br /> Since worker tasks are deleted upon completion, the system uses Activities (immutable audit records) to determine whether a step succeeded or failed.

- **Overlap prevention**<br /> The scheduler checks for active executions before starting a new one for the same schedule. This prevents concurrent execution of the same schedule.

- **ContinueOnFailure**<br /> Each step can be configured to continue or halt on failure. When any step at an index has `ContinueOnFailure = false` and its activity failed, the entire execution stops and remaining waiting tasks are cleaned up.
