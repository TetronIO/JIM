# Full Synchronisation - CSO Processing Flow

> Last updated: 2026-07-10, JIM v0.13.0

This diagram shows the core decision tree for processing a single Connected System Object (CSO) during Full or Delta Synchronisation. This is the central flow of JIM's identity management engine.

Both Full Sync and Delta Sync use identical processing logic per-CSO. The only difference is CSO selection:
- **Full Sync**: processes ALL CSOs in the Connected System (or only those in the target partition, if the Run Profile specifies a `TargetPartitionId`; see below)
- **Delta Sync**: processes only CSOs modified since `LastSyncCompletedAt`

Since v0.7.1, sync decisions are split across three layers:
- **ISyncEngine:** Pure domain logic (projection, Attribute Flow, deletion rules, export confirmation). Stateless, I/O-free.
- **ISyncServer:** Orchestration facade (matching, scoping, drift detection, export evaluation). Delegates to application-layer servers.
- **ISyncRepository:** Dedicated data access (bulk CSO/MVO writes, Pending Exports, RPEIs).

## Overall Page Processing

```mermaid
flowchart TD
    Start([Start Sync]) --> Prepare[Prepare: count CSOs + Pending Exports<br/>If TargetPartitionId set, scope to that partition<br/>Load Synchronisation Rules, object types via ISyncRepository<br/>Build drift detection cache<br/>Build export evaluation cache<br/>Pre-load Pending Exports into dictionary]
    Prepare --> PageLoop{More CSO<br/>pages?}

    PageLoop -->|Yes| LoadPage[Load page of CSOs<br/>without attributes for performance]
    LoadPage --> CsoLoop{More CSOs<br/>in page?}

    CsoLoop -->|Yes| CheckCancel{Cancellation<br/>requested?}
    CheckCancel -->|Yes| FlushBeforeCancel[Complete current page flush<br/>before stopping]
    FlushBeforeCancel --> Return([Return - activity<br/>finalised by caller])
    CheckCancel -->|No| Pass1[Pass 1: for every CSO in page<br/>ProcessObsoleteAndExportConfirmationAsync<br/>- Confirm Pending Exports<br/>- Tear down obsolete CSOs<br/>- Populate _pendingDisconnectedMvoIds]
    Pass1 --> Pass2[Pass 2: for every non-obsolete CSO<br/>ProcessActiveConnectedSystemObjectAsync<br/>See Per-CSO Processing below<br/>Skips if IsUnchangedSinceLastSync]
    Pass2 --> IncrProgress[Increment ObjectsProcessed]
    IncrProgress --> CsoLoop

    CsoLoop -->|No| DeferredRef[Process deferred reference attributes<br/>Second pass: resolve MVO references<br/>that depend on other CSOs in page]
    DeferredRef --> PersistMvo[PersistPendingMetaverseObjectsAsync:<br/>bulk persist MVO creates + updates]
    PersistMvo --> CreateMvoChanges[CreatePendingMvoChangeObjectsAsync:<br/>build in-memory MVO change records<br/>for audit trail]
    CreateMvoChanges --> EvalExports[EvaluatePendingExportsAsync:<br/>batch-evaluate outbound exports<br/>for each tracked MVO]
    EvalExports --> FlushPE[FlushPendingExportOperationsAsync:<br/>create/delete/update Pending Exports]
    FlushPE --> ResolveSnapshots[ResolvePendingExportReferenceSnapshotsAsync:<br/>fix up reference attribute snapshots<br/>on newly-created Pending Exports]
    ResolveSnapshots --> FlushCSO[FlushObsoleteCsoOperationsAsync:<br/>persist queued CSO deletions]
    FlushCSO --> FlushMVO[FlushPendingMvoDeletionsAsync:<br/>0-grace-period MVO deletions]
    FlushMVO --> FlushRpeis[FlushRpeisAsync:<br/>bulk-insert RPEIs via raw SQL<br/>clear in-memory collection]
    FlushRpeis --> FlushMvoChanges[FlushPendingMvoChangesAsync:<br/>persist MVO change records<br/>before change tracker clear]
    FlushMvoChanges --> ClearCache[Clear export evaluation cache<br/>and change tracker at page boundary]
    ClearCache --> UpdateProgress[Update activity progress<br/>in database]
    UpdateProgress --> PageLoop

    PageLoop -->|No| CrossPage[Cross-page reference resolution<br/>Reload CSOs with unresolved references<br/>Resolve MVO references across pages<br/>Merge new attribute-flow rows under<br/>the existing MvoChange parent RPEI<br/>Re-run persist/flush pipeline]
    CrossPage --> Watermark[Update delta sync watermark<br/>LastSyncCompletedAt = UtcNow]
    Watermark --> End([Sync Complete])
```

## Per-CSO Processing

This is the decision tree within `ProcessConnectedSystemObjectAsync` for a single CSO.

```mermaid
flowchart TD
    Entry([ProcessConnectedSystemObjectAsync]) --> ConfirmPE[Confirm Pending Exports<br/>ISyncEngine.EvaluatePendingExportConfirmation<br/>checks if exported values match CSO attributes]
    ConfirmPE --> CheckObsolete{CSO status<br/>= Obsolete?}

    %% --- Obsolete CSO path ---
    CheckObsolete -->|Yes| CheckJoined{CSO joined<br/>to MVO?}
    CheckJoined -->|No, NotJoined| QuietDelete[Delete CSO quietly<br/>Already disconnected]
    CheckJoined -->|No, other JoinType| DeleteOrphan[Create Deleted RPEI<br/>Queue CSO for deletion]
    CheckJoined -->|Yes| CheckOosAction{InboundOutOfScope<br/>Action?}

    CheckOosAction -->|RemainJoined| KeepJoin[Delete CSO but preserve<br/>MVO join state<br/>Once managed always managed]
    CheckOosAction -->|Disconnect| RemoveAttrs{ISyncEngine.DetermineOutOfScopeAction<br/>RemoveContributed<br/>AttributesOnObsoletion<br/>enabled on object type?}

    RemoveAttrs -->|Yes| RecallAttrs[Attribute Recall + re-election:<br/>Mark MVO attributes where<br/>ContributedBySystemId = this system for removal<br/>Re-elect next-priority surviving contributor<br/>ReElectSurvivingContributorsAsync<br/>Attribute with no survivor is cleared,<br/>or frozen if a deletion grace period is active]
    RemoveAttrs -->|No| BreakJoin
    RecallAttrs --> QueueRecall[Queue MVO for export evaluation<br/>with recalled + re-elected values<br/>Targets receive removals or a<br/>change-of-value to the survivor]
    QueueRecall --> BreakJoin[Break CSO-MVO join<br/>Set JoinType = NotJoined]
    BreakJoin --> EvalDeletion[ISyncEngine.EvaluateMvoDeletionRule<br/>Pure decision on MVO fate]
    EvalDeletion --> DeletionRule{MVO deletion<br/>rule?}

    DeletionRule -->|Manual| NoDelete[No automatic deletion<br/>MVO remains]
    DeletionRule -->|WhenLastConnector<br/>Disconnected| CheckRemaining{Remaining<br/>CSOs > 0?}
    CheckRemaining -->|Yes| NoDelete
    CheckRemaining -->|No| CheckGrace{Grace<br/>period?}

    DeletionRule -->|WhenAuthoritative<br/>SourceDisconnected| CheckAuth{Disconnecting system<br/>is authoritative?}
    CheckAuth -->|No| NoDelete
    CheckAuth -->|Yes| CheckGrace

    CheckGrace -->|0 or unset| ImmediateDelete[Queue MVO for<br/>immediate deletion<br/>at page flush]
    CheckGrace -->|> 0| DeferDelete[Mark MVO with<br/>LastConnectorDisconnectedDate<br/>Housekeeping deletes later]

    %% --- Non-obsolete CSO path ---
    CheckObsolete -->|No| CheckSyncRules{Active sync<br/>rules exist?}
    CheckSyncRules -->|No| Done([No changes])

    CheckSyncRules -->|Yes| CheckScope[Evaluate scoping criteria<br/>OR between groups, AND within group]
    CheckScope --> InScope{CSO in scope<br/>for any import rule?}

    InScope -->|No, rules have scoping| HandleOOS[Handle out of scope]
    HandleOOS --> OosJoined{CSO joined<br/>to MVO?}
    OosJoined -->|No| Done
    OosJoined -->|Yes| OosAction{InboundOutOfScope<br/>Action?}
    OosAction -->|RemainJoined| RetainJoin[OutOfScopeRetainJoin<br/>No Attribute Flow, preserve join]
    OosAction -->|Disconnect| DisconnectOOS[DisconnectedOutOfScope<br/>Recall contributed attributes<br/>if enabled on object type<br/>Break join, evaluate deletion]

    InScope -->|Yes| CheckMvo{CSO joined<br/>to MVO?}

    %% --- Join/Project path ---
    CheckMvo -->|No| AttemptJoin[Attempt Join<br/>For each import Synchronisation Rule:<br/>Find matching MVO by join criteria]
    AttemptJoin --> JoinResult{Match<br/>found?}

    JoinResult -->|No match| AttemptProject{ISyncEngine.EvaluateProjection<br/>Synchronisation Rule has<br/>ProjectToMetaverse = true?}
    AttemptProject -->|Yes| Project[Create new MVO<br/>Set type from Synchronisation Rule<br/>Link CSO to new MVO]
    AttemptProject -->|No| Done

    JoinResult -->|Single match| EstablishJoin[Establish join<br/>CSO.MetaverseObject = MVO<br/>Set JoinType + DateJoined]
    JoinResult -->|Multiple matches| AmbiguousError[AmbiguousMatch error<br/>RPEI with error]
    JoinResult -->|Match already joined| ExistingJoinError[CouldNotJoinDueToExistingJoin<br/>error RPEI]

    %% --- Attribute Flow path ---
    EstablishJoin --> AttrFlow
    Project --> AttrFlow
    CheckMvo -->|Yes| AttrFlow[ISyncEngine.FlowInboundAttributes<br/>Pass 1: scalar attributes only<br/>For each Synchronisation Rule mapping:<br/>- Direct: CSO attr --> MVO attr<br/>- Expression: evaluate --> MVO attr<br/>- ContributedBySystemId set on all new values<br/>Skip reference attributes]

    AttrFlow --> Priority[Attribute Priority resolution<br/>When an attribute has more than one<br/>contributing rule, pick a winner by<br/>configured priority order<br/>A contribution that loses to the<br/>incumbent is not applied<br/>Null is a value can assert null]
    Priority --> QueueRef[Queue CSO for deferred<br/>reference attribute processing<br/>Pass 2 at end of page]
    QueueRef --> ApplyChanges[ISyncEngine.ApplyPendingAttributeChanges<br/>Apply pending attribute<br/>additions and removals to MVO]
    ApplyChanges --> ValidateIntegrity[Data integrity validation<br/>on metaverse attribute operations]
    ValidateIntegrity --> QueueMvo[Queue MVO for batch<br/>persist and export evaluation]
    QueueMvo --> DriftDetect[Drift Detection<br/>Compare CSO values against<br/>expected MVO state<br/>Create corrective Pending Exports<br/>for EnforceState export rules]
    DriftDetect --> Result([Return change result:<br/>Projected / Joined / AttributeFlow / NoChanges])

    %% --- Error handling ---
    Entry -.->|SyncJoinException| JoinError[RPEI with specific error type<br/>AmbiguousMatch, ExistingJoin, etc.]
    Entry -.->|Unhandled Exception| UnhandledError[RPEI with UnhandledError<br/>+ stack trace<br/>Processing continues to next CSO]
```

## Key Design Decisions

- **Three-layer sync architecture (v0.7.1)**<br /> Sync decisions are split across `ISyncEngine` (pure domain logic: projection, Attribute Flow, deletion rules, export confirmation), `ISyncServer` (orchestration: matching, scoping, drift detection, export evaluation), and `ISyncRepository` (dedicated data access: bulk CSO/MVO writes, Pending Exports, RPEIs). This separation enables deterministic unit testing of business logic without I/O.

- **Two-pass Attribute Flow**<br /> Scalar attributes are processed first (pass 1 via `ISyncEngine.FlowInboundAttributes`), then reference attributes are deferred to a second pass after all CSOs in the page have MVOs. This ensures group member references can resolve to MVOs that were created later in the same page.

- **Batch persistence**<br /> MVO creates/updates, Pending Exports, and CSO deletions are all batched per-page via `ISyncRepository` bulk operations to reduce database round trips. This is critical for performance at scale.

- **No-net-change detection**<br /> Before creating Pending Exports, the system checks if the target CSO already has the expected values (using pre-cached data). This avoids unnecessary export operations.

- **Drift detection**<br /> After inbound Attribute Flow, `DriftDetectionService` checks whether CSO values match expected MVO state. If an `EnforceState` export rule exists and the CSO has drifted, a corrective Pending Export is created.

- **Attribute recall, re-election and hand-over via ContributedBySystemId**<br /> Every MVO attribute value tracks which Connected System contributed it. When a CSO is obsoleted, attributes contributed by that system are recalled (marked for removal from the MVO) when **both** of the following hold: the CSO type has `RemoveContributedAttributesOnObsoletion` enabled, and the MVO is not slated for immediate deletion (the immediate-deletion check avoids nugatory work when the MVO is about to be deleted at page flush, per #390). A configured deletion grace period no longer skips recall wholesale (Attribute Priority, #91): before clearing, `ReElectSurvivingContributorsAsync` hands each recalled attribute to the next-priority still-joined contributor where one survives, a change-of-value rather than a clear. Only an attribute with no surviving contributor is affected by the grace period: it is frozen (preserved) for the grace window rather than cleared, so identity-critical single-source values that feed expression-based exports (for example an LDAP Distinguished Name) are not cleared mid-grace. The recalled and re-elected values are queued for export evaluation so target systems receive the removals or the change-of-value; the only export-evaluation skip is for MVOs pending immediate deletion, whose Delete Pending Exports are created by `FlushPendingMvoDeletionsAsync` instead.

- **Cross-page reference resolution**<br /> After all pages are processed, CSOs with unresolved reference attributes are reloaded from the database. At this point, all MVOs exist, so cross-page references can be resolved. The standard flush pipeline (persist MVOs, evaluate exports, flush PEs) runs again for the resolved references.

- **Partition-scoped imports (v0.8.0, #353)**<br /> When a Run Profile specifies a `TargetPartitionId`, CSO counting and page loading are filtered to only that partition's scope. This allows large Connected Systems to be synchronised in targeted slices rather than processing the entire CSO population every time.

- **Error isolation**<br /> Each CSO is processed within its own try/catch. Errors create RPEIs but do not halt processing of remaining CSOs.

- **Cancellation safety**<br /> `CheckCancel` completes the current page flush before stopping. This ensures all in-progress MVOs, Pending Exports, and RPEIs are persisted; no work is lost on cancellation.

- **Per-page cache loading**<br /> The export evaluation cache is loaded per-page and cleared at page boundaries. This keeps memory consumption bounded regardless of total CSO count, preventing out-of-memory conditions on large Connected Systems.

- **Data integrity validation (v0.9.0, #465)**<br /> Metaverse attribute operations are validated for data integrity before being applied. This prevents silent corruption from malformed attribute values reaching the metaverse.

- **Two-pass per-CSO processing (v0.10.0)**<br /> Each page iterates over its CSOs twice. Pass 1 (`ProcessObsoleteAndExportConfirmationAsync`) handles pending-export confirmation and obsolete CSO teardown for every CSO, populating `_pendingDisconnectedMvoIds` before any Pass 2 work begins. Pass 2 (`ProcessActiveConnectedSystemObjectAsync`) runs join/projection/Attribute Flow only for non-obsolete CSOs. This ordering guarantees that Pass 2 join attempts see the complete set of disconnected MVOs from Pass 1 and skip them, avoiding race conditions where a CSO tries to join an MVO that is being torn down in the same page.

- **Cross-page RPEI merge (v0.10.0)**<br /> The unique index `IX_MetaverseObjectChanges_ActivityRunProfileExecutionItemId` means each RPEI can have at most one MvoChange parent. Cross-page reference resolution therefore merges new reference-attribute changes *under the existing MvoChange parent* rather than creating a second standalone RPEI for the same MVO. This resolves the previous ~2x RPEI duplication and the confusing split-outcome rows that appeared in activity detail when groups spanned multiple pages.

- **Two-phase MVO change persistence (v0.10.0)**<br /> MVO change records are built in-memory during the page (`CreatePendingMvoChangeObjectsAsync`) and persisted in a distinct `FlushPendingMvoChangesAsync` step that runs *before* the change tracker clear. Splitting creation from persistence avoids losing the in-memory records when the change tracker is cleared to bound memory at page boundaries.
