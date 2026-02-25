# Full Synchronisation - CSO Processing Flow

> Generated against JIM v0.3.0 (`0d1c88e9`). If the codebase has changed significantly since then, these diagrams may be out of date.

This diagram shows the core decision tree for processing a single Connected System Object (CSO) during Full or Delta Synchronisation. This is the central flow of JIM's identity management engine.

Both Full Sync and Delta Sync use identical processing logic per-CSO. The only difference is CSO selection:
- **Full Sync**: processes ALL CSOs in the Connected System
- **Delta Sync**: processes only CSOs modified since `LastDeltaSyncCompletedAt`

## Overall Page Processing

```mermaid
flowchart TD
    Start([Start Sync]) --> Prepare[Prepare: count CSOs + pending exports<br/>Load sync rules, object types<br/>Build drift detection cache<br/>Build export evaluation cache<br/>Pre-load pending exports into dictionary]
    Prepare --> PageLoop{More CSO<br/>pages?}

    PageLoop -->|Yes| LoadPage[Load page of CSOs<br/>without attributes for performance]
    LoadPage --> CsoLoop{More CSOs<br/>in page?}

    CsoLoop -->|Yes| CheckCancel{Cancellation<br/>requested?}
    CheckCancel -->|Yes| Return([Return - activity<br/>finalised by caller])
    CheckCancel -->|No| ProcessCso[ProcessConnectedSystemObjectAsync<br/>See Per-CSO Processing below]
    ProcessCso --> IncrProgress[Increment ObjectsProcessed]
    IncrProgress --> CsoLoop

    CsoLoop -->|No| DeferredRef[Process deferred reference attributes<br/>Second pass: resolve MVO references<br/>that depend on other CSOs in page]
    DeferredRef --> PersistMvo[Batch persist MVO creates + updates]
    PersistMvo --> MvoChanges[Create MVO change objects<br/>for audit trail]
    MvoChanges --> EvalExports[Batch evaluate outbound exports<br/>Create pending exports for target systems]
    EvalExports --> FlushPE[Flush pending export<br/>create/delete/update operations]
    FlushPE --> FlushCSO[Flush obsolete CSO deletions]
    FlushCSO --> FlushMVO[Flush pending MVO deletions<br/>0-grace-period only]
    FlushMVO --> UpdateProgress[Update activity progress<br/>in database]
    UpdateProgress --> PageLoop

    PageLoop -->|No| CrossPage[Cross-page reference resolution<br/>Reload CSOs with unresolved references<br/>Resolve MVO references across page boundaries<br/>Re-run flush pipeline for resolved references]
    CrossPage --> Watermark[Update delta sync watermark<br/>LastDeltaSyncCompletedAt = UtcNow]
    Watermark --> End([Sync Complete])
```

## Per-CSO Processing

This is the decision tree within `ProcessConnectedSystemObjectAsync` for a single CSO.

```mermaid
flowchart TD
    Entry([ProcessConnectedSystemObjectAsync]) --> ConfirmPE[Confirm pending exports<br/>Check if previously exported values<br/>now match CSO attributes]
    ConfirmPE --> CheckObsolete{CSO status<br/>= Obsolete?}

    %% --- Obsolete CSO path ---
    CheckObsolete -->|Yes| CheckJoined{CSO joined<br/>to MVO?}
    CheckJoined -->|No, NotJoined| QuietDelete[Delete CSO quietly<br/>Already disconnected]
    CheckJoined -->|No, other JoinType| DeleteOrphan[Create Deleted RPEI<br/>Queue CSO for deletion]
    CheckJoined -->|Yes| CheckOosAction{InboundOutOfScope<br/>Action?}

    CheckOosAction -->|RemainJoined| KeepJoin[Delete CSO but preserve<br/>MVO join state<br/>Once managed always managed]
    CheckOosAction -->|Disconnect| RemoveAttrs{RemoveContributed<br/>AttributesOnObsoletion<br/>enabled on object type?}

    RemoveAttrs -->|Yes| RecallAttrs[Attribute Recall:<br/>Find MVO attributes where<br/>ContributedBySystemId = this system<br/>Add to PendingAttributeValueRemovals<br/>Track removedAttributes set]
    RemoveAttrs -->|No| BreakJoin
    RecallAttrs --> QueueRecall[Queue MVO for export evaluation<br/>with removedAttributes set<br/>Pure recalls skip export evaluation]
    QueueRecall --> BreakJoin[Break CSO-MVO join<br/>Set JoinType = NotJoined]
    BreakJoin --> EvalDeletion[Evaluate MVO deletion rule]
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
    OosAction -->|RemainJoined| RetainJoin[OutOfScopeRetainJoin<br/>No attribute flow, preserve join]
    OosAction -->|Disconnect| DisconnectOOS[DisconnectedOutOfScope<br/>Recall contributed attributes<br/>if enabled on object type<br/>Break join, evaluate deletion]

    InScope -->|Yes| CheckMvo{CSO joined<br/>to MVO?}

    %% --- Join/Project path ---
    CheckMvo -->|No| AttemptJoin[Attempt Join<br/>For each import sync rule:<br/>Find matching MVO by join criteria]
    AttemptJoin --> JoinResult{Match<br/>found?}

    JoinResult -->|No match| AttemptProject{Sync rule has<br/>ProjectToMetaverse<br/>= true?}
    AttemptProject -->|Yes| Project[Create new MVO<br/>Set type from sync rule<br/>Link CSO to new MVO]
    AttemptProject -->|No| Done

    JoinResult -->|Single match| EstablishJoin[Establish join<br/>CSO.MetaverseObject = MVO<br/>Set JoinType + DateJoined]
    JoinResult -->|Multiple matches| AmbiguousError[AmbiguousMatch error<br/>RPEI with error]
    JoinResult -->|Match already joined| ExistingJoinError[CouldNotJoinDueToExistingJoin<br/>error RPEI]

    %% --- Attribute Flow path ---
    EstablishJoin --> AttrFlow
    Project --> AttrFlow
    CheckMvo -->|Yes| AttrFlow[Inbound Attribute Flow<br/>Pass 1: scalar attributes only<br/>For each sync rule mapping:<br/>- Direct: CSO attr --> MVO attr<br/>- Expression: evaluate --> MVO attr<br/>- ContributedBySystemId set on all new values<br/>Skip reference attributes]

    AttrFlow --> QueueRef[Queue CSO for deferred<br/>reference attribute processing<br/>Pass 2 at end of page]
    QueueRef --> ApplyChanges[Apply pending attribute<br/>additions and removals to MVO]
    ApplyChanges --> QueueMvo[Queue MVO for batch<br/>persist and export evaluation]
    QueueMvo --> DriftDetect[Drift Detection<br/>Compare CSO values against<br/>expected MVO state<br/>Create corrective pending exports<br/>for EnforceState export rules]
    DriftDetect --> Result([Return change result:<br/>Projected / Joined / AttributeFlow / NoChanges])

    %% --- Error handling ---
    Entry -.->|SyncJoinException| JoinError[RPEI with specific error type<br/>AmbiguousMatch, ExistingJoin, etc.]
    Entry -.->|Unhandled Exception| UnhandledError[RPEI with UnhandledError<br/>+ stack trace<br/>Processing continues to next CSO]
```

## Key Design Decisions

- **Two-pass attribute flow**: Scalar attributes are processed first (pass 1), then reference attributes are deferred to a second pass after all CSOs in the page have MVOs. This ensures group member references can resolve to MVOs that were created later in the same page.

- **Batch persistence**: MVO creates/updates, pending exports, and CSO deletions are all batched per-page to reduce database round trips. This is critical for performance at scale.

- **No-net-change detection**: Before creating pending exports, the system checks if the target CSO already has the expected values (using pre-cached data). This avoids unnecessary export operations.

- **Drift detection**: After inbound attribute flow, the system checks whether CSO values match expected MVO state. If an `EnforceState` export rule exists and the CSO has drifted, a corrective pending export is created.

- **Attribute recall via ContributedBySystemId**: Every MVO attribute value tracks which connected system contributed it. When a CSO is obsoleted and `RemoveContributedAttributesOnObsoletion` is enabled on the object type, all attributes contributed by that system are recalled (removed from the MVO). The `removedAttributes` set is passed to export evaluation, where pure recall operations (all changes are removals) skip export evaluation entirely to avoid expression mapping errors against incomplete data.

- **Cross-page reference resolution**: After all pages are processed, CSOs with unresolved reference attributes are reloaded from the database. At this point, all MVOs exist, so cross-page references can be resolved. The standard flush pipeline (persist MVOs, evaluate exports, flush PEs) runs again for the resolved references.

- **Error isolation**: Each CSO is processed within its own try/catch. Errors create RPEIs but do not halt processing of remaining CSOs.
