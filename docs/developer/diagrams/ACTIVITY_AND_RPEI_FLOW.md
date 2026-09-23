# Activity and RPEI Flow

> Last updated: 2026-09-23, JIM v0.15.0

This diagram shows how Activities are created, how Run Profile Execution Items (RPEIs) are accumulated during operations, and how the final activity status is determined. Activities are the immutable audit record for every operation in JIM.

Activities arise from two distinct paths. Operational work (synchronisation, data generation, clearing, deleting or deprovisioning a Connected System, removing data after a schema refresh, recalling a deleted Synchronisation Rule's contributed values, auxiliary class discovery, Temporal Scope Reconciliation, history retention cleanup) runs on a Worker Task, so its Activity is created when the task is queued and stays `InProgress` until the task completes. Configuration changes (issue #14) take no Worker Task: the config server that mutates the entity creates and completes the Activity synchronously, in the same call, alongside a versioned configuration snapshot.

Since v0.10.0, sync RPEIs are bulk-inserted per page via raw SQL (`FlushRpeisAsync`) rather than held in-memory for the full run, to keep memory bounded at 100K+ object scale. Summary statistics are accumulated incrementally during each flush. Cross-page reference resolution merges new attribute-flow rows under the existing MvoChange parent rather than creating a duplicate RPEI, resolving the previous ~2x RPEI duplication when references spanned multiple pages.

## Activity Status Values

| Status | Value | Meaning |
|--------|-------|---------|
| NotSet | 0 | Default, should not appear in practice |
| InProgress | 1 | Set at creation, operation is running |
| Complete | 2 | All RPEIs succeeded, no errors |
| CompleteWithWarning | 3 | Some RPEIs have errors, but not all; or no RPEI errors but the Activity carries a warning |
| CompleteWithError | 4 | Some RPEIs recorded an `UnhandledError` (a JIM defect rather than a data issue), but not all RPEIs errored |
| FailedWithError | 5 | All RPEIs errored, or unhandled exception |
| Cancelled | 6 | User cancelled the operation |

## Activity Creation

```mermaid
flowchart TD
    Trigger{Task<br/>origin?}

    Trigger -->|Schedule fires| Scheduler[SchedulerServer:<br/>Queue step group tasks]
    Trigger -->|Manual run| Web[JIM.Web:<br/>User clicks Run]
    Trigger -->|API call| Api[API controller:<br/>Create task request]
    Trigger -->|Config change| ConfigServer[Config server e.g.<br/>ConnectedSystemServer, SchedulerServer,<br/>MetaverseServer, SearchServer, ServiceSettingsServer]

    Scheduler --> CreateTask[TaskingServer.CreateWorkerTaskAsync]
    Web --> CreateTask
    Api --> CreateTask

    CreateTask --> Fence{Run Profile or clear<br/>against a Connected System<br/>that is being deleted?}
    Fence -->|Yes| Refused([Refused: no task,<br/>no Activity #809])
    Fence -->|No| TaskType{Worker task<br/>type?}
    TaskType -->|SynchronisationWorkerTask| SyncActivity[TargetType = ConnectedSystemRunProfile<br/>TargetName = runProfile.Name<br/>TargetContext = connectedSystem.Name]
    TaskType -->|ExampleDataTemplateWorkerTask| DataGenActivity[TargetType = DataGeneration]
    TaskType -->|ClearConnectedSystemObjectsWorkerTask| ClearActivity[TargetType = ConnectedSystem<br/>TargetOperationType = Clear]
    TaskType -->|DeleteConnectedSystemWorkerTask| DeleteActivity[TargetType = ConnectedSystem<br/>TargetOperationType = Delete,<br/>or Deprovision for Synchronised<br/>Deprovisioning #809]
    TaskType -->|SchemaRefreshRemovalWorkerTask| SchemaActivity[TargetType = ConnectedSystem<br/>TargetOperationType = SchemaRefreshRemoval]
    TaskType -->|DeleteSyncRuleWorkerTask| RecallActivity[TargetType = SynchronisationRule<br/>TargetOperationType = RecallAttributeValues]
    TaskType -->|AuxiliaryClassDiscoveryWorkerTask| AuxActivity[TargetType = ConnectedSystem<br/>TargetOperationType = DiscoverAuxiliaryClasses]
    TaskType -->|TemporalScopeReconciliationWorkerTask| TemporalActivity[TargetType = TemporalScopeReconciliation]
    TaskType -->|HistoryRetentionCleanupWorkerTask| RetentionActivity[TargetType = HistoryRetentionCleanup<br/>TargetOperationType = Delete]
    TaskType -->|ConfigurationChangePreviewWorkerTask| PreviewActivity[No new Activity: the task must carry<br/>the Activity its preview started under]

    SyncActivity --> CreateActivity
    DataGenActivity --> CreateActivity
    ClearActivity --> CreateActivity
    DeleteActivity --> CreateActivity
    SchemaActivity --> CreateActivity
    RecallActivity --> CreateActivity
    AuxActivity --> CreateActivity
    TemporalActivity --> CreateActivity
    RetentionActivity --> CreateActivity

    %% --- Configuration change: created synchronously, no worker task (#14) ---
    ConfigServer --> ConfigActivity[Create + complete Activity SYNCHRONOUSLY<br/>no worker task involved<br/>CreateActivityWithTriadAsync, then<br/>capture configuration snapshot, then<br/>CompleteActivityAsync in the same call]
    ConfigActivity --> ConfigTargets[TargetType is a configuration type:<br/>SynchronisationRule, Schedule, ServiceSetting,<br/>TrustedCertificate, ApiKey, Role, PredefinedSearch,<br/>ConnectorDefinition, MetaverseObjectType,<br/>MetaverseAttribute, ExampleDataSet, ObjectMatchingRule, ...<br/>TargetOperationType = Create, Update or Delete]
    ConfigTargets --> ConfigDone([Configuration change<br/>Activity persisted])

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
    Success -->|Delete from a Delta Import| DeletedRPEI[RPEI: ObjectChangeType = Deleted<br/>DeletionDetected outcome]
    Success -->|No changes, skipped by<br/>content hash, or a replayed delete| NoRPEI[No RPEI kept]

    PerObject --> Error{Error<br/>type?}
    Error -->|Connector flagged the object| ConnRPEI[RPEI: mapped Connector error<br/>e.g. MissingExternalIdAttributeValue]
    Error -->|Duplicate attributes| DupAttrRPEI[RPEI: DuplicateImportedAttributes]
    Error -->|Unknown object type| TypeRPEI[RPEI: CouldNotMatchObjectType]
    Error -->|Duplicate external ID| DupObjRPEI[RPEI: DuplicateObject]
    Error -->|Unhandled exception| UnhandledRPEI[RPEI: UnhandledError<br/>+ stack trace]

    AddedRPEI --> PostPasses
    UpdatedRPEI --> PostPasses
    DeletedRPEI --> PostPasses
    ConnRPEI --> PostPasses
    DupAttrRPEI --> PostPasses
    TypeRPEI --> PostPasses
    DupObjRPEI --> PostPasses
    UnhandledRPEI --> PostPasses

    PostPasses[Full Import passes after all pages:<br/>Deletion detection: RPEI Deleted with<br/>DeletionDetected outcome per newly missing CSO<br/>If refused by a Run Profile limit #1618:<br/>no RPEIs, a warning and<br/>DetectedDeletionsWithheld on the Activity<br/>Unconfirmed exported Create: RPEI ExportNotConfirmed #1695<br/>Reference resolution: UnresolvedReference errors]
    PostPasses --> Batches[Save phase, per create and update batch:<br/>FlushImportRpeisAsync bulk-inserts that<br/>batch's RPEIs via raw SQL once its CSOs<br/>have committed, accumulates summary stats,<br/>then releases them]
    Batches --> Reconcile[Reconciliation merges onto the CSO's RPEI:<br/>ExportConfirmed outcome,<br/>ExportNotConfirmed or<br/>ExportConfirmationFailed error]
    Reconcile --> FinalFlush[Flush remaining RPEIs<br/>Activity counters and message<br/>describe the whole run]
```

## RPEI Accumulation During Sync

```mermaid
flowchart TD
    SyncStart([Sync processing]) --> DirectAdd[RPEIs added per-page to<br/>activity.RunProfileExecutionItems<br/>during per-CSO processing,<br/>then bulk-inserted and cleared<br/>by FlushRpeisAsync]

    DirectAdd --> PerCSO{For each CSO}

    PerCSO --> TryCatch[Per-exception try-catch<br/>around ProcessActiveConnectedSystemObjectAsync]

    TryCatch --> Normal{Normal<br/>outcome?}
    Normal -->|Projected| ProjectedRPEI[RPEI: ObjectChangeType = Projected]
    Normal -->|Joined| JoinedRPEI[RPEI: ObjectChangeType = Joined]
    Normal -->|Attribute Flow| FlowRPEI[RPEI: ObjectChangeType = AttributeFlow]
    Normal -->|Left import scope| OosRPEI[RPEI: ObjectChangeType =<br/>DisconnectedOutOfScope]
    Normal -->|No changes| SkipRPEI[No RPEI created<br/>Only when HasChanges = true]

    TryCatch --> JoinError{SyncJoin<br/>Exception?}
    JoinError -->|AmbiguousMatch| AmbiguousRPEI[RPEI: AmbiguousMatch error]
    JoinError -->|ExistingJoin| ExistingRPEI[RPEI: CouldNotJoinDueToExistingJoin]

    TryCatch --> ExprError{Expression<br/>exception?}
    ExprError -->|Evaluation failed| ExprRPEI[RPEI: ExpressionEvaluationError]
    ExprError -->|Missing input, mapping<br/>set to Fail the object| MissingRPEI[RPEI: ExpressionMissingInput]

    TryCatch --> Unhandled{Unhandled<br/>exception?}
    Unhandled -->|Yes| UnhandledRPEI2[RPEI: UnhandledError<br/>+ stack trace<br/>Processing continues to next CSO]

    PerCSO --> Obsolete{CSO<br/>obsolete?}
    Obsolete -->|Yes| ObsoleteRPEIs[One RPEI per CSO:<br/>Disconnected root outcome with<br/>CsoDeleted and any MvoDeleted or<br/>MvoDeletionScheduled children,<br/>or Deleted when it was not joined]
```

## RPEI Accumulation During Export

```mermaid
flowchart TD
    ExportStart([Export runs in batches]) --> Limits[Run Profile export limits #1618:<br/>Max creates, updates and deletes<br/>A change type with more pending than its<br/>limit is withheld entirely and stays pending<br/>Counts and a warning recorded on the Activity]
    Limits --> ProcessResults[PersistBatchRpeisAsync per batch:<br/>Creates RPEIs from ProcessedExportItems,<br/>bulk-inserts them and releases memory]

    ProcessResults --> PerExport{For each<br/>export result}

    PerExport --> Outcome{Export<br/>outcome?}
    Outcome -->|Create or Update succeeded| ExpRPEI[RPEI: ObjectChangeType = Exported]
    Outcome -->|Delete succeeded| DeprovRPEI[RPEI: ObjectChangeType = Deprovisioned]
    Outcome -->|Deferred whole, a reference<br/>is still owed #1398| DeferRPEI[RPEI: ObjectChangeType = PendingExport<br/>No export outcome]
    Outcome -->|Unresolved reference, under<br/>Error handling| UnresolvedRPEI[RPEI: UnresolvedReference error]
    Outcome -->|Failed| FailRPEI[RPEI: ErrorType = UnhandledError,<br/>or InvalidGeneratedExternalId or<br/>ClassMembershipRequirementsNotMet<br/>Error message + retry count]
```

## Activity Status Determination

```mermaid
flowchart TD
    TaskDone([Task completes]) --> CalcStats[CalculateActivitySummaryStats:<br/>Count RPEIs by ObjectChangeType<br/>Populate TotalProjected, TotalJoined,<br/>TotalAttributeFlows, TotalErrors, etc.]

    CalcStats --> CheckErrors{Analyse<br/>RPEI errors}

    CheckErrors --> HasErrors{Any RPEI has<br/>ErrorType set<br/>and != NotSet?}

    HasErrors -->|No| HasWarning{Activity carries<br/>a WarningMessage?}
    HasWarning -->|Yes| WarnActivity
    HasWarning -->|No| CompleteOk

    HasErrors -->|Yes| AllErrors{ALL RPEIs<br/>have errors?}
    AllErrors -->|Yes| FailActivity[FailActivityWithErrorAsync<br/>Status = FailedWithError<br/>All items experienced an error]
    AllErrors -->|No, some| Unhandled{Any RPEI with<br/>UnhandledError?}
    Unhandled -->|Yes| ErrorActivity[CompleteActivityWithErrorAsync<br/>Status = CompleteWithError]
    Unhandled -->|No| WarnActivity[CompleteActivityWithWarningAsync<br/>Status = CompleteWithWarning]

    CompleteOk[CompleteActivityAsync<br/>Status = Complete]

    CalcStats -.->|Exception thrown<br/>during status check| SafeFail[SafeFailActivityAsync<br/>See triple fallback below]
```

Error counts come from one database query (`GetActivityRpeiErrorCountsAsync`) rather than from RPEIs held in memory. An Activity-level `WarningMessage` (a Connector warning such as a Delta Import falling back to a Full Import, withheld deletions or exports under a Run Profile limit) completes the Activity with a warning even when no RPEI has an error.

After a Full Import completes, the Worker stamps `LastSuccessfulFullImportCompletedAt` on the Connected System only when `FullImportSuccessEvaluator` agrees the run genuinely succeeded: `Complete`, or `CompleteWithWarning` with no object-level errors, and no deletions withheld (#1605). The stranded-value sweep's gate reads that timestamp.

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

- **RPEI list separation during import**<br /> RPEIs are maintained in a separate list during import to prevent EF Core from following the `Activity -> RPEI -> CSO` navigation chain during `SaveChanges`. This avoids accidentally persisting CSOs before they're ready. Each save batch's RPEIs are bulk-inserted via raw SQL once that batch's CSOs have committed, and summary statistics accumulate per flush, so an import never holds every RPEI until the end.

- **Direct attachment during sync**<br /> During sync, RPEIs are added directly to `activity.RunProfileExecutionItems` since CSOs already exist in the database (they were created during a prior import).

- **Conditional RPEI creation**<br /> RPEIs are only created when `HasChanges = true` during sync. This is an optimisation to avoid unnecessary allocations for objects that haven't changed.

- **Error isolation**<br /> Each CSO is processed within its own try-catch during sync. Errors create RPEIs but do not halt processing of remaining CSOs. This ensures a single bad object doesn't prevent the entire sync from completing.

- **Status model**<br /> `Complete` (no errors, no Activity warning), `CompleteWithWarning` (some errors, or an Activity-level warning), `CompleteWithError` (some `UnhandledError` items, escalated because they indicate a defect), `FailedWithError` (all errors or unhandled exception). This gives operators clear visibility into the severity of issues.

- **Triple fallback for failure**<br /> `SafeFailActivityAsync` ensures activities are never left stuck in `InProgress`, even when the DbContext is corrupted or disposed. This is critical for system reliability; stuck activities would block future schedule executions.

- **Initiator triad audit**<br /> Every activity records who initiated it (`InitiatedByType`, `InitiatedById`, `InitiatedByName`). For scheduled tasks, this preserves the schedule context. For deferred MVO deletions, the original initiator is captured at mark time and replayed during housekeeping.

- **Synchronous configuration-change Activities (#14)**<br /> Configuration changes do not go through a Worker Task. The owning config server creates the Activity, mutates the entity, captures a configuration snapshot, and completes the Activity all in the same synchronous call. `ActivityTargetType` now spans the full configuration surface, including `SynchronisationRule`, `Schedule`, `ServiceSetting`, `TrustedCertificate`, `ApiKey`, `Role`, `PredefinedSearch`, `ConnectorDefinition`, `MetaverseObjectType`, `MetaverseAttribute`, `ObjectMatchingRule` and `ExampleDataSet`, in addition to the operational target types created by Worker Tasks.

- **Incremental stat counters (#1078)**<br /> `GetActivityRunProfileExecutionStatsAsync` no longer aggregates over the RPEI and outcome tables on every read. The persistence paths maintain advisory per-Activity counter rows as the run executes, so an in-progress Activity's stats (served repeatedly while an administrator watches a run) are an O(counter rows) lookup. On completion, `FinaliseActivityRunProfileExecutionStatsAsync` replaces the counters with an exact aggregation and sets `Activity.RunProfileExecutionStatsFinalised`. Activities that completed before the counter table existed keep the legacy aggregation path and are finalised lazily the first time their stats are read. Non-relational providers (the EF in-memory test provider) always use the aggregation path, since counter maintenance is raw SQL.

- **Run Profile steps on the Activity (#1161)**<br /> Before a Run Profile execution starts, `ActivityPhaseReporter.StartAsync` records every step the run can perform, so the Activity shows the whole journey rather than only the step it has reached; processors enter each step as they reach it, a Connector can narrate sub-steps and object counts inside the fetching step, and `FinishAsync` closes the steps out whatever happens, marking the step a failed run stopped in.

- **Outcome tree additions (v0.15.0)**<br /> Outcomes now carry more of the story: `MvoDeletionCancelled` beneath a `Joined` outcome when a rejoin cancels a scheduled deletion (#1627), `ProvisioningCancelled` when never-exported provisioning is withdrawn rather than deleted (#1682), `ValuesPreserved` when a disconnecting system's values are kept because no import source remains (#1570), and an `ExportFailed` outcome on the RPEI of an exported Create that a Full Import did not report back (#1695). Pending Export outcomes record the change type they staged (`StagedChangeType`, #1573).

- **Scope-exit and no-contributor sync outcomes (Attribute Priority, #91)**<br /> Beyond the `ObjectChangeType` recorded on each RPEI, sync builds a finer-grained outcome tree. A CSO leaving scope (rather than being deleted at source) records a `DisconnectedOutOfScope` outcome, or an `OutOfScopeRetainJoin` outcome when the scoping rule's Inbound Out-of-Scope Action is RemainJoined (#1649; before that it was recorded as a stray `AttributeFlow` root), both attributed to the scoping rule; and when a recalled attribute has no surviving contributor and is genuinely cleared (not re-elected to a survivor, not frozen under a deletion grace period), a `NoContributor` child outcome is surfaced so an administrator can see the blank was an event, not merely an uncontributed attribute.
