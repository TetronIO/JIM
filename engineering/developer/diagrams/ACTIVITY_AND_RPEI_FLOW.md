# Activity and RPEI Flow

> Last updated: 2026-10-29, JIM v0.17.0

This diagram shows how Activities are created, how Run Profile Execution Items (RPEIs) are accumulated during operations, and how the final activity status is determined. Activities are the immutable audit record for every operation in JIM.

Activities arise from two distinct paths. Operational work (synchronisation, data generation, clearing, deleting or deprovisioning a Connected System, removing data after a schema refresh, recalling a deleted Synchronisation Rule's contributed values, auxiliary class discovery, Temporal Scope Reconciliation, history retention cleanup) runs on a Worker Task, so its Activity is created when the task is queued and stays `InProgress` until the task completes. Configuration changes (issue #14) take no Worker Task: the config server that mutates the entity creates and completes the Activity synchronously, in the same call, alongside a versioned configuration snapshot. A few Activities are recorded by the service doing the work, outside both paths: a Scheduled Metaverse Object Deletion batch (`MetaverseObjectHousekeeping`, #1020), authentication events, password sets and Password Synchronisation, and `ScimRequests` (#124), one per Connected System, SCIM key and 15-minute window, counting what a SCIM client created, updated and deleted through the SCIM 2.0 Service Provider endpoint and the requests refused.

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
    Error -->|Connector flagged the object| ConnRPEI[RPEI: mapped Connector error<br/>e.g. MissingExternalIdAttributeValue<br/>An unreadable value is listed on the<br/>item and the object still imported, #2066]
    Error -->|Duplicate attributes| DupAttrRPEI[RPEI: DuplicateImportedAttributes]
    Error -->|Unknown object type| TypeRPEI[RPEI: CouldNotMatchObjectType]
    Error -->|Duplicate external ID| DupObjRPEI[RPEI: DuplicateObject]
    Error -->|External ID held under another<br/>Object Type, change not allowed #2049| TypeChangedRPEI[RPEI: ObjectTypeChanged<br/>held CSO kept and excluded<br/>from deletion detection]
    Error -->|Unhandled exception| UnhandledRPEI[RPEI: UnhandledError<br/>+ stack trace]

    AddedRPEI --> PostPasses
    UpdatedRPEI --> PostPasses
    DeletedRPEI --> PostPasses
    ConnRPEI --> PostPasses
    DupAttrRPEI --> PostPasses
    TypeRPEI --> PostPasses
    DupObjRPEI --> PostPasses
    TypeChangedRPEI --> PostPasses
    UnhandledRPEI --> PostPasses

    PostPasses[Full Import passes after all pages:<br/>Deletion detection: RPEI Deleted with<br/>DeletionDetected outcome per newly missing CSO,<br/>never for a CSO refused an Object Type change<br/>If refused by a Run Profile limit #1618:<br/>no RPEIs, a warning and<br/>DetectedDeletionsWithheld on the Activity<br/>Unconfirmed exported Create: RPEI ExportNotConfirmed #1695<br/>Reference resolution: every unresolved or ambiguous<br/>reference counted on the object's RPEI, #959<br/>UnresolvedReference error under Error handling]
    PostPasses --> Batches[Save phase, per create and update batch:<br/>FlushImportRpeisAsync bulk-inserts that<br/>batch's RPEIs via raw SQL once its CSOs<br/>have committed, accumulates summary stats,<br/>then releases them]
    Batches --> Reconcile[Reconciliation merges onto the CSO's RPEI:<br/>ExportConfirmed outcome, or an<br/>ExportNotConfirmed error on every import<br/>that misses a change, #2119<br/>A change held or released while it awaits<br/>an Initial Password adds nothing, #2039]
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
    Normal -->|Left scope, join kept| RetainRPEI[RPEI: ObjectChangeType =<br/>OutOfScopeRetainJoin<br/>its own outcome root, #1649]
    Normal -->|No changes| SkipRPEI[No RPEI created<br/>Only when HasChanges = true]

    TryCatch --> JoinError{SyncJoin<br/>Exception?}
    JoinError -->|AmbiguousMatch| AmbiguousRPEI[RPEI: AmbiguousMatch error]
    JoinError -->|Matching rule failed| RuleFailedRPEI[RPEI: ObjectMatchingRuleFailed error]
    JoinError -->|ExistingJoin| ExistingRPEI[RPEI: CouldNotJoinDueToExistingJoin]

    TryCatch --> ExprError{Expression<br/>exception?}
    ExprError -->|Evaluation failed| ExprRPEI[RPEI: ExpressionEvaluationError]
    ExprError -->|Missing input, mapping<br/>set to Fail the object| MissingRPEI[RPEI: ExpressionMissingInput]

    TryCatch --> ScopeError{Scoping criterion<br/>not evaluable? #2053}
    ScopeError -->|This object's scope, or a<br/>surviving contributor's #2336| ScopeRPEI[RPEI: ScopingCriterionFailed<br/>MVO left untouched<br/>A survivor's: the object is held<br/>back and kept marked for<br/>the next run, #2343]

    TryCatch --> Unhandled{Unhandled<br/>exception?}
    Unhandled -->|Yes| UnhandledRPEI2[RPEI: UnhandledError<br/>+ stack trace<br/>Processing continues to next CSO]

    PerCSO --> ExportEval{Export evaluation<br/>at the page flush}
    ExportEval -->|Matching before provisioning<br/>found several objects, #2052| ProvAmbiguousRPEI[RPEI on the MVO:<br/>AmbiguousProvisioningMatch<br/>with match candidates]
    ExportEval -->|Matching rule failed<br/>before provisioning| ProvRuleFailedRPEI[RPEI on the MVO:<br/>ObjectMatchingRuleFailed]
    ExportEval -->|Rule targets another<br/>Object Type in the system| TypeConflictRPEI[RPEI: CouldNotExportDueTo<br/>ExistingConnectedSystemObject]

    PerCSO --> Obsolete{CSO<br/>obsolete?}
    Obsolete -->|Yes| ObsoleteRPEIs[One RPEI per CSO:<br/>Disconnected root outcome with<br/>CsoDeleted and any MvoDeleted or<br/>MvoDeletionScheduled children,<br/>or Deleted when it was not joined<br/>The RPEI that schedules a deletion<br/>is recorded on the MVO, #1811]
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
    Outcome -->|Generated value already in use,<br/>corrected by Collision Remediation| CorrectedRPEI[RPEI: ObjectChangeType = PendingExport<br/>GeneratedValueRemediated outcome]
    Outcome -->|Generated value already in use,<br/>needs a decision| DecisionRPEI[RPEI: GeneratedValueCollisionUnresolved error<br/>Pending Export Parked]
    Outcome -->|Failed| FailRPEI[RPEI: ErrorType from the Connector's<br/>classification: AccessDenied,<br/>RejectedByConnectedSystem or<br/>OutsideManagedScope #2118,<br/>ClassMembershipRequirementsNotMet,<br/>UniqueValueAlreadyInUse,<br/>PasswordRequiredToEnableAccount,<br/>InvalidGeneratedExternalId and others<br/>Unclassified: UnhandledError<br/>Message: Export failed after N attempts<br/>Sent again at the next export run, #2119]
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

    HasErrors -->|Yes| AllErrors{ALL RPEIs have errors,<br/>refused Object Type changes<br/>aside #2049, and for an import,<br/>no more objects read than that?}
    AllErrors -->|Yes| FailActivity[FailActivityWithErrorAsync<br/>Status = FailedWithError<br/>All items experienced an error]
    AllErrors -->|No, some| Unhandled{Any RPEI with<br/>UnhandledError?}
    Unhandled -->|Yes| ErrorActivity[CompleteActivityWithErrorAsync<br/>Status = CompleteWithError]
    Unhandled -->|No| WarnActivity[CompleteActivityWithWarningAsync<br/>Status = CompleteWithWarning]

    CompleteOk[CompleteActivityAsync<br/>Status = Complete]

    CalcStats -.->|Exception thrown<br/>during status check| SafeFail[SafeFailActivityAsync<br/>See triple fallback below]
    TaskDone -.->|Stopped by the Worker's<br/>memory guard #2487| MemoryStop[SafeFailActivityAsync with the<br/>guard's reason: FailedWithError]
    TaskDone -.->|Memory low while it ran,<br/>not stopped #2487| MemoryWarn[Warning added after completion:<br/>Complete becomes CompleteWithWarning]
```

Error counts come from one database query (`GetActivityRpeiErrorCountsAsync`) rather than from RPEIs held in memory, and `ActivityOutcomeEvaluator` decides the status from them. An import gives no RPEI to an object it found unchanged, so for an import "every RPEI errored" fails the run only when the import read no more objects than it recorded errors for (its `ObjectsProcessed` at completion is the whole run's count); one bad value among a hundred unchanged objects completes with a warning. Other run types keep the RPEI rule, since their counters describe their last step. An Activity-level `WarningMessage` (a Connector warning such as a Delta Import falling back to a Full Import, withheld deletions or exports under a Run Profile limit) completes the Activity with a warning even when no RPEI has an error. An Object Type change refused at import (#2049) is left out of the every-RPEI-errored test: it is a safe, deliberate outcome that changed nothing, so it neither fails a run on its own nor decides whether one fails. The Worker's memory guard (#2487) fails a run it stopped with its reason, and adds a warning to a run that finished while memory was low, after completion so the verdict is not replaced; a completed Activity then reads `CompleteWithWarning`, and any other verdict is left as it is.

After a Full Import completes, the Worker stamps `LastSuccessfulFullImportCompletedAt` on the Connected System only when `FullImportSuccessEvaluator` agrees the run genuinely succeeded: `Complete`, or `CompleteWithWarning` with no object-level errors, and no deletions withheld (#1605). The stranded-value sweep's gate reads that timestamp.

## SafeFailActivityAsync - Triple Fallback

When activity completion fails (e.g., EF tracking corruption, disposed DbContext), this three-level fallback ensures activities are never left stuck in InProgress. A persistence failure goes to a fresh context first, because the run's own context still holds the entities that failed to save and would re-attempt the doomed write.

```mermaid
flowchart TD
    Error([Exception during<br/>activity completion]) --> Poisoned{Persistence failure?<br/>DbUpdateException, DbException<br/>or SyncPersistenceException}
    Poisoned -->|Yes| FreshFirst[Fresh context first<br/>same as Level 3]
    FreshFirst -->|Success| Done
    FreshFirst -->|Failed| Level1
    Poisoned -->|No| Level1[Level 1: Normal<br/>FailActivityWithErrorAsync<br/>via ActivityServer]
    Level1 --> L1Result{Success?}
    L1Result -->|Yes| Done([Activity marked failed])

    L1Result -->|No| Level2[Level 2: Direct repository<br/>Update activity status directly<br/>Bypasses EF tracking issues]
    Level2 --> L2Result{Success?}
    L2Result -->|Yes| Done

    L2Result -->|No| Level3[Level 3: Emergency<br/>Create fresh JimApplication<br/>+ new DbContext<br/>Force-update activity status,<br/>or keep a terminal status found there<br/>Copy the terminal state onto the<br/>caller's Activity, #1874]
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

- **Triple fallback for failure**<br /> `SafeFailActivityAsync` ensures activities are never left stuck in `InProgress`, even when the DbContext is corrupted or disposed. This is critical for system reliability; stuck activities would block future schedule executions. A persistence failure (including a `SyncPersistenceException`, whatever its inner cause) is recorded through a fresh context first. Whenever the fresh context records the failure, or finds the Activity already ended, the caller's copy adopts that terminal state (#1874): the Worker completes the task with the caller's copy, and `TaskingServer.CompleteWorkerTaskAsync` completes any Activity that copy still says is in progress, which used to turn a failed run into a `Complete` one and erase its error.

- **Initiator triad audit**<br /> Every activity records who initiated it (`InitiatedByType`, `InitiatedById`, `InitiatedByName`). For scheduled tasks, this preserves the schedule context. For deferred MVO deletions, the original initiator is captured at mark time and replayed during housekeeping.

- **Synchronous configuration-change Activities (#14)**<br /> Configuration changes do not go through a Worker Task. The owning config server creates the Activity, mutates the entity, captures a configuration snapshot, and completes the Activity all in the same synchronous call. `ActivityTargetType` now spans the full configuration surface, including `SynchronisationRule`, `Schedule`, `ServiceSetting`, `TrustedCertificate`, `ApiKey`, `Role`, `PredefinedSearch`, `ConnectorDefinition`, `MetaverseObjectType`, `MetaverseAttribute`, `ObjectMatchingRule` and `ExampleDataSet`, in addition to the operational target types created by Worker Tasks.

- **Incremental stat counters (#1078)**<br /> `GetActivityRunProfileExecutionStatsAsync` no longer aggregates over the RPEI and outcome tables on every read. The persistence paths maintain advisory per-Activity counter rows as the run executes, so an in-progress Activity's stats (served repeatedly while an administrator watches a run) are an O(counter rows) lookup. On completion, `FinaliseActivityRunProfileExecutionStatsAsync` replaces the counters with an exact aggregation and sets `Activity.RunProfileExecutionStatsFinalised`. Activities that completed before the counter table existed keep the legacy aggregation path and are finalised lazily the first time their stats are read. Non-relational providers (the EF in-memory test provider) always use the aggregation path, since counter maintenance is raw SQL.

- **Run Profile steps on the Activity (#1161)**<br /> Before a Run Profile execution starts, `ActivityPhaseReporter.StartAsync` records every step the run can perform, so the Activity shows the whole journey rather than only the step it has reached; processors enter each step as they reach it, a Connector can narrate sub-steps and object counts inside the fetching step, and `FinishAsync` closes the steps out whatever happens, marking the step a failed run stopped in.

- **Outcome tree additions (v0.15.0)**<br /> Outcomes now carry more of the story: `MvoDeletionCancelled` beneath a `Joined` outcome when a rejoin cancels a scheduled deletion (#1627), `ProvisioningCancelled` when never-exported provisioning is withdrawn rather than deleted (#1682), `ValuesPreserved` when a disconnecting system's values are kept because no import source remains (#1570), and an `ExportFailed` outcome on the RPEI of an exported Create that a Full Import did not report back (#1695). Pending Export outcomes record the change type they staged (`StagedChangeType`, #1573).

- **Scope-exit and no-contributor sync outcomes (Attribute Priority, #91)**<br /> Beyond the `ObjectChangeType` recorded on each RPEI, sync builds a finer-grained outcome tree. A CSO leaving scope (rather than being deleted at source) records a `DisconnectedOutOfScope` outcome, or an `OutOfScopeRetainJoin` outcome when the scoping rule's Inbound Out-of-Scope Action is RemainJoined (#1649; before that it was recorded as a stray `AttributeFlow` root), both attributed to the scoping rule; and when a recalled attribute has no surviving contributor and is genuinely cleared (not re-elected to a survivor, not frozen under a deletion grace period), a `NoContributor` child outcome is surfaced so an administrator can see the blank was an event, not merely an uncontributed attribute.
