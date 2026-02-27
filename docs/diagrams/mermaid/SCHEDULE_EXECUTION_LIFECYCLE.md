# Schedule Execution Lifecycle

> Generated against JIM v0.3.0 (`0d1c88e9`). If the codebase has changed significantly since then, these diagrams may be out of date.

This diagram shows how schedules are triggered, how step groups are queued and advanced, and how the scheduler and worker collaborate to drive multi-step execution to completion.

## Three-Service Collaboration

JIM uses three services that collaborate on scheduled execution:

| Service | Role | Polling Interval |
|---------|------|-----------------|
| **JIM.Scheduler** | Detects due schedules, creates executions, queues tasks, recovery | 30 seconds |
| **JIM.Worker** | Executes tasks, drives step advancement on completion | 2 seconds |
| **JIM.Web** | Manual run requests (creates worker tasks directly) | On-demand |

## Scheduler Polling Cycle

```mermaid
flowchart TD
    Start([Scheduler Polling Cycle]) --> WaitDb[Wait for database to be ready<br/>Retry every 2 seconds]
    WaitDb --> PollLoop{Shutdown<br/>requested?}

    PollLoop -->|Yes| End([Scheduler Stopped])
    PollLoop -->|No| Step1[Step 1: Update cron next-run-times<br/>Parse cron expressions<br/>Set NextRunTime on schedules]

    Step1 --> Step2[Step 2: Process due schedules<br/>See Due Schedule Processing below]
    Step2 --> Step3[Step 3: Recover stuck executions<br/>Safety net for worker crashes<br/>See Recovery section below]
    Step3 --> Step4[Step 4: Recover stale worker tasks<br/>Heartbeat-based crash detection]
    Step4 --> Sleep[Sleep 30 seconds]
    Sleep --> PollLoop
```

## Due Schedule Processing

```mermaid
flowchart TD
    GetDue[Get schedules where<br/>NextRunTime <= UtcNow] --> Loop{More due<br/>schedules?}
    Loop -->|No| Done([Done])
    Loop -->|Yes| CheckOverlap{Active execution<br/>already exists?}

    CheckOverlap -->|Yes| SkipLog[Log warning: schedule<br/>already running, skip]
    SkipLog --> Loop

    CheckOverlap -->|No| StartExec[StartScheduleExecutionAsync]
    StartExec --> CreateExec[Create ScheduleExecution<br/>Status = InProgress<br/>CurrentStepIndex = 0]
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
    CheckType -->|PowerShell<br/>Executable<br/>SqlScript| NotImpl[Log warning:<br/>not yet implemented<br/>Skip step]

    CreateSyncTask --> CreateActivity[TaskingServer.CreateWorkerTaskAsync<br/>Creates Activity with initiator triad<br/>Associates Activity with WorkerTask]
    CreateActivity --> ForEach
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
    HasNext -->|No| ExecComplete[Execution complete<br/>Status = Completed<br/>CompletedAt = UtcNow]
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
        WS([Worker starts]) --> RecoverAll[RecoverStaleWorkerTasksAsync<br/>TimeSpan.Zero<br/>ALL Processing tasks are<br/>orthaned at startup]
        RecoverAll --> ReQueue1[Re-queue as Queued<br/>Fail associated Activities]
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
        HasStale -->|Yes| ReQueue2[Re-queue stale tasks<br/>Fail associated Activities<br/>Worker will pick up<br/>on next poll]
    end
```

## Execution State Diagram

```mermaid
stateDiagram-v2
    [*] --> InProgress: Scheduler creates execution<br/>Queues all step groups

    InProgress --> InProgress: Worker completes step<br/>Advances to next step group

    InProgress --> Completed: Last step group completes<br/>No more waiting tasks

    InProgress --> Failed: Step group has failures<br/>ContinueOnFailure = false

    InProgress --> Cancelled: User cancels execution<br/>All tasks deleted

    Completed --> [*]
    Failed --> [*]
    Cancelled --> [*]
```

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
11. Execution marked Completed

## Key Design Decisions

- **All steps queued upfront**: The scheduler creates all worker tasks at execution start, with subsequent steps as `WaitingForPreviousStep`. This makes the full execution plan visible in the task queue from the beginning.

- **Worker drives advancement**: Step transitions are driven by the worker (via `TryAdvanceScheduleExecutionAsync`) for minimal latency. The scheduler provides a safety net for crash recovery only.

- **Activity-based outcome detection**: Since worker tasks are deleted upon completion, the system uses Activities (immutable audit records) to determine whether a step succeeded or failed.

- **Overlap prevention**: The scheduler checks for active executions before starting a new one for the same schedule. This prevents concurrent execution of the same schedule.

- **ContinueOnFailure**: Each step can be configured to continue or halt on failure. When any step at an index has `ContinueOnFailure = false` and its activity failed, the entire execution stops and remaining waiting tasks are cleaned up.
