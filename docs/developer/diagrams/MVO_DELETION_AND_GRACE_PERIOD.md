# MVO Deletion and Grace Period

> Last updated: 2026-07-10, JIM v0.13.0

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

    OosAction -->|Disconnect| RemoveAttrs{RemoveContributed<br/>AttributesOnObsoletion<br/>enabled on object type?}
    RemoveAttrs -->|Yes| RecallAttrs[Attribute Recall + re-election:<br/>Mark MVO attributes where<br/>ContributedBySystemId = this system for removal<br/>Re-elect next-priority surviving contributor<br/>Attribute with no survivor is cleared,<br/>or frozen if a deletion grace period is active]
    RemoveAttrs -->|No| BreakJoin
    RecallAttrs --> QueueRecall[Queue MVO for export evaluation<br/>with recalled + re-elected values<br/>Targets receive removals or a<br/>change-of-value to the survivor]
    QueueRecall --> BreakJoin[Break CSO-MVO join<br/>Set JoinType = NotJoined]

    BreakJoin --> CountRemaining[Count remaining CSOs<br/>before break, subtract 1]
    CountRemaining --> EvalDeletion[ISyncEngine.EvaluateMvoDeletionRule<br/>Pure decision on MVO fate]
```

## Deletion Rule Evaluation

```mermaid
flowchart TD
    Start([ISyncEngine.EvaluateMvoDeletionRule]) --> CheckOrigin{MVO Origin?}
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
    PageEnd([Page flush:<br/>FlushPendingMvoDeletionsAsync]) --> Capture[CaptureReferenceRecallContextAsync:<br/>Record who references the candidates and the<br/>candidates' per-system resolved reference values]
    Capture --> Loop{More MVOs<br/>in batch?}
    Loop -->|No| Recall[StageReferenceRecallExportsAsync:<br/>Stage membership-removal Pending Exports<br/>for objects that referenced the deleted MVOs]
    Recall --> Done([Done])

    Loop -->|Yes| EvalExports[EvaluateMvoDeletionAsync:<br/>Create delete Pending Exports<br/>for provisioned target CSOs]
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

    Eligible --> Capture[CaptureReferenceRecallContextAsync:<br/>Record who references the candidates and the<br/>candidates' per-system resolved reference values]
    Capture --> Loop{More eligible<br/>MVOs?}
    Loop -->|No| Recall[StageReferenceRecallExportsAsync:<br/>Stage membership-removal Pending Exports<br/>for objects that referenced the deleted MVOs]
    Recall --> Done([Done])

    Loop -->|Yes| EvalExports[EvaluateMvoDeletionAsync:<br/>Create delete Pending Exports<br/>for remaining provisioned CSOs]
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

- **Internal MVO protection**<br /> MVOs with `Origin = Internal` (admin accounts, service accounts created directly in JIM) are never subject to automatic deletion, regardless of the deletion rule configured on the object type.

- **Grace period reconnection**<br /> If a CSO reconnects to an MVO during the grace period, the MVO is no longer eligible for deletion. The `LastConnectorDisconnectedDate` remains set, but the eligibility query checks for remaining CSOs, so the MVO won't be deleted.

- **Initiator preservation**<br /> When an MVO is marked for deferred deletion, the original initiator info (who/what caused the disconnection) is captured on the MVO. When housekeeping eventually deletes it, this original initiator is used in the audit trail, not "housekeeping" or "system".

- **Export cleanup before deletion**<br /> Both immediate and housekeeping deletion paths call `EvaluateMvoDeletionAsync()` before the actual deletion. This creates delete Pending Exports for any provisioned target system CSOs, ensuring the external system is cleaned up.

- **Reference recall after deletion (#908)**<br /> Both deletion paths also stage membership-removal Pending Exports for every Metaverse Object that referenced a deleted one (for example groups whose Static Members included a deleted leaver). The referencing linkage and the deleted objects' per-system resolved reference values (for example target DNs) are captured via `CaptureReferenceRecallContextAsync()` before deletion, because `DeleteMetaverseObjectAsync()` nulls the reference FKs and `EvaluateMvoDeletionAsync()` disconnects the CSOs. After the deletions, `StageReferenceRecallExportsAsync()` evaluates each referencing object once with every reference it lost in the batch, staging Remove changes whose values are pre-resolved at staging time; export-time resolution walks MVO to joined CSO and can never succeed for a deleted object. Without this recall, targets without referential integrity would keep deleted users as group members forever, because the referencing groups' CSOs never change and the unchanged-skip means no sync re-evaluates them.

- **Fallback on failure**<br /> If immediate deletion fails (e.g., database error), the system sets `LastConnectorDisconnectedDate` as a fallback. This ensures housekeeping will pick up the MVO for retry on the next cycle, rather than losing the deletion intent.

- **Capped housekeeping**<br /> Housekeeping processes a maximum of 50 MVOs per cycle (every 60 seconds). This prevents large deletion backlogs from monopolising the worker during idle time.

- **WhenAuthoritativeSourceDisconnected fallback**<br /> If `DeletionTriggerConnectedSystemIds` is empty, the rule falls back to `WhenLastConnectorDisconnected` behaviour. This prevents misconfiguration from causing unexpected deletions.

- **Dedup within page**<br /> Multiple CSOs from the same MVO can disconnect in the same sync page. The dedup check in `MarkMvoForDeletionAsync` prevents the same MVO from being queued for immediate deletion twice.

- **Attribute recall, re-election and hand-over via ContributedBySystemId**<br /> MVO attribute values contributed by the disconnecting system (identified by `ContributedBySystemId`) are recalled when **both** of the following hold: `RemoveContributedAttributesOnObsoletion` is enabled on the CSO type, and the MVO is not slated for immediate deletion (the immediate-deletion check avoids nugatory work when the MVO is about to be deleted at page flush, per #390). A configured deletion grace period no longer skips recall wholesale (Attribute Priority, #91): before clearing, a still-joined next-priority contributor is re-elected for each recalled attribute where one survives, so an authoritative source leaving hands the attribute to the next source (a change-of-value) rather than blanking it. Only an attribute with no surviving contributor is affected by the grace period: it is frozen (preserved) for the grace window rather than cleared, so identity-critical single-source values are not lost mid-grace. The diagram shows only the first gate for clarity. Recalled and re-elected values are queued for export evaluation so target systems receive the removals or the change-of-value; the only skip is for MVOs pending immediate deletion, whose Delete Pending Exports are created by `FlushPendingMvoDeletionsAsync`.

- **IsPendingDeletion**<br /> An MVO is considered pending deletion when it has `LastConnectorDisconnectedDate` set, has `Origin = Projected` (not `Internal`), and its type's deletion rule is either `WhenLastConnectorDisconnected` or `WhenAuthoritativeSourceDisconnected`.
