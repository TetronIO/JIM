# Full Synchronisation - CSO Processing Flow

This diagram shows the core decision tree for processing a single Connected System Object (CSO) during Full or Delta Synchronisation. This is the central flow of JIM's identity management engine.

Both Full Sync and Delta Sync use identical processing logic per-CSO. The only difference is CSO selection:
- **Full Sync**: processes ALL CSOs in the Connected System
- **Delta Sync**: processes only CSOs modified since `LastDeltaSyncCompletedAt`

## Overall Page Processing

```mermaid
flowchart TD
    Start([Start Sync]) --> Prepare[Prepare: count CSOs + pending exports\nLoad sync rules, object types\nBuild drift detection cache\nBuild export evaluation cache\nPre-load pending exports into dictionary]
    Prepare --> PageLoop{More CSO\npages?}

    PageLoop -->|Yes| LoadPage[Load page of CSOs\nwithout attributes for performance]
    LoadPage --> CsoLoop{More CSOs\nin page?}

    CsoLoop -->|Yes| CheckCancel{Cancellation\nrequested?}
    CheckCancel -->|Yes| Return([Return - activity\nfinalised by caller])
    CheckCancel -->|No| ProcessCso[ProcessConnectedSystemObjectAsync\nSee Per-CSO Processing below]
    ProcessCso --> IncrProgress[Increment ObjectsProcessed]
    IncrProgress --> CsoLoop

    CsoLoop -->|No| DeferredRef[Process deferred reference attributes\nSecond pass: resolve MVO references\nthat depend on other CSOs in page]
    DeferredRef --> PersistMvo[Batch persist MVO creates + updates]
    PersistMvo --> MvoChanges[Create MVO change objects\nfor audit trail]
    MvoChanges --> EvalExports[Batch evaluate outbound exports\nCreate pending exports for target systems]
    EvalExports --> FlushPE[Flush pending export\ncreate/delete/update operations]
    FlushPE --> FlushCSO[Flush obsolete CSO deletions]
    FlushCSO --> FlushMVO[Flush pending MVO deletions\n0-grace-period only]
    FlushMVO --> UpdateProgress[Update activity progress\nin database]
    UpdateProgress --> PageLoop

    PageLoop -->|No| Watermark[Update delta sync watermark\nLastDeltaSyncCompletedAt = UtcNow]
    Watermark --> End([Sync Complete])
```

## Per-CSO Processing

This is the decision tree within `ProcessConnectedSystemObjectAsync` for a single CSO.

```mermaid
flowchart TD
    Entry([ProcessConnectedSystemObjectAsync]) --> ConfirmPE[Confirm pending exports\nCheck if previously exported values\nnow match CSO attributes]
    ConfirmPE --> CheckObsolete{CSO status\n= Obsolete?}

    %% --- Obsolete CSO path ---
    CheckObsolete -->|Yes| CheckJoined{CSO joined\nto MVO?}
    CheckJoined -->|No, NotJoined| QuietDelete[Delete CSO quietly\nAlready disconnected]
    CheckJoined -->|No, other JoinType| DeleteOrphan[Create Deleted RPEI\nQueue CSO for deletion]
    CheckJoined -->|Yes| CheckOosAction{InboundOutOfScope\nAction?}

    CheckOosAction -->|RemainJoined| KeepJoin[Delete CSO but preserve\nMVO join state\nOnce managed always managed]
    CheckOosAction -->|Disconnect| RemoveAttrs{Remove contributed\nattributes setting?}

    RemoveAttrs -->|Yes| RecallAttrs[Remove contributed attributes\nfrom MVO\nQueue MVO for export evaluation]
    RemoveAttrs -->|No| BreakJoin
    RecallAttrs --> BreakJoin[Break CSO-MVO join\nSet JoinType = NotJoined]
    BreakJoin --> EvalDeletion[Evaluate MVO deletion rule]
    EvalDeletion --> DeletionRule{MVO deletion\nrule?}

    DeletionRule -->|Manual| NoDelete[No automatic deletion\nMVO remains]
    DeletionRule -->|WhenLastConnector\nDisconnected| CheckRemaining{Remaining\nCSOs > 0?}
    CheckRemaining -->|Yes| NoDelete
    CheckRemaining -->|No| CheckGrace{Grace\nperiod?}

    DeletionRule -->|WhenAuthoritative\nSourceDisconnected| CheckAuth{Disconnecting system\nis authoritative?}
    CheckAuth -->|No| NoDelete
    CheckAuth -->|Yes| CheckGrace

    CheckGrace -->|0 or unset| ImmediateDelete[Queue MVO for\nimmediate deletion\nat page flush]
    CheckGrace -->|> 0| DeferDelete[Mark MVO with\nLastConnectorDisconnectedDate\nHousekeeping deletes later]

    %% --- Non-obsolete CSO path ---
    CheckObsolete -->|No| CheckSyncRules{Active sync\nrules exist?}
    CheckSyncRules -->|No| Done([No changes])

    CheckSyncRules -->|Yes| CheckScope[Evaluate scoping criteria\nOR between groups, AND within group]
    CheckScope --> InScope{CSO in scope\nfor any import rule?}

    InScope -->|No, rules have scoping| HandleOOS[Handle out of scope]
    HandleOOS --> OosJoined{CSO joined\nto MVO?}
    OosJoined -->|No| Done
    OosJoined -->|Yes| OosAction{InboundOutOfScope\nAction?}
    OosAction -->|RemainJoined| RetainJoin[OutOfScopeRetainJoin\nNo attribute flow, preserve join]
    OosAction -->|Disconnect| DisconnectOOS[DisconnectedOutOfScope\nRemove contributed attributes\nBreak join, evaluate deletion]

    InScope -->|Yes| CheckMvo{CSO joined\nto MVO?}

    %% --- Join/Project path ---
    CheckMvo -->|No| AttemptJoin[Attempt Join\nFor each import sync rule:\nFind matching MVO by join criteria]
    AttemptJoin --> JoinResult{Match\nfound?}

    JoinResult -->|No match| AttemptProject{Sync rule has\nProjectToMetaverse\n= true?}
    AttemptProject -->|Yes| Project[Create new MVO\nSet type from sync rule\nLink CSO to new MVO]
    AttemptProject -->|No| Done

    JoinResult -->|Single match| EstablishJoin[Establish join\nCSO.MetaverseObject = MVO\nSet JoinType + DateJoined]
    JoinResult -->|Multiple matches| AmbiguousError[AmbiguousMatch error\nRPEI with error]
    JoinResult -->|Match already joined| ExistingJoinError[CouldNotJoinDueToExistingJoin\nerror RPEI]

    %% --- Attribute Flow path ---
    EstablishJoin --> AttrFlow
    Project --> AttrFlow
    CheckMvo -->|Yes| AttrFlow[Inbound Attribute Flow\nPass 1: scalar attributes only\nFor each sync rule mapping:\n- Direct: CSO attr --> MVO attr\n- Expression: evaluate --> MVO attr\nSkip reference attributes]

    AttrFlow --> QueueRef[Queue CSO for deferred\nreference attribute processing\nPass 2 at end of page]
    QueueRef --> ApplyChanges[Apply pending attribute\nadditions and removals to MVO]
    ApplyChanges --> QueueMvo[Queue MVO for batch\npersist and export evaluation]
    QueueMvo --> DriftDetect[Drift Detection\nCompare CSO values against\nexpected MVO state\nCreate corrective pending exports\nfor EnforceState export rules]
    DriftDetect --> Result([Return change result:\nProjected / Joined / AttributeFlow / NoChanges])

    %% --- Error handling ---
    Entry -.->|SyncJoinException| JoinError[RPEI with specific error type\nAmbiguousMatch, ExistingJoin, etc.]
    Entry -.->|Unhandled Exception| UnhandledError[RPEI with UnhandledError\n+ stack trace\nProcessing continues to next CSO]
```

## Key Design Decisions

- **Two-pass attribute flow**: Scalar attributes are processed first (pass 1), then reference attributes are deferred to a second pass after all CSOs in the page have MVOs. This ensures group member references can resolve to MVOs that were created later in the same page.

- **Batch persistence**: MVO creates/updates, pending exports, and CSO deletions are all batched per-page to reduce database round trips. This is critical for performance at scale.

- **No-net-change detection**: Before creating pending exports, the system checks if the target CSO already has the expected values (using pre-cached data). This avoids unnecessary export operations.

- **Drift detection**: After inbound attribute flow, the system checks whether CSO values match expected MVO state. If an `EnforceState` export rule exists and the CSO has drifted, a corrective pending export is created.

- **Error isolation**: Each CSO is processed within its own try/catch. Errors create RPEIs but do not halt processing of remaining CSOs.
