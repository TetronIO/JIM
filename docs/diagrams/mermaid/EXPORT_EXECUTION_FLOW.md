# Export Execution Flow

> Generated against JIM v0.2.0 (`5a4788e9`). If the codebase has changed significantly since then, these diagrams may be out of date.

This diagram shows how pending exports are executed against connected systems via connectors. The export processor (`SyncExportTaskProcessor`) delegates to the `ExportExecutionServer` for the core execution logic, which supports batching, parallelism, deferred reference resolution, and retry with backoff.

## Export Task Processing

```mermaid
flowchart TD
    Start([PerformExportAsync]) --> CountPE[Count pending exports\nfor connected system]
    CountPE --> HasExports{Pending exports\n> 0?}
    HasExports -->|No| NoWork[Update activity:\nNo exports to process]
    NoWork --> Done([Return])

    HasExports -->|Yes| CheckConnector{Connector supports\nexport?}
    CheckConnector -->|No| FailActivity[FailActivityWithErrorAsync:\nConnector does not support export]
    FailActivity --> Done

    CheckConnector -->|Yes| CheckCancel{Cancellation\nrequested?}
    CheckCancel -->|Yes| CancelMsg[Update activity:\nCancelled before export]
    CancelMsg --> Done

    CheckCancel -->|No| Execute[ExportExecutionServer.ExecuteExportsAsync\nSee Export Execution below]
    Execute --> ProcessResult[ProcessExportResultAsync\nCreate RPEIs for each export:\n- Create --> Provisioned\n- Update --> Exported\n- Delete --> Deprovisioned\n- Failed --> UnhandledError with retry count]

    ProcessResult --> CheckContainers{New containers\ncreated during export?}
    CheckContainers -->|Yes| AutoSelect[Auto-select new containers\nRefresh and select containers\nby created external IDs\nEnsures they appear in future imports]
    CheckContainers -->|No| Done
    AutoSelect --> Done
```

## Export Execution (ExportExecutionServer)

```mermaid
flowchart TD
    Start([ExecuteExportsAsync]) --> GetExecutable[Get executable pending exports\nDatabase filter: Status, NextRetryAt, ErrorCount\nIn-memory filter: has exportable attribute changes\nDelete exports already exported are skipped]
    GetExecutable --> HasExports{Exports\nfound?}
    HasExports -->|No| EmptyResult([Return empty result])

    HasExports -->|Yes| CheckPreview{Run mode =\nPreviewOnly?}
    CheckPreview -->|Yes| PreviewResult[Return export IDs\nwithout executing]
    PreviewResult --> Done([Return result])

    CheckPreview -->|No| ConnectorType{Connector\nexport type?}

    ConnectorType -->|IConnectorExportUsingCalls| PrepareConnector[Inject CertificateProvider\nand CredentialProtection]
    PrepareConnector --> OpenExport[OpenExportConnection\nwith system settings]
    OpenExport --> SplitExports[Split into:\n- Immediate exports: no unresolved references\n- Deferred exports: have unresolved references]

    %% --- Immediate exports ---
    SplitExports --> HasImmediate{Immediate\nexports?}
    HasImmediate -->|Yes| BatchImmediate[Create batches\nof configurable size]
    BatchImmediate --> ParallelCheck{MaxParallelism > 1\nand factories provided?}
    ParallelCheck -->|Yes| ParallelBatch[Process batches in parallel\nEach batch gets own:\n- DbContext\n- Connector instance\nProgress serialised via SemaphoreSlim]
    ParallelCheck -->|No| SequentialBatch[Process batches sequentially\nUsing existing connector + DbContext]

    ParallelBatch --> HasDeferred
    SequentialBatch --> HasDeferred

    HasImmediate -->|No| HasDeferred{Deferred\nexports?}

    %% --- Deferred exports ---
    HasDeferred -->|Yes| BulkFetchRefs[Bulk pre-fetch all\nreferenced CSOs by MVO IDs\nin single query]
    BulkFetchRefs --> ResolveRefs[For each deferred export:\nTry to resolve MVO references\nto target system CSO external IDs]
    ResolveRefs --> Resolved{References\nresolved?}
    Resolved -->|Yes| ExportResolved[Batch export resolved\nexports same as immediate]
    Resolved -->|No| MarkDeferred[Mark as deferred\nWill be retried next run]

    HasDeferred -->|No| CaptureContainers
    ExportResolved --> CaptureContainers
    MarkDeferred --> CaptureContainers

    CaptureContainers[Capture created container\nexternal IDs from connector]
    CaptureContainers --> CloseExport[CloseExportConnection]
    CloseExport --> SecondPass[Second pass: retry deferred\nreferences that may now\nbe resolvable]
    SecondPass --> Done

    ConnectorType -->|IConnectorExportUsingFiles| FileExport[File-based export\nwith batching]
    FileExport --> Done
```

## Batch Execution Detail

Each batch follows this sequence, whether processed sequentially or in parallel:

```mermaid
flowchart TD
    Start([Process batch]) --> MarkExecuting[Mark all exports in batch\nas Status = Executing]
    MarkExecuting --> CallConnector[connector.ExportAsync\nSend batch to connector\nReturns List of ExportResult]
    CallConnector --> ProcessResults[For each export + result pair]
    ProcessResults --> CheckResult{Export\nsucceeded?}

    CheckResult -->|Yes, Create| HandleCreate[Record Provisioned\nCapture new external ID\nfrom ExportResult\nSet Status = Exported]
    CheckResult -->|Yes, Update| HandleUpdate[Record Exported\nSet Status = Exported]
    CheckResult -->|Yes, Delete| HandleDelete[Record Deprovisioned\nDelete pending export\nDelete CSO]
    CheckResult -->|Failed| HandleFail[Increment ErrorCount\nSet error message\nCalculate NextRetryAt\nwith exponential backoff]

    HandleCreate --> Persist
    HandleUpdate --> Persist
    HandleDelete --> Persist
    HandleFail --> CheckMaxRetries{ErrorCount >=\nMaxRetries?}
    CheckMaxRetries -->|Yes| MarkFailed[Set Status = Failed\nPermanent failure\nRequires manual intervention]
    CheckMaxRetries -->|No| SetRetry[Set Status = ExportNotConfirmed\nSet NextRetryAt = backoff time]
    MarkFailed --> Persist
    SetRetry --> Persist

    Persist[Batch persist\nall export status updates]
    Persist --> CaptureItems[Capture ProcessedExportItems\nfor RPEI creation by caller]
    CaptureItems --> Done([Batch complete])
```

## Parallel Batch Architecture

When `MaxParallelism > 1`, batches are distributed across concurrent tasks. Each task is fully isolated to avoid EF Core thread-safety issues.

```
                    +-------------------+
                    |  Export Processor  |
                    |  (caller context) |
                    +---------+---------+
                              |
                    +---------+---------+
                    |  SemaphoreSlim    |
                    |  (MaxParallelism) |
                    +---------+---------+
                              |
              +---------------+---------------+
              |               |               |
     +--------+------+ +-----+-------+ +-----+-------+
     |   Batch 1     | |   Batch 2   | |   Batch 3   |
     | Own DbContext  | | Own DbCtx   | | Own DbCtx   |
     | Own Connector  | | Own Conn    | | Own Conn     |
     | Re-loads PEs   | | Re-loads    | | Re-loads     |
     | by ID from own | | PEs by ID   | | PEs by ID   |
     | context        | |             | |              |
     +-------+--------+ +------+------+ +------+------+
             |                 |                |
             +--------+--------+--------+-------+
                      |                 |
              +-------+------+  +-------+-------+
              | Result Lock  |  | Progress      |
              | (aggregation)|  | Semaphore     |
              | thread-safe  |  | (serialised)  |
              +--------------+  +---------------+
```

- **Batch IDs are captured** before dispatching - each parallel task re-loads its exports from its own DbContext by ID
- **Progress reporting** is serialised via `SemaphoreSlim(1,1)` to protect the caller's shared DbContext
- **Result aggregation** uses a lock for thread-safe counter updates
- **Connector instances** are created per-batch via factory to avoid shared connection state

## Key Design Decisions

- **Two-pass export**: Exports without unresolved references are executed first (immediate). Exports with unresolved MVO references are deferred, with references bulk-resolved in a single query, then executed in a second pass.

- **Retry with backoff**: Failed exports are retried with exponential backoff via `NextRetryAt`. After `MaxRetries` attempts, the export is marked as permanently `Failed`.

- **No-net-change detection**: Before exports are created during sync, the system checks if the target CSO already has the expected values. This happens upstream in `EvaluateExportRulesWithNoNetChangeDetectionAsync`, not during export execution.

- **Container auto-selection**: When exports create new containers (e.g., OUs in LDAP), their external IDs are captured and auto-selected so they appear in future imports without manual configuration.

- **Preview mode**: `SyncRunMode.PreviewOnly` returns the list of exports that would be processed without executing them, enabling dry-run functionality.

- **Per-batch isolation**: Each parallel batch gets its own `DbContext` and connector instance. EF Core is not thread-safe, so sharing a context across batches would cause data corruption.
