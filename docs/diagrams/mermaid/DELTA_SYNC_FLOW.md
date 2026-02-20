# Delta Sync Flow

> Generated against JIM v0.2.0 (`988472e3`). If the codebase has changed significantly since then, these diagrams may be out of date.

This diagram shows how Delta Synchronisation differs from Full Synchronisation. Both use identical per-CSO processing logic; the only difference is CSO selection and a few lifecycle steps.

## Full Sync vs Delta Sync Comparison

| Aspect | Full Sync | Delta Sync |
|--------|-----------|------------|
| CSO Selection | ALL CSOs | Only CSOs with `LastUpdated > watermark` |
| Early Exit | Never | Yes, if 0 modified CSOs |
| Pending Export Surfacing | Yes (creates RPEIs for operator visibility) | No |
| Per-page pipeline | Identical | Identical |
| Watermark Update | Yes | Yes (even when 0 changes) |
| Use Case | Initial sync, periodic reconciliation | Incremental updates |

## Delta Sync Flow

```mermaid
flowchart TD
    Start([PerformDeltaSyncAsync]) --> Watermark[Determine watermark:<br/>LastDeltaSyncCompletedAt<br/>or DateTime.MinValue if first run]

    Watermark --> CountModified[Count CSOs modified<br/>since watermark]
    CountModified --> HasChanges{Modified<br/>CSOs > 0?}

    HasChanges -->|No| EarlyWatermark[Update watermark<br/>to UtcNow]
    EarlyWatermark --> EarlyDone([Return - no work needed])

    HasChanges -->|Yes| CountPE[Count pending exports<br/>Total = modified CSOs + PEs]
    CountPE --> LoadCaches[Load sync rules, object types<br/>Drift detection cache<br/>Pending exports dictionary<br/>Export evaluation cache]

    LoadCaches --> PageLoop{More CSO<br/>pages?}

    PageLoop -->|Yes| LoadPage[Load page of modified CSOs<br/>WHERE LastUpdated > watermark]
    LoadPage --> CsoLoop{More CSOs<br/>in page?}

    CsoLoop -->|Yes| CheckCancel{Cancellation<br/>requested?}
    CheckCancel -->|Yes| Return([Return])
    CheckCancel -->|No| ProcessCso[ProcessConnectedSystemObjectAsync<br/>Identical to Full Sync:<br/>confirm PEs, join, project,<br/>attribute flow, drift detection]
    ProcessCso --> CsoLoop

    CsoLoop -->|No| PageFlush[Page flush pipeline:<br/>1. Deferred reference attributes<br/>2. Batch persist MVOs<br/>3. Create MVO change objects<br/>4. Evaluate exports<br/>5. Flush PE operations<br/>6. Flush obsolete CSOs<br/>7. Flush MVO deletions<br/>8. Update activity progress]
    PageFlush --> PageLoop

    PageLoop -->|No| UpdateWatermark[Update watermark<br/>LastDeltaSyncCompletedAt = UtcNow]
    UpdateWatermark --> Done([Sync Complete])
```

## Watermark Mechanism

```mermaid
flowchart LR
    subgraph "First-Ever Delta Sync"
        NullWatermark[LastDeltaSyncCompletedAt<br/>= null] --> DefaultMin[Defaults to<br/>DateTime.MinValue]
        DefaultMin --> AllCSOs[All CSOs selected<br/>Behaves like Full Sync]
    end

    subgraph "Subsequent Delta Syncs"
        PrevWatermark[LastDeltaSyncCompletedAt<br/>= previous sync time] --> FilterCSOs[Only CSOs where<br/>LastUpdated > watermark]
        FilterCSOs --> SubsetCSOs[Subset of CSOs<br/>processed]
    end

    subgraph "Watermark Update"
        SyncCompletes[Sync completes<br/>successfully] --> SetWatermark[LastDeltaSyncCompletedAt<br/>= DateTime.UtcNow]
        SetWatermark --> Persisted[Persisted to database<br/>via repository]
    end
```

## What Full Sync Does That Delta Sync Does Not

```mermaid
flowchart TD
    FullSync([Full Sync only]) --> SurfacePE[SurfacePendingExportsAsExecutionItems]
    SurfacePE --> FilterPE[Filter pending exports:<br/>Status = Pending or<br/>ExportNotConfirmed]
    FilterPE --> HasPE{Pending exports<br/>to surface?}
    HasPE -->|No| Skip([Skip])
    HasPE -->|Yes| CreateRPEI[Create RPEI for each PE<br/>ObjectChangeType from PE ChangeType<br/>Gives operators visibility into<br/>what next export run will do]
```

## Key Design Decisions

- **Identical per-CSO logic**: Both full and delta sync share the exact same `ProcessConnectedSystemObjectAsync()` from `SyncTaskProcessorBase`. The only difference is which CSOs are selected for processing.

- **Early exit optimisation**: Delta sync checks if any CSOs have been modified before loading caches and entering the page loop. If nothing has changed, it updates the watermark and returns immediately.

- **Watermark always advances**: Even when zero CSOs are modified, the watermark is updated. This prevents the watermark from becoming stale if no changes occur for an extended period.

- **First delta sync processes everything**: If `LastDeltaSyncCompletedAt` is null (no previous sync), the watermark defaults to `DateTime.MinValue`, effectively selecting all CSOs — the same set as a full sync.

- **No pending export surfacing**: Delta sync skips `SurfacePendingExportsAsExecutionItems()` since it's a lightweight incremental operation. Full sync surfaces pending exports as RPEIs so operators can see what changes are staged for the next export run.
