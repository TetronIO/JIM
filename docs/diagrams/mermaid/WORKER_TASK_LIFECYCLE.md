# Worker Task Lifecycle

> Generated against JIM v0.3.0 (`0d1c88e9`). If the codebase has changed significantly since then, these diagrams may be out of date.

This diagram shows how the JIM Worker service picks up, executes, and completes tasks. It covers the main polling loop, task dispatch, heartbeat management, cancellation handling, and housekeeping.

## Worker Main Loop

```mermaid
flowchart TD
    Start([Worker Starts]) --> InitLog[Initialise logging]
    InitLog --> InitDb[Initialise database<br/>Create JimApplication for main loop]

    InitDb --> CancelOrphans[Process orphaned cancellation requests<br/>from previous crash]
    CancelOrphans --> RecoverStale[Recover ALL stale tasks<br/>TimeSpan.Zero = recover immediately<br/>All Processing tasks are orphaned at startup]

    RecoverStale --> MainLoop{Shutdown<br/>requested?}
    MainLoop -->|Yes| ShutdownCancel[Cancel all current tasks<br/>via CancellationTokenSource]
    ShutdownCancel --> End([Worker Stopped])

    MainLoop -->|No| HasTasks{CurrentTasks<br/>count > 0?}

    %% --- Active tasks: heartbeat + cancellation ---
    HasTasks -->|Yes| Heartbeat[Update heartbeats for<br/>all active task IDs]
    Heartbeat --> CheckCancel[Check database for<br/>cancellation requests<br/>matching active task IDs]
    CheckCancel --> AnyCancel{Tasks to<br/>cancel?}
    AnyCancel -->|Yes| DoCancel[For each: trigger CancellationToken<br/>CancelWorkerTaskAsync<br/>Remove from CurrentTasks]
    AnyCancel -->|No| MainLoop
    DoCancel --> MainLoop

    %% --- No active tasks: poll for new work ---
    HasTasks -->|No| PollQueue[GetNextWorkerTasksToProcessAsync<br/>Returns batch of parallel or single sequential task]
    PollQueue --> HasNew{New tasks<br/>found?}
    HasNew -->|No| Housekeeping[Perform housekeeping<br/>See Housekeeping section below]
    Housekeeping --> Sleep[Sleep 2 seconds]
    Sleep --> MainLoop

    HasNew -->|Yes| Dispatch[For each task: spawn Task.Run<br/>with dedicated JimApplication + DbContext]
    Dispatch --> MainLoop
```

## Task Execution (per spawned task)

Each task runs in its own `Task.Run` with an isolated `JimApplication` and `JimDbContext` to avoid EF Core connection sharing issues.

```mermaid
flowchart TD
    Spawned([Task.Run starts]) --> CreateJim[Create dedicated JimApplication<br/>with fresh JimDbContext]
    CreateJim --> ReRetrieve[Re-retrieve WorkerTask<br/>using task-specific JimApplication<br/>Avoid cross-instance issues]
    ReRetrieve --> SetExecuted[Set Activity.Executed = UtcNow]

    SetExecuted --> TaskType{WorkerTask<br/>type?}

    %% --- Sync task ---
    TaskType -->|SynchronisationWorkerTask| ResolveConnector[Resolve connector<br/>LDAP / File / ...]
    ResolveConnector --> ResolveRP[Get RunProfile from<br/>ConnectedSystem.RunProfiles]
    ResolveRP --> RunType{RunProfile<br/>RunType?}

    RunType -->|FullImport| FI[SyncImportTaskProcessor<br/>PerformFullImportAsync]
    RunType -->|DeltaImport| DI[SyncImportTaskProcessor<br/>PerformFullImportAsync<br/>Connector handles delta filtering]
    RunType -->|FullSynchronisation| FS[SyncFullSyncTaskProcessor<br/>PerformFullSyncAsync]
    RunType -->|DeltaSynchronisation| DS[SyncDeltaSyncTaskProcessor<br/>PerformDeltaSyncAsync]
    RunType -->|Export| EX[SyncExportTaskProcessor<br/>PerformExportAsync]

    FI --> CompleteActivity
    DI --> CompleteActivity
    FS --> CompleteActivity
    DS --> CompleteActivity
    EX --> CompleteActivity

    %% --- Data generation task ---
    TaskType -->|DataGenerationTemplate<br/>WorkerTask| DataGen[Execute template<br/>with progress callback]
    DataGen --> DataGenResult{Success?}
    DataGenResult -->|Yes| DataGenComplete[CompleteActivityAsync]
    DataGenResult -->|No| DataGenFail[FailActivityWithErrorAsync]
    DataGenComplete --> CompleteTask
    DataGenFail --> CompleteTask

    %% --- Clear CSOs task ---
    TaskType -->|ClearConnectedSystem<br/>ObjectsWorkerTask| ClearCSOs[ClearConnectedSystemObjectsAsync]
    ClearCSOs --> ClearResult{Success?}
    ClearResult -->|Yes| ClearComplete[CompleteActivityAsync]
    ClearResult -->|No| ClearFail[FailActivityWithErrorAsync]
    ClearComplete --> CompleteTask
    ClearFail --> CompleteTask

    %% --- Delete CS task ---
    TaskType -->|DeleteConnectedSystem<br/>WorkerTask| DeleteCS[ExecuteDeletionAsync<br/>Marks orphaned MVOs<br/>then deletes CS]
    DeleteCS --> DeleteResult{Success?}
    DeleteResult -->|Yes| DeleteComplete[CompleteActivityAsync]
    DeleteResult -->|No| ResetStatus[Reset CS status to Active<br/>for retry]
    ResetStatus --> DeleteFail[FailActivityWithErrorAsync]
    DeleteComplete --> CompleteTask
    DeleteFail --> CompleteTask

    %% --- Activity completion for sync tasks ---
    CompleteActivity[CompleteActivityBasedOnExecutionResultsAsync<br/>Calculate summary stats from RPEIs]
    CompleteActivity --> DetermineStatus{RPEI<br/>error analysis}
    DetermineStatus -->|All RPEIs have errors| FailActivity[FailActivityWithErrorAsync]
    DetermineStatus -->|Some RPEIs have errors| WarnActivity[CompleteActivityWithWarningAsync]
    DetermineStatus -->|No errors| SuccessActivity[CompleteActivityAsync]

    FailActivity --> CompleteTask
    WarnActivity --> CompleteTask
    SuccessActivity --> CompleteTask

    %% --- Sync exception handling ---
    ResolveConnector -.->|Exception| SafeFail[SafeFailActivityAsync<br/>3-level fallback:<br/>1. Normal FailActivity<br/>2. Direct repository update<br/>3. Emergency new DbContext]
    SafeFail --> CompleteTask

    %% --- Task completion ---
    CompleteTask[CompleteWorkerTaskAsync<br/>Delete WorkerTask from database<br/>If scheduled: TryAdvanceScheduleExecution]
    CompleteTask --> RemoveFromList[Remove from CurrentTasks<br/>thread-safe lock]
    RemoveFromList --> Disposed([JimApplication disposed<br/>Database connection released])
```

## Housekeeping (idle time)

Runs every 60 seconds when the worker has no active tasks.

```mermaid
flowchart TD
    Check{Last housekeeping<br/>< 60 seconds ago?}
    Check -->|Yes| Skip([Skip])
    Check -->|No| MvoCleanup[Find MVOs eligible for deletion<br/>Grace period has passed<br/>Max 50 per cycle]
    MvoCleanup --> HasMvos{MVOs<br/>found?}
    HasMvos -->|No| HistoryCheck
    HasMvos -->|Yes| DeleteLoop[For each MVO:<br/>1. Evaluate export rules - create delete exports<br/>2. Delete MVO with original initiator info]
    DeleteLoop --> HistoryCheck

    HistoryCheck{Last history cleanup<br/>< 6 hours ago?}
    HistoryCheck -->|Yes| Done([Done])
    HistoryCheck -->|No| HistoryCleanup[Delete expired change history<br/>CSO changes + MVO changes + Activities<br/>older than retention period - default 90 days]
    HistoryCleanup --> Done
```

## Key Design Decisions

- **Isolated DbContext per task**: Each task gets its own `JimApplication` and `JimDbContext` to avoid EF Core connection sharing issues. The main loop has its own instance for polling and heartbeats.

- **Heartbeat-based liveness**: Active tasks have their heartbeats updated every polling cycle (2 seconds). The scheduler uses heartbeat timestamps to detect crashed workers and recover stale tasks.

- **Startup recovery**: On startup, ALL `Processing` tasks are immediately recovered (re-queued) since the worker just started and nothing can genuinely be processing.

- **Task deletion on completion**: Worker tasks are deleted from the database upon completion (not kept). The Activity record serves as the permanent audit trail.

- **SafeFailActivityAsync**: Three-level fallback ensures activities are never left stuck in `InProgress` status, even if EF tracking is corrupted or the DbContext is disposed.

- **Parallel dispatch**: When `GetNextWorkerTasksToProcessAsync` returns multiple tasks (parallel step group from a schedule), they are all spawned via `Task.Run` simultaneously, each with their own DbContext.
