# Worker Task Lifecycle

This diagram shows how the JIM Worker service picks up, executes, and completes tasks. It covers the main polling loop, task dispatch, heartbeat management, cancellation handling, and housekeeping.

## Worker Main Loop

```mermaid
flowchart TD
    Start([Worker Starts]) --> InitLog[Initialise logging]
    InitLog --> InitDb[Initialise database\nCreate JimApplication for main loop]

    InitDb --> CancelOrphans[Process orphaned cancellation requests\nfrom previous crash]
    CancelOrphans --> RecoverStale[Recover ALL stale tasks\nTimeSpan.Zero = recover immediately\nAll Processing tasks are orphaned at startup]

    RecoverStale --> MainLoop{Shutdown\nrequested?}
    MainLoop -->|Yes| ShutdownCancel[Cancel all current tasks\nvia CancellationTokenSource]
    ShutdownCancel --> End([Worker Stopped])

    MainLoop -->|No| HasTasks{CurrentTasks\ncount > 0?}

    %% --- Active tasks: heartbeat + cancellation ---
    HasTasks -->|Yes| Heartbeat[Update heartbeats for\nall active task IDs]
    Heartbeat --> CheckCancel[Check database for\ncancellation requests\nmatching active task IDs]
    CheckCancel --> AnyCancel{Tasks to\ncancel?}
    AnyCancel -->|Yes| DoCancel[For each: trigger CancellationToken\nCancelWorkerTaskAsync\nRemove from CurrentTasks]
    AnyCancel -->|No| MainLoop
    DoCancel --> MainLoop

    %% --- No active tasks: poll for new work ---
    HasTasks -->|No| PollQueue[GetNextWorkerTasksToProcessAsync\nReturns batch of parallel or single sequential task]
    PollQueue --> HasNew{New tasks\nfound?}
    HasNew -->|No| Housekeeping[Perform housekeeping\nSee Housekeeping section below]
    Housekeeping --> Sleep[Sleep 2 seconds]
    Sleep --> MainLoop

    HasNew -->|Yes| Dispatch[For each task: spawn Task.Run\nwith dedicated JimApplication + DbContext]
    Dispatch --> MainLoop
```

## Task Execution (per spawned task)

Each task runs in its own `Task.Run` with an isolated `JimApplication` and `JimDbContext` to avoid EF Core connection sharing issues.

```mermaid
flowchart TD
    Spawned([Task.Run starts]) --> CreateJim[Create dedicated JimApplication\nwith fresh JimDbContext]
    CreateJim --> ReRetrieve[Re-retrieve WorkerTask\nusing task-specific JimApplication\nAvoid cross-instance issues]
    ReRetrieve --> SetExecuted[Set Activity.Executed = UtcNow]

    SetExecuted --> TaskType{WorkerTask\ntype?}

    %% --- Sync task ---
    TaskType -->|SynchronisationWorkerTask| ResolveConnector[Resolve connector\nLDAP / File / ...]
    ResolveConnector --> ResolveRP[Get RunProfile from\nConnectedSystem.RunProfiles]
    ResolveRP --> RunType{RunProfile\nRunType?}

    RunType -->|FullImport| FI[SyncImportTaskProcessor\nPerformFullImportAsync]
    RunType -->|DeltaImport| DI[SyncImportTaskProcessor\nPerformFullImportAsync\nConnector handles delta filtering]
    RunType -->|FullSynchronisation| FS[SyncFullSyncTaskProcessor\nPerformFullSyncAsync]
    RunType -->|DeltaSynchronisation| DS[SyncDeltaSyncTaskProcessor\nPerformDeltaSyncAsync]
    RunType -->|Export| EX[SyncExportTaskProcessor\nPerformExportAsync]

    FI --> CompleteActivity
    DI --> CompleteActivity
    FS --> CompleteActivity
    DS --> CompleteActivity
    EX --> CompleteActivity

    %% --- Data generation task ---
    TaskType -->|DataGenerationTemplate\nWorkerTask| DataGen[Execute template\nwith progress callback]
    DataGen --> DataGenResult{Success?}
    DataGenResult -->|Yes| DataGenComplete[CompleteActivityAsync]
    DataGenResult -->|No| DataGenFail[FailActivityWithErrorAsync]
    DataGenComplete --> CompleteTask
    DataGenFail --> CompleteTask

    %% --- Clear CSOs task ---
    TaskType -->|ClearConnectedSystem\nObjectsWorkerTask| ClearCSOs[ClearConnectedSystemObjectsAsync]
    ClearCSOs --> ClearResult{Success?}
    ClearResult -->|Yes| ClearComplete[CompleteActivityAsync]
    ClearResult -->|No| ClearFail[FailActivityWithErrorAsync]
    ClearComplete --> CompleteTask
    ClearFail --> CompleteTask

    %% --- Delete CS task ---
    TaskType -->|DeleteConnectedSystem\nWorkerTask| DeleteCS[ExecuteDeletionAsync\nMarks orphaned MVOs\nthen deletes CS]
    DeleteCS --> DeleteResult{Success?}
    DeleteResult -->|Yes| DeleteComplete[CompleteActivityAsync]
    DeleteResult -->|No| ResetStatus[Reset CS status to Active\nfor retry]
    ResetStatus --> DeleteFail[FailActivityWithErrorAsync]
    DeleteComplete --> CompleteTask
    DeleteFail --> CompleteTask

    %% --- Activity completion for sync tasks ---
    CompleteActivity[CompleteActivityBasedOnExecutionResultsAsync\nCalculate summary stats from RPEIs]
    CompleteActivity --> DetermineStatus{RPEI\nerror analysis}
    DetermineStatus -->|All RPEIs have errors| FailActivity[FailActivityWithErrorAsync]
    DetermineStatus -->|Some RPEIs have errors| WarnActivity[CompleteActivityWithWarningAsync]
    DetermineStatus -->|No errors| SuccessActivity[CompleteActivityAsync]

    FailActivity --> CompleteTask
    WarnActivity --> CompleteTask
    SuccessActivity --> CompleteTask

    %% --- Sync exception handling ---
    ResolveConnector -.->|Exception| SafeFail[SafeFailActivityAsync\n3-level fallback:\n1. Normal FailActivity\n2. Direct repository update\n3. Emergency new DbContext]
    SafeFail --> CompleteTask

    %% --- Task completion ---
    CompleteTask[CompleteWorkerTaskAsync\nDelete WorkerTask from database\nIf scheduled: TryAdvanceScheduleExecution]
    CompleteTask --> RemoveFromList[Remove from CurrentTasks\nthread-safe lock]
    RemoveFromList --> Disposed([JimApplication disposed\nDatabase connection released])
```

## Housekeeping (idle time)

Runs every 60 seconds when the worker has no active tasks.

```mermaid
flowchart TD
    Check{Last housekeeping\n< 60 seconds ago?}
    Check -->|Yes| Skip([Skip])
    Check -->|No| MvoCleanup[Find MVOs eligible for deletion\nGrace period has passed\nMax 50 per cycle]
    MvoCleanup --> HasMvos{MVOs\nfound?}
    HasMvos -->|No| HistoryCheck
    HasMvos -->|Yes| DeleteLoop[For each MVO:\n1. Evaluate export rules - create delete exports\n2. Delete MVO with original initiator info]
    DeleteLoop --> HistoryCheck

    HistoryCheck{Last history cleanup\n< 6 hours ago?}
    HistoryCheck -->|Yes| Done([Done])
    HistoryCheck -->|No| HistoryCleanup[Delete expired change history\nCSO changes + MVO changes + Activities\nolder than retention period - default 90 days]
    HistoryCleanup --> Done
```

## Key Design Decisions

- **Isolated DbContext per task**: Each task gets its own `JimApplication` and `JimDbContext` to avoid EF Core connection sharing issues. The main loop has its own instance for polling and heartbeats.

- **Heartbeat-based liveness**: Active tasks have their heartbeats updated every polling cycle (2 seconds). The scheduler uses heartbeat timestamps to detect crashed workers and recover stale tasks.

- **Startup recovery**: On startup, ALL `Processing` tasks are immediately recovered (re-queued) since the worker just started and nothing can genuinely be processing.

- **Task deletion on completion**: Worker tasks are deleted from the database upon completion (not kept). The Activity record serves as the permanent audit trail.

- **SafeFailActivityAsync**: Three-level fallback ensures activities are never left stuck in `InProgress` status, even if EF tracking is corrupted or the DbContext is disposed.

- **Parallel dispatch**: When `GetNextWorkerTasksToProcessAsync` returns multiple tasks (parallel step group from a schedule), they are all spawned via `Task.Run` simultaneously, each with their own DbContext.
