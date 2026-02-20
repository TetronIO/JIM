# Pending Export Lifecycle

> Generated against JIM v0.2.0 (`5a4788e9`). If the codebase has changed significantly since then, these diagrams may be out of date.

This diagram shows the full lifecycle of a Pending Export from creation during synchronisation, through export execution, to confirmation during a confirming import. Pending Exports are the mechanism by which JIM propagates changes from the metaverse to target connected systems.

## State Diagram

```mermaid
stateDiagram-v2
    [*] --> Pending: Created during Sync<br/>(export evaluation)

    Pending --> Executing: Export run starts<br/>batch marked executing

    Executing --> Exported: Connector reports<br/>success

    Executing --> ExportNotConfirmed: Connector reports<br/>failure (retryable)
    Executing --> Failed: ErrorCount >= MaxRetries

    Exported --> [*]: Confirming import confirms<br/>all attribute values match<br/>(PE deleted)

    Exported --> ExportNotConfirmed: Confirming import finds<br/>attribute values don't match

    ExportNotConfirmed --> Executing: Next export run<br/>(after NextRetryAt backoff)

    ExportNotConfirmed --> Pending: Sync re-evaluates<br/>and reasserts changes

    ExportNotConfirmed --> Failed: ErrorCount >= MaxRetries<br/>(permanent failure)

    Failed --> [*]: Manual intervention<br/>or PE deleted

    note right of Pending: Initial state.<br/>Created by EvaluateExportRules<br/>during Full/Delta Sync.
    note right of Exported: Awaiting confirmation.<br/>Confirming import checks if<br/>CSO attributes match expected values.
    note left of ExportNotConfirmed: Retryable failure.<br/>Will be re-exported after<br/>exponential backoff delay.
    note left of Failed: Permanent failure.<br/>Requires manual intervention.<br/>RPEI: ExportConfirmationFailed
```

## Full Lifecycle Across Operations

A Pending Export's journey typically spans three separate run profile executions:

```mermaid
flowchart LR
    subgraph "1. Sync (Full or Delta)"
        SyncStart[MVO attribute changes<br/>during inbound flow] --> EvalExport[EvaluateExportRules:<br/>Find export sync rules<br/>for MVO type]
        EvalExport --> InScope{MVO in scope<br/>for export rule?}
        InScope -->|No| EvalDeprov[Evaluate deprovisioning:<br/>Create Delete PE if CSO exists]
        InScope -->|Yes| MapAttrs[Map MVO attributes<br/>to CSO attributes<br/>via export sync rule mappings]
        MapAttrs --> NetChange{No-net-change<br/>detection}
        NetChange -->|CSO already current| Skip[Skip - no PE created<br/>Target already has correct values]
        NetChange -->|Changes needed| CheckExisting{Existing CSO<br/>in target system?}
        CheckExisting -->|Yes| CreateUpdatePE[Create PE:<br/>ChangeType = Update<br/>Status = Pending]
        CheckExisting -->|No| CreateCreatePE[Create PE:<br/>ChangeType = Create<br/>Status = Pending<br/>Provision new CSO]
    end

    subgraph "2. Export"
        GetExecutable[Get executable PEs:<br/>Status = Pending or<br/>ExportNotConfirmed<br/>NextRetryAt <= now] --> MarkExec[Mark batch:<br/>Status = Executing]
        MarkExec --> ConnExport[Connector executes<br/>export operations]
        ConnExport --> Success{Success?}
        Success -->|Yes, Create| ProvResult[Status = Exported<br/>Capture new external ID<br/>RPEI: Provisioned]
        Success -->|Yes, Update| ExpResult[Status = Exported<br/>RPEI: Exported]
        Success -->|Yes, Delete| DeprovResult[Delete PE + CSO<br/>RPEI: Deprovisioned]
        Success -->|No| FailResult[Increment ErrorCount<br/>Set NextRetryAt<br/>Status = ExportNotConfirmed]
    end

    subgraph "3. Confirming Import"
        ImportData[Import fresh data<br/>from target system] --> Reconcile[PendingExportReconciliationService:<br/>Compare each PE attribute<br/>against imported CSO values]
        Reconcile --> AllMatch{All attributes<br/>confirmed?}
        AllMatch -->|Yes| DeletePE[Delete PE<br/>Export confirmed<br/>PE lifecycle complete]
        AllMatch -->|Partial| PartialConfirm[Remove confirmed attributes<br/>Keep unconfirmed<br/>Change Create to Update<br/>Status = ExportNotConfirmed]
        AllMatch -->|None| NoneConfirm[Keep all attributes<br/>Increment error count<br/>Status = ExportNotConfirmed]
    end

    CreateUpdatePE --> GetExecutable
    CreateCreatePE --> GetExecutable
    EvalDeprov --> GetExecutable
    ProvResult --> ImportData
    ExpResult --> ImportData
    FailResult -.->|Next export run<br/>after backoff| GetExecutable
    PartialConfirm -.->|Next export run| GetExecutable
    NoneConfirm -.->|Next export run| GetExecutable
```

## Pending Export Confirmation During Sync

During Full/Delta Sync, pending exports are also checked for confirmation (separate from the confirming import path above). This happens in `ProcessPendingExport` within `SyncTaskProcessorBase`:

```mermaid
flowchart TD
    Start([ProcessPendingExport<br/>for each CSO]) --> LookupPE[Lookup pending exports<br/>for this CSO from<br/>pre-loaded dictionary]
    LookupPE --> HasPE{PE exists<br/>for CSO?}
    HasPE -->|No| Done([Skip])

    HasPE -->|Yes| CheckStatus{PE<br/>status?}
    CheckStatus -->|Pending| SkipPending[Skip - not yet exported<br/>Nothing to confirm]
    CheckStatus -->|Exported| SkipExported[Skip - awaiting<br/>confirmation via import<br/>reconciliation service]
    CheckStatus -->|ExportNotConfirmed| CompareAttrs[For each attribute change:<br/>Compare expected value<br/>against CSO current value]

    CompareAttrs --> MatchResult{All attributes<br/>confirmed?}
    MatchResult -->|All confirmed| QueueDelete[Queue PE for<br/>batch deletion]
    MatchResult -->|Some confirmed| QueuePartialUpdate[Remove confirmed attributes<br/>If Create, change to Update<br/>Increment error count<br/>Queue for batch update]
    MatchResult -->|None confirmed| QueueFullUpdate[Increment error count<br/>Queue for batch update]

    QueueDelete --> Done
    QueuePartialUpdate --> Done
    QueueFullUpdate --> Done
```

## Attribute-Level Status Tracking

Each attribute change within a Pending Export has its own status, enabling partial confirmation:

```mermaid
stateDiagram-v2
    [*] --> Pending: Attribute change created

    Pending --> ExportedPendingConfirmation: Export run executes<br/>successfully

    ExportedPendingConfirmation --> [*]: Confirming import<br/>confirms value matches<br/>(attribute change removed from PE)

    ExportedPendingConfirmation --> ExportedNotConfirmed: Confirming import<br/>finds value mismatch

    ExportedNotConfirmed --> Pending: Reasserted on<br/>next export run

    ExportedNotConfirmed --> Failed: Max retries exceeded

    Failed --> [*]: Manual intervention
```

## Change Types

```
+--------+-------------------+---------------------------------------------+
| Type   | When Created      | What Happens                                |
+--------+-------------------+---------------------------------------------+
| Create | No CSO exists in  | Provisions new object in target system      |
|        | target system for | Connector creates object + sets attributes  |
|        | this MVO          | PE captures DN template + all attributes    |
+--------+-------------------+---------------------------------------------+
| Update | CSO exists but    | Updates existing object attributes          |
|        | attributes differ | Only changed attributes are included        |
|        | from MVO values   | No-net-change detection avoids unnecessary  |
+--------+-------------------+---------------------------------------------+
| Delete | MVO deletion rule | Removes object from target system           |
|        | triggered, or MVO | Created by EvaluateMvoDeletionAsync or      |
|        | falls out of      | EvaluateOutOfScopeExportsAsync              |
|        | export scope      | PE deleted along with CSO on success        |
+--------+-------------------+---------------------------------------------+
```

## Drift Detection Creates Corrective Exports

During sync, drift detection can also create Pending Exports:

```mermaid
flowchart TD
    DriftCheck[EvaluateDriftAndEnforceState<br/>during sync CSO processing] --> CompareCSO[Compare CSO attribute values<br/>against expected MVO values<br/>using EnforceState export rules]
    CompareCSO --> Drifted{CSO value<br/>differs from<br/>expected?}
    Drifted -->|No| NoDrift([No action])
    Drifted -->|Yes| CheckContributor{Is this system<br/>a legitimate contributor<br/>for this attribute?}
    CheckContributor -->|Yes| LegitChange([Skip - legitimate import<br/>from authoritative source])
    CheckContributor -->|No| CreateCorrective[Create corrective PE:<br/>ChangeType = Update<br/>Status = Pending<br/>RPEI: DriftCorrection]
```

## Key Design Decisions

- **Three-operation lifecycle**: A pending export typically spans Sync (creation), Export (execution), and Confirming Import (confirmation). This design ensures changes are verified end-to-end.

- **Partial confirmation**: Individual attribute changes can be confirmed independently. If 3 out of 5 attributes match the target system, only the 2 unconfirmed attributes remain on the pending export for retry.

- **Create-to-Update demotion**: When a Create PE is partially confirmed (object was created but some attributes didn't take), it's demoted to an Update PE. This prevents the next export from trying to create an already-existing object.

- **No-net-change detection**: Before creating a PE during sync, the system checks if the target CSO already has the expected values (using pre-cached data in `ExportEvaluationCache`). This avoids unnecessary export operations and reduces connector load.

- **Drift correction**: When `EnforceState` is enabled on an export sync rule and the CSO has values that don't match the MVO, a corrective PE is created to reassert the correct values. This detects and corrects unauthorised changes made directly in target systems.

- **Exponential backoff**: Failed exports use increasing retry delays (`NextRetryAt`) to avoid hammering a target system that's experiencing issues.
