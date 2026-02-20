# MVO Deletion and Grace Period

> Generated against JIM v0.2.0 (`988472e3`). If the codebase has changed significantly since then, these diagrams may be out of date.

This diagram shows the full lifecycle of Metaverse Object (MVO) deletion, from the trigger event (CSO disconnection) through deletion rule evaluation, grace period handling, and deferred housekeeping cleanup.

## Deletion Rules

| Rule | Value | Trigger | Behaviour |
|------|-------|---------|-----------|
| Manual | 0 | Never | MVO is never automatically deleted. Requires admin intervention. |
| WhenLastConnectorDisconnected | 1 | All CSOs disconnected | MVO deleted when no CSOs remain joined. Default rule. |
| WhenAuthoritativeSourceDisconnected | 2 | Specified system disconnects | MVO deleted when ANY system in `DeletionTriggerConnectedSystemIds` disconnects, even if other CSOs remain. |

## Trigger: CSO Disconnection During Sync

```mermaid
flowchart TD
    Start([CSO becomes obsolete<br/>during sync]) --> Joined{CSO joined<br/>to MVO?}

    Joined -->|No, NotJoined| QuietDelete[Delete CSO quietly<br/>Already disconnected]
    Joined -->|No, other JoinType| DeleteWithRPEI[Create Deleted RPEI<br/>Queue CSO for deletion]

    Joined -->|Yes| OosAction{InboundOutOfScope<br/>Action?}

    OosAction -->|RemainJoined| KeepJoin[Delete CSO<br/>Preserve MVO join state<br/>Once managed always managed<br/>No deletion evaluation]

    OosAction -->|Disconnect| RemoveAttrs{RemoveContributed<br/>Attributes enabled<br/>on object type?}
    RemoveAttrs -->|Yes| RecallAttrs[Remove contributed attributes<br/>from MVO<br/>Queue MVO for export evaluation]
    RemoveAttrs -->|No| BreakJoin
    RecallAttrs --> BreakJoin[Break CSO-MVO join<br/>Set JoinType = NotJoined]

    BreakJoin --> CountRemaining[Count remaining CSOs<br/>before break, subtract 1]
    CountRemaining --> EvalDeletion[ProcessMvoDeletionRuleAsync]
```

## Deletion Rule Evaluation

```mermaid
flowchart TD
    Start([ProcessMvoDeletionRuleAsync]) --> CheckOrigin{MVO Origin?}
    CheckOrigin -->|Internal| Protected([Skip - internal MVOs<br/>protected from automatic deletion])

    CheckOrigin -->|Projected| GetRule{Deletion<br/>rule?}

    GetRule -->|Manual| NoAction([No automatic deletion])

    GetRule -->|WhenLastConnector<br/>Disconnected| CheckRemaining{Remaining<br/>CSOs > 0?}
    CheckRemaining -->|Yes| NoActionYet([Not yet - other CSOs<br/>still connected])
    CheckRemaining -->|No| MarkForDeletion[MarkMvoForDeletionAsync]

    GetRule -->|WhenAuthoritative<br/>SourceDisconnected| CheckTriggers{DeletionTrigger<br/>ConnectedSystemIds<br/>configured?}
    CheckTriggers -->|Empty| FallbackRule[Fall back to<br/>WhenLastConnectorDisconnected<br/>behaviour]
    FallbackRule --> CheckRemaining

    CheckTriggers -->|Has entries| IsAuthSource{Disconnecting system<br/>in trigger list?}
    IsAuthSource -->|No| NoAction2([Not an authoritative source<br/>No deletion])
    IsAuthSource -->|Yes| MarkForDeletion
```

## Grace Period Decision

```mermaid
flowchart TD
    Mark([MarkMvoForDeletionAsync]) --> DedupCheck{MVO already queued<br/>for deletion<br/>in this page?}
    DedupCheck -->|Yes| Skip([Skip - prevent<br/>double-queueing])

    DedupCheck -->|No| CheckGrace{Grace period<br/>on MVO type?}

    CheckGrace -->|Null or TimeSpan.Zero| Immediate[Add MVO to<br/>pendingMvoDeletions batch<br/>Deleted at page boundary]

    CheckGrace -->|> 0| Deferred[Set LastConnectorDisconnectedDate<br/>= DateTime.UtcNow<br/>Capture initiator info:<br/>DeletionInitiatedByType<br/>DeletionInitiatedById<br/>DeletionInitiatedByName<br/>Persist via UpdateMetaverseObjectAsync]
    Deferred --> WaitForHousekeeping([Deferred to housekeeping<br/>Eligible after grace period expires])
```

## Immediate Deletion (Zero Grace Period)

```mermaid
flowchart TD
    PageEnd([Page flush:<br/>FlushPendingMvoDeletionsAsync]) --> Loop{More MVOs<br/>in batch?}
    Loop -->|No| Done([Done])

    Loop -->|Yes| EvalExports[EvaluateMvoDeletionAsync:<br/>Create delete pending exports<br/>for provisioned target CSOs]
    EvalExports --> DeleteMVO[DeleteMetaverseObjectAsync<br/>with initiator info]
    DeleteMVO --> Success{Success?}

    Success -->|Yes| Loop
    Success -->|No| Fallback[Set LastConnectorDisconnectedDate<br/>as fallback so housekeeping<br/>can retry later]
    Fallback --> Loop
```

## Deferred Deletion (Housekeeping)

```mermaid
flowchart TD
    Idle([Worker idle<br/>every 60 seconds]) --> Query[GetMetaverseObjectsEligibleForDeletionAsync<br/>Max 50 per cycle]

    Query --> Criteria[Eligibility criteria:<br/>1. Origin = Projected<br/>2. LastConnectorDisconnectedDate != null<br/>3. Grace period expired<br/>4. Rule-specific checks]
    Criteria --> RuleCheck{Deletion<br/>rule?}

    RuleCheck -->|WhenLastConnector<br/>Disconnected| NoCSOs{No CSOs<br/>remaining?}
    NoCSOs -->|Yes| Eligible
    NoCSOs -->|No| NotEligible([Skip - CSOs reconnected<br/>during grace period])

    RuleCheck -->|WhenAuthoritative<br/>SourceDisconnected| Eligible[Always eligible once marked<br/>May still have target CSOs]

    Eligible --> Loop{More eligible<br/>MVOs?}
    Loop -->|No| Done([Done])

    Loop -->|Yes| EvalExports[EvaluateMvoDeletionAsync:<br/>Create delete pending exports<br/>for remaining provisioned CSOs]
    EvalExports --> DeleteMVO[DeleteMetaverseObjectAsync<br/>Uses ORIGINAL initiator info<br/>from when MVO was marked]
    DeleteMVO --> Result{Success?}
    Result -->|Yes| Loop
    Result -->|No| LogError[Log error<br/>Continue with other MVOs<br/>Will retry next cycle]
    LogError --> Loop
```

## State Diagram

```mermaid
stateDiagram-v2
    [*] --> Normal: MVO created via<br/>projection or internally

    Normal --> MarkedForDeletion: CSO disconnects,<br/>deletion rule triggers,<br/>grace period > 0

    Normal --> [*]: CSO disconnects,<br/>deletion rule triggers,<br/>grace period = 0<br/>(immediate deletion)

    MarkedForDeletion --> Normal: CSO reconnects<br/>during grace period<br/>(grace period resets)

    MarkedForDeletion --> [*]: Grace period expires,<br/>housekeeping deletes MVO

    note right of Normal
        Origin = Projected or Internal.
        Internal MVOs are protected
        from automatic deletion.
    end note
    note left of MarkedForDeletion
        LastConnectorDisconnectedDate set.
        DeletionEligibleDate =
        DisconnectedDate + GracePeriod.
        Original initiator info preserved.
    end note
```

## Key Design Decisions

- **Internal MVO protection**: MVOs with `Origin = Internal` (admin accounts, service accounts created directly in JIM) are never subject to automatic deletion, regardless of the deletion rule configured on the object type.

- **Grace period reconnection**: If a CSO reconnects to an MVO during the grace period, the MVO is no longer eligible for deletion. The `LastConnectorDisconnectedDate` remains set, but the eligibility query checks for remaining CSOs, so the MVO won't be deleted.

- **Initiator preservation**: When an MVO is marked for deferred deletion, the original initiator info (who/what caused the disconnection) is captured on the MVO. When housekeeping eventually deletes it, this original initiator is used in the audit trail — not "housekeeping" or "system".

- **Export cleanup before deletion**: Both immediate and housekeeping deletion paths call `EvaluateMvoDeletionAsync()` before the actual deletion. This creates delete pending exports for any provisioned target system CSOs, ensuring the external system is cleaned up.

- **Fallback on failure**: If immediate deletion fails (e.g., database error), the system sets `LastConnectorDisconnectedDate` as a fallback. This ensures housekeeping will pick up the MVO for retry on the next cycle, rather than losing the deletion intent.

- **Capped housekeeping**: Housekeeping processes a maximum of 50 MVOs per cycle (every 60 seconds). This prevents large deletion backlogs from monopolising the worker during idle time.

- **WhenAuthoritativeSourceDisconnected fallback**: If `DeletionTriggerConnectedSystemIds` is empty, the rule falls back to `WhenLastConnectorDisconnected` behaviour. This prevents misconfiguration from causing unexpected deletions.

- **Dedup within page**: Multiple CSOs from the same MVO can disconnect in the same sync page. The dedup check in `MarkMvoForDeletionAsync` prevents the same MVO from being queued for immediate deletion twice.
