# Worker Task Lifecycle

> Last updated: 2026-04-01 — JIM v0.8.0

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

    MainLoop -->|No| TouchHealth[Write UTC timestamp to<br/>/tmp/healthcheck file<br/>Docker healthcheck monitors file age]
    TouchHealth --> HasTasks{CurrentTasks<br/>count > 0?}

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

Each task runs in its own `Task.Run` with an isolated `JimApplication`, `JimDbContext`, `ISyncRepository`, and `ISyncServer` to avoid EF Core connection sharing issues. Sync/delta sync processors also receive a stateless `ISyncEngine` for pure domain decisions.

```mermaid
flowchart TD
    Spawned([Task.Run starts]) --> CreateJim[Create dedicated JimApplication<br/>with fresh JimDbContext<br/>Create ISyncRepository + ISyncServer +<br/>ISyncEngine scoped to this task]
    CreateJim --> ReRetrieve[Re-retrieve WorkerTask<br/>using task-specific JimApplication<br/>Avoid cross-instance issues]
    ReRetrieve --> SetExecuted[Set Activity.Executed = UtcNow]

    SetExecuted --> TaskType{WorkerTask<br/>type?}

    %% --- Sync task ---
    TaskType -->|SynchronisationWorkerTask| ResolveConnector[Resolve connector<br/>LDAP / File / ...]
    ResolveConnector --> ResolveRP[Get RunProfile from<br/>ConnectedSystem.RunProfiles]
    ResolveRP --> RunType{RunProfile<br/>RunType?}

    RunType -->|FullImport| FI[SyncImportTaskProcessor<br/>PerformFullImportAsync<br/>Uses ISyncEngine + ISyncServer + ISyncRepository]
    RunType -->|DeltaImport| DI[SyncImportTaskProcessor<br/>PerformFullImportAsync<br/>Connector handles delta filtering<br/>Uses ISyncEngine + ISyncServer + ISyncRepository]
    RunType -->|FullSynchronisation| FS[SyncFullSyncTaskProcessor<br/>PerformFullSyncAsync<br/>Uses ISyncEngine + ISyncServer + ISyncRepository]
    RunType -->|DeltaSynchronisation| DS[SyncDeltaSyncTaskProcessor<br/>PerformDeltaSyncAsync<br/>Uses ISyncEngine + ISyncServer + ISyncRepository]
    RunType -->|Export| EX[SyncExportTaskProcessor<br/>PerformExportAsync<br/>Uses ISyncServer + ISyncRepository]

    FI --> CompleteActivity
    DI --> CompleteActivity
    FS --> CompleteActivity
    DS --> CompleteActivity
    EX --> CompleteActivity

    %% --- Data generation task ---
    TaskType -->|ExampleDataTemplate<br/>WorkerTask| DataGen[Execute template<br/>with progress callback]
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

## Docker Healthcheck (#185)

Both Worker and Scheduler write a heartbeat file each main-loop iteration. Docker's `HEALTHCHECK` instruction compares the file's modification timestamp against a staleness threshold.

```mermaid
flowchart LR
    Loop([Main loop iteration]) --> WriteFile["Write DateTime.UtcNow to<br/>/tmp/healthcheck"]
    WriteFile --> NextIteration([Continue loop])

    Docker([Docker HEALTHCHECK<br/>every 30s]) --> StatFile["stat -c %Y /tmp/healthcheck"]
    StatFile --> Compare{"(now - mtime)<br/>< threshold?"}
    Compare -->|Yes| Healthy([Container healthy])
    Compare -->|No| Unhealthy([Container unhealthy<br/>Docker restarts after retries])
```

| Service   | Staleness threshold | Start period | Rationale                                  |
|-----------|--------------------:|-----------:|----------------------------------------------|
| Worker    | 60 s                | 60 s       | 2 s polling cycle; 60 s tolerates brief stalls |
| Scheduler | 120 s               | 120 s      | Longer cycle; waits for application readiness  |

## Key Design Decisions

- **Three-layer sync DI architecture (#394)**: Worker processors use three collaborating interfaces injected at task spawn time:
  - **ISyncEngine** — Pure domain logic (projection decisions, attribute flow, deletion rules, export confirmation). Stateless, synchronous, zero-dependency, I/O-free, fully unit-testable. 8 methods covering projection, attribute flow, export confirmation, deletion rules, and reconciliation. Used by import, full sync, and delta sync processors.
  - **ISyncServer** — Orchestration facade that delegates to existing application-layer servers (ExportEvaluationServer, ExportExecutionServer, ScopingEvaluationServer, DriftDetectionService) and ISyncRepository. All processors use this.
  - **ISyncRepository** — Dedicated data access boundary for sync operations (bulk CSO/MVO writes, pending exports, RPEIs). Replaces scattered access through multiple server properties.

- **Per-task DI scope (#394)**: Each spawned task gets its own `JimApplication` (via `IJimApplicationFactory.Create()`), `JimDbContext`, `ISyncRepository`, `ISyncServer`, and `ISyncEngine` — fully isolated from the main loop and other tasks. This avoids EF Core connection sharing issues and ensures each task can be disposed independently. The main loop has its own instance for polling and heartbeats.

- **Heartbeat-based liveness (two levels)**:
  - **Task-level**: Active tasks have their database heartbeats updated every polling cycle (2 seconds). The scheduler uses heartbeat timestamps to detect crashed workers and recover stale tasks.
  - **Container-level (#185)**: The main loop writes a UTC timestamp to `/tmp/healthcheck` each iteration. Docker's `HEALTHCHECK` instruction compares file age against a staleness threshold (60 s for Worker, 120 s for Scheduler) to detect stalled service loops and trigger container restarts.

- **Startup recovery**: On startup, ALL `Processing` tasks are immediately recovered (re-queued) since the worker just started and nothing can genuinely be processing.

- **Task deletion on completion**: Worker tasks are deleted from the database upon completion (not kept). The Activity record serves as the permanent audit trail.

- **SafeFailActivityAsync**: Three-level fallback ensures activities are never left stuck in `InProgress` status, even if EF tracking is corrupted or the DbContext is disposed.

- **Parallel dispatch**: When `GetNextWorkerTasksToProcessAsync` returns multiple tasks (parallel step group from a schedule), they are all spawned via `Task.Run` simultaneously, each with their own DbContext.
