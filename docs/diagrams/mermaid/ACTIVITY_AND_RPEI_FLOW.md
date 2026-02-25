# Activity and RPEI Flow

> Generated against JIM v0.3.0 (`0d1c88e9`). If the codebase has changed significantly since then, these diagrams may be out of date.

This diagram shows how Activities are created, how Run Profile Execution Items (RPEIs) are accumulated during operations, and how the final activity status is determined. Activities are the immutable audit record for every operation in JIM.

## Activity Status Values

| Status | Value | Meaning |
|--------|-------|---------|
| NotSet | 0 | Default, should not appear in practice |
| InProgress | 1 | Set at creation, operation is running |
| Complete | 2 | All RPEIs succeeded, no errors |
| CompleteWithWarning | 3 | Some RPEIs have errors, but not all |
| CompleteWithError | 4 | Exception during processing (with stack trace) |
| FailedWithError | 5 | All RPEIs errored, or unhandled exception |
| Cancelled | 6 | User cancelled the operation |

## Activity Creation

```mermaid
flowchart TD
    Trigger{Task<br/>origin?}

    Trigger -->|Schedule fires| Scheduler[SchedulerServer:<br/>Queue step group tasks]
    Trigger -->|Manual run| Web[JIM.Web:<br/>User clicks Run]
    Trigger -->|API call| Api[API controller:<br/>Create task request]

    Scheduler --> CreateTask[TaskingServer.CreateWorkerTaskAsync]
    Web --> CreateTask
    Api --> CreateTask

    CreateTask --> TaskType{Worker task<br/>type?}
    TaskType -->|SynchronisationWorkerTask| SyncActivity[TargetType = ConnectedSystemRunProfile<br/>TargetName = runProfile.Name<br/>TargetContext = connectedSystem.Name]
    TaskType -->|DataGenerationTemplateWorkerTask| DataGenActivity[TargetType = DataGenerationTemplate]
    TaskType -->|ClearConnectedSystemObjectsWorkerTask| ClearActivity[TargetType = ConnectedSystem<br/>TargetOperationType = Clear]
    TaskType -->|DeleteConnectedSystemWorkerTask| DeleteActivity[TargetType = ConnectedSystem<br/>TargetOperationType = Delete]

    SyncActivity --> CreateActivity
    DataGenActivity --> CreateActivity
    ClearActivity --> CreateActivity
    DeleteActivity --> CreateActivity

    CreateActivity[CreateActivityWithTriadAsync:<br/>Status = InProgress<br/>Executed = UtcNow<br/>Copy initiator triad from task<br/>Copy schedule execution context]
    CreateActivity --> Validate[ValidateActivity:<br/>InitiatedByType must not be NotSet<br/>User/ApiKey must have InitiatedById]
    Validate --> Persist[Persist Activity<br/>Associate with WorkerTask]
```

## RPEI Accumulation During Import

```mermaid
flowchart TD
    ImportStart([Import processing]) --> SeparateList[RPEIs stored in SEPARATE list<br/>Not added to Activity.RunProfileExecutionItems<br/>Prevents EF Core from following<br/>Activity -> RPEI -> CSO navigation<br/>during SaveChanges]

    SeparateList --> PerObject{For each<br/>imported object}

    PerObject --> Success{Object<br/>outcome?}
    Success -->|New CSO| AddedRPEI[RPEI: ObjectChangeType = Added]
    Success -->|Updated CSO| UpdatedRPEI[RPEI: ObjectChangeType = Updated]
    Success -->|Obsoleted CSO| DeletedRPEI[RPEI: ObjectChangeType = Deleted]
    Success -->|No changes| NoRPEI[No RPEI created<br/>Avoids unnecessary allocations]

    PerObject --> Error{Error<br/>type?}
    Error -->|Duplicate attributes| DupAttrRPEI[RPEI: DuplicateImportedAttributes]
    Error -->|Unknown object type| TypeRPEI[RPEI: CouldNotMatchObjectType]
    Error -->|Duplicate external ID| DupObjRPEI[RPEI: DuplicateObject]
    Error -->|Missing external ID| MissingIdRPEI[RPEI: MissingExternalIdAttributeValue]
    Error -->|Unhandled exception| UnhandledRPEI[RPEI: UnhandledError<br/>+ stack trace]

    AddedRPEI --> AfterPersist
    UpdatedRPEI --> AfterPersist
    DeletedRPEI --> AfterPersist
    DupAttrRPEI --> AfterPersist
    TypeRPEI --> AfterPersist
    DupObjRPEI --> AfterPersist
    MissingIdRPEI --> AfterPersist
    UnhandledRPEI --> AfterPersist

    AfterPersist[After CSOs persisted:<br/>activity.AddRunProfileExecutionItems<br/>from separate list]
    AfterPersist --> UpdateActivity[UpdateActivityAsync<br/>RPEIs now safely attached]
```

## RPEI Accumulation During Sync

```mermaid
flowchart TD
    SyncStart([Sync processing]) --> DirectAdd[RPEIs added directly to<br/>activity.RunProfileExecutionItems<br/>during per-CSO processing]

    DirectAdd --> PerCSO{For each CSO}

    PerCSO --> TryCatch[Three-layer try-catch<br/>around ProcessConnectedSystemObjectAsync]

    TryCatch --> Normal{Normal<br/>outcome?}
    Normal -->|Projected| ProjectedRPEI[RPEI: ObjectChangeType = Projected]
    Normal -->|Joined| JoinedRPEI[RPEI: ObjectChangeType = Joined]
    Normal -->|Attribute flow| FlowRPEI[RPEI: ObjectChangeType = AttributeFlow]
    Normal -->|No changes| SkipRPEI[No RPEI created<br/>Only when HasChanges = true]

    TryCatch --> JoinError{SyncJoin<br/>Exception?}
    JoinError -->|AmbiguousMatch| AmbiguousRPEI[RPEI: AmbiguousMatch error]
    JoinError -->|ExistingJoin| ExistingRPEI[RPEI: CouldNotJoinDueToExistingJoin]

    TryCatch --> Unhandled{Unhandled<br/>exception?}
    Unhandled -->|Yes| UnhandledRPEI2[RPEI: UnhandledError<br/>+ stack trace<br/>Processing continues to next CSO]

    PerCSO --> Obsolete{CSO<br/>obsolete?}
    Obsolete -->|Yes| ObsoleteRPEIs[Up to 2 RPEIs:<br/>Disconnected + Deleted]
```

## RPEI Accumulation During Export

```mermaid
flowchart TD
    ExportStart([Export completes]) --> ProcessResults[ProcessExportResultAsync<br/>Creates RPEIs from<br/>ProcessedExportItems]

    ProcessResults --> PerExport{For each<br/>export result}

    PerExport --> Outcome{Export<br/>outcome?}
    Outcome -->|Create succeeded| ProvRPEI[RPEI: ObjectChangeType = Provisioned]
    Outcome -->|Update succeeded| ExpRPEI[RPEI: ObjectChangeType = Exported]
    Outcome -->|Delete succeeded| DeprovRPEI[RPEI: ObjectChangeType = Deprovisioned]
    Outcome -->|Failed| FailRPEI[RPEI: ErrorType = UnhandledError<br/>Error message + retry count]
```

## Activity Status Determination

```mermaid
flowchart TD
    TaskDone([Task completes]) --> CalcStats[CalculateActivitySummaryStats:<br/>Count RPEIs by ObjectChangeType<br/>Populate TotalProjected, TotalJoined,<br/>TotalAttributeFlows, TotalErrors, etc.]

    CalcStats --> CheckErrors{Analyse<br/>RPEI errors}

    CheckErrors --> HasErrors{Any RPEI has<br/>ErrorType set<br/>and != NotSet?}
    HasErrors -->|No| CompleteOk[CompleteActivityAsync<br/>Status = Complete]

    HasErrors -->|Yes| AllErrors{ALL RPEIs<br/>have errors?}
    AllErrors -->|Yes| FailActivity[FailActivityWithErrorAsync<br/>Status = FailedWithError<br/>All items experienced an error]
    AllErrors -->|No, some| WarnActivity[CompleteActivityWithWarningAsync<br/>Status = CompleteWithWarning]

    CalcStats -.->|Exception thrown<br/>during status check| SafeFail[SafeFailActivityAsync<br/>See triple fallback below]
```

## SafeFailActivityAsync - Triple Fallback

When activity completion fails (e.g., EF tracking corruption, disposed DbContext), this three-level fallback ensures activities are never left stuck in InProgress.

```mermaid
flowchart TD
    Error([Exception during<br/>activity completion]) --> Level1[Level 1: Normal<br/>FailActivityWithErrorAsync<br/>via ActivityServer]
    Level1 --> L1Result{Success?}
    L1Result -->|Yes| Done([Activity marked failed])

    L1Result -->|No| Level2[Level 2: Direct repository<br/>Update activity status directly<br/>Bypasses EF tracking issues]
    Level2 --> L2Result{Success?}
    L2Result -->|Yes| Done

    L2Result -->|No| Level3[Level 3: Emergency<br/>Create fresh JimApplication<br/>+ new DbContext<br/>Force-update activity status]
    Level3 --> L3Result{Success?}
    L3Result -->|Yes| Done
    L3Result -->|No| Fatal[FATAL: Log error<br/>Activity stuck in InProgress<br/>Requires manual intervention]
```

## Key Design Decisions

- **RPEI list separation during import**: RPEIs are maintained in a separate list during import to prevent EF Core from following the `Activity -> RPEI -> CSO` navigation chain during `SaveChanges`. This avoids accidentally persisting CSOs before they're ready.

- **Direct attachment during sync**: During sync, RPEIs are added directly to `activity.RunProfileExecutionItems` since CSOs already exist in the database (they were created during a prior import).

- **Conditional RPEI creation**: RPEIs are only created when `HasChanges = true` during sync. This is an optimisation to avoid unnecessary allocations for objects that haven't changed.

- **Error isolation**: Each CSO is processed within its own try-catch during sync. Errors create RPEIs but do not halt processing of remaining CSOs. This ensures a single bad object doesn't prevent the entire sync from completing.

- **Three-tier status model**: `Complete` (no errors), `CompleteWithWarning` (some errors), `FailedWithError` (all errors or unhandled exception). This gives operators clear visibility into the severity of issues.

- **Triple fallback for failure**: `SafeFailActivityAsync` ensures activities are never left stuck in `InProgress`, even when the DbContext is corrupted or disposed. This is critical for system reliability — stuck activities would block future schedule executions.

- **Initiator triad audit**: Every activity records who initiated it (`InitiatedByType`, `InitiatedById`, `InitiatedByName`). For scheduled tasks, this preserves the schedule context. For deferred MVO deletions, the original initiator is captured at mark time and replayed during housekeeping.
