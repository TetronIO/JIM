# Export Execution Flow

> Generated against JIM v0.3.0 (`0d1c88e9`). If the codebase has changed significantly since then, these diagrams may be out of date.

This diagram shows how pending exports are executed against connected systems via connectors. The export processor (`SyncExportTaskProcessor`) delegates to the `ExportExecutionServer` for the core execution logic, which supports batching, parallelism, deferred reference resolution, and retry with backoff.

## Export Task Processing

```mermaid
flowchart TD
    Start([PerformExportAsync]) --> CountPE[Count pending exports<br/>for connected system]
    CountPE --> HasExports{Pending exports<br/>> 0?}
    HasExports -->|No| NoWork[Update activity:<br/>No exports to process]
    NoWork --> Done([Return])

    HasExports -->|Yes| CheckConnector{Connector supports<br/>export?}
    CheckConnector -->|No| FailActivity[FailActivityWithErrorAsync:<br/>Connector does not support export]
    FailActivity --> Done

    CheckConnector -->|Yes| CheckCancel{Cancellation<br/>requested?}
    CheckCancel -->|Yes| CancelMsg[Update activity:<br/>Cancelled before export]
    CancelMsg --> Done

    CheckCancel -->|No| Execute[ExportExecutionServer.ExecuteExportsAsync<br/>See Export Execution below]
    Execute --> ProcessResult[ProcessExportResultAsync<br/>Create RPEIs for each export:<br/>- Create --> Exported<br/>- Update --> Exported<br/>- Delete --> Deprovisioned<br/>- Failed --> UnhandledError with retry count]

    ProcessResult --> CheckContainers{New containers<br/>created during export?}
    CheckContainers -->|Yes| AutoSelect[Auto-select new containers<br/>Refresh and select containers<br/>by created external IDs<br/>Ensures they appear in future imports]
    CheckContainers -->|No| Done
    AutoSelect --> Done
```

## Export Execution (ExportExecutionServer)

```mermaid
flowchart TD
    Start([ExecuteExportsAsync]) --> GetExecutable[Get executable pending exports<br/>Database filter: Status, NextRetryAt, ErrorCount<br/>In-memory filter: has exportable attribute changes<br/>Delete exports already exported are skipped]
    GetExecutable --> HasExports{Exports<br/>found?}
    HasExports -->|No| EmptyResult([Return empty result])

    HasExports -->|Yes| CheckPreview{Run mode =<br/>PreviewOnly?}
    CheckPreview -->|Yes| PreviewResult[Return export IDs<br/>without executing]
    PreviewResult --> Done([Return result])

    CheckPreview -->|No| ConnectorType{Connector<br/>export type?}

    ConnectorType -->|IConnectorExportUsingCalls| PrepareConnector[Inject CertificateProvider<br/>and CredentialProtection]
    PrepareConnector --> OpenExport[OpenExportConnection<br/>with system settings]
    OpenExport --> SplitExports[Split into:<br/>- Immediate exports: no unresolved references<br/>- Deferred exports: have unresolved references]

    %% --- Immediate exports ---
    SplitExports --> HasImmediate{Immediate<br/>exports?}
    HasImmediate -->|Yes| BatchImmediate[Create batches<br/>of configurable size]
    BatchImmediate --> ParallelCheck{MaxParallelism > 1<br/>and factories provided?}
    ParallelCheck -->|Yes| ParallelBatch[Process batches in parallel<br/>Each batch gets own:<br/>- DbContext<br/>- Connector instance<br/>Progress serialised via SemaphoreSlim]
    ParallelCheck -->|No| SequentialBatch[Process batches sequentially<br/>Using existing connector + DbContext]

    ParallelBatch --> HasDeferred
    SequentialBatch --> HasDeferred

    HasImmediate -->|No| HasDeferred{Deferred<br/>exports?}

    %% --- Deferred exports ---
    HasDeferred -->|Yes| BulkFetchRefs[Bulk pre-fetch all<br/>referenced CSOs by MVO IDs<br/>in single query]
    BulkFetchRefs --> ResolveRefs[For each deferred export:<br/>Try to resolve MVO references<br/>to target system CSO external IDs]
    ResolveRefs --> Resolved{References<br/>resolved?}
    Resolved -->|Yes| ExportResolved[Batch export resolved<br/>exports same as immediate]
    Resolved -->|No| MarkDeferred[Mark as deferred<br/>Will be retried next run]

    HasDeferred -->|No| CaptureContainers
    ExportResolved --> CaptureContainers
    MarkDeferred --> CaptureContainers

    CaptureContainers[Capture created container<br/>external IDs from connector]
    CaptureContainers --> CloseExport[CloseExportConnection]
    CloseExport --> SecondPass[Second pass: retry deferred<br/>references that may now<br/>be resolvable]
    SecondPass --> Done

    ConnectorType -->|IConnectorExportUsingFiles| FileExport[File-based export<br/>with batching]
    FileExport --> Done
```

## Batch Execution Detail

Each batch follows this sequence, whether processed sequentially or in parallel:

```mermaid
flowchart TD
    Start([Process batch]) --> MarkExecuting[Mark all exports in batch<br/>as Status = Executing]
    MarkExecuting --> CallConnector[connector.ExportAsync<br/>Send batch to connector<br/>Returns List of ExportResult]
    CallConnector --> ProcessResults[For each export + result pair]
    ProcessResults --> CheckResult{Export<br/>succeeded?}

    CheckResult -->|Yes, Create| HandleCreate[Record Exported<br/>Capture new external ID<br/>from ExportResult<br/>Set Status = Exported]
    CheckResult -->|Yes, Update| HandleUpdate[Record Exported<br/>Set Status = Exported]
    CheckResult -->|Yes, Delete| HandleDelete[Record Deprovisioned<br/>Delete pending export<br/>Delete CSO]
    CheckResult -->|Failed| HandleFail[Increment ErrorCount<br/>Set error message<br/>Calculate NextRetryAt<br/>with exponential backoff]

    HandleCreate --> Persist
    HandleUpdate --> Persist
    HandleDelete --> Persist
    HandleFail --> CheckMaxRetries{ErrorCount >=<br/>MaxRetries?}
    CheckMaxRetries -->|Yes| MarkFailed[Set Status = Failed<br/>Permanent failure<br/>Requires manual intervention]
    CheckMaxRetries -->|No| SetRetry[Set Status = ExportNotConfirmed<br/>Set NextRetryAt = backoff time]
    MarkFailed --> Persist
    SetRetry --> Persist

    Persist[Batch persist<br/>all export status updates]
    Persist --> CaptureItems[Capture ProcessedExportItems<br/>for RPEI creation by caller]
    CaptureItems --> Done([Batch complete])
```

## LDAP Export Consolidation and Chunking

For LDAP connectors, individual attribute changes are consolidated and chunked before being sent to the directory server. This ensures RFC 4511 compliance and prevents server rejection of oversized modify requests.

```mermaid
flowchart TD
    Input([Pending Export<br/>with attribute changes]) --> Consolidate[ConsolidateModifications:<br/>Group changes by attribute name<br/>and operation type]

    Consolidate --> Example1[Example: 200 individual<br/>member Add changes]
    Example1 --> Merged[Consolidated into single<br/>DirectoryAttributeModification<br/>with 200 values]

    Merged --> CheckSize{Values ><br/>batch size?}
    CheckSize -->|No| SingleRequest[Single ModifyRequest<br/>with all values]
    CheckSize -->|Yes| Chunk[ChunkModifyRequests:<br/>Split into batches<br/>of configurable size<br/>Default: 100]
    Chunk --> MultiRequest[Multiple ModifyRequests<br/>sent sequentially<br/>e.g., 2 requests of 100 values]

    SingleRequest --> Send([Send to LDAP server])
    MultiRequest --> Send
```

**Batch size** is configurable via the "Modify Batch Size" connector setting (default: 100, range: 10-5000).

## Parallel Batch Architecture

When `MaxParallelism > 1`, batches are distributed across concurrent tasks. Each task is fully isolated to avoid EF Core thread-safety issues.

```mermaid
flowchart TD
    Caller[Export Processor<br/>caller context] --> Semaphore[SemaphoreSlim<br/>MaxParallelism]

    Semaphore --> B1[Batch 1<br/>Own DbContext<br/>Own Connector<br/>Re-loads PEs by ID]
    Semaphore --> B2[Batch 2<br/>Own DbContext<br/>Own Connector<br/>Re-loads PEs by ID]
    Semaphore --> B3[Batch N<br/>Own DbContext<br/>Own Connector<br/>Re-loads PEs by ID]

    B1 --> ResultLock[Result Lock<br/>thread-safe aggregation]
    B2 --> ResultLock
    B3 --> ResultLock

    B1 --> ProgressSem[Progress Semaphore<br/>serialised via SemaphoreSlim 1,1<br/>protects caller DbContext]
    B2 --> ProgressSem
    B3 --> ProgressSem
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

- **LDAP consolidation**: Multiple changes to the same attribute with the same operation type (e.g., 200 individual "member Add" operations) are consolidated into a single `DirectoryAttributeModification` before sending to the directory server. This is the correct RFC 4511 pattern and dramatically reduces the number of LDAP modify requests.

- **LDAP chunking**: Consolidated modifications that exceed the configurable batch size (default: 100) are split into multiple `ModifyRequest` objects sent sequentially. This prevents LDAP server rejection of oversized requests — important for large group membership changes.
