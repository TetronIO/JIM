# Unique Value Generation and Collision Remediation

- **Status:** Doing (release 1, Phase 1 in progress)
- **Issue:** [#242](https://github.com/TetronIO/JIM/issues/242)
- **PRD:** [`../../prd/doing/PRD_UNIQUE_VALUE_GENERATION.md`](../../prd/doing/PRD_UNIQUE_VALUE_GENERATION.md)
- **Depends on (release 2):** [`../../prd/PRD_METAVERSE_DERIVED_ATTRIBUTE_FLOWS.md`](../../prd/PRD_METAVERSE_DERIVED_ATTRIBUTE_FLOWS.md)
- **Related:** [#399](https://github.com/TetronIO/JIM/issues/399) attribute provenance display (delivers the Metaverse Object page chips), [#614](https://github.com/TetronIO/JIM/issues/614) internal Metaverse Object management (Set Value and Generate a new value deferred there), [#223](https://github.com/TetronIO/JIM/issues/223) Initial Export Only (per-mapping flag precedent), [#1121](https://github.com/TetronIO/JIM/issues/1121) Initial Password Provisioning (parked state, release on configuration change, queue-and-follow), [#1087](https://github.com/TetronIO/JIM/issues/1087) / [#1495](https://github.com/TetronIO/JIM/issues/1495) causality views, [#1079](https://github.com/TetronIO/JIM/issues/1079) optimistic export apply, [#91](https://github.com/TetronIO/JIM/issues/91) attribute priority, [#1361](https://github.com/TetronIO/JIM/issues/1361) Missing Input Behaviour, [#892](https://github.com/TetronIO/JIM/issues/892) Temporal Scope Reconciler (review flag)
- **UI mockups:** [Unique Value Generation: Design and Mockups](https://claude.ai/artifact/G9R6cK7WR7QwmPukctFpkb) · [Generated Value Options](https://claude.ai/artifact/AnigVtXRxx1t71qVS41yMr) (release 1 form) · [Linked Identifiers](https://claude.ai/artifact/KEbCieWusy8aoxnZqkMagD)
- **Plan explainer:** [Unique Value Generation Plan](https://claude.ai/artifact/WsfoqDR7PrwiQZLQBPCt9q) (what changes per layer, the data model, the three data flows, the assignment lifecycle and the releases)
- **Last Updated:** 2026-09-23 (decision 6 revised: inline per-object resolution with run-scoped caches); 2026-09-19 (adversarial review applied; four-release delivery; uniqueness tokens, counters and the retired values register added)

## Overview

JIM cannot generate the identifiers an IDAM team owns (account name, employee number, email, UPN); the demo data hides that by putting them in the HR feed. This plan delivers, in four releases: a "JIM generates it" source type on import and export Attribute Flows whose value is a base expression plus a **uniqueness token** (a number only if the value is taken, a forward-only **sequence** with optional fixed width, or a **random** token); a unique value service with four availability gates; a JIM-owned sticky **generated value assignment** per object and attribute; a **retired values register** so values are never re-issued; probing of the target directory; and **Collision Remediation**, which revises JIM's own assignment when a target rejects a value as in use, unless the value is anchored by another target, in which case an administrator is given a decision with three exits. Everything surfaces through the existing causality model.

The design was settled in the PRD's nineteen design decisions and is not re-opened here. This document decides *how*, and records the findings of the 2026-09-19 adversarial review with where each one is addressed.

## Business Value

- Demo credibility and real-deployment fit: HR feeds stop carrying IT-owned attributes (Scenario 1 becomes the canonical example).
- Numeric, prefixed and random identifiers (employee numbers, badge numbers, correlation ids) come from the same form as account names.
- Opinionated security posture: values are never re-issued by default, and sequence counters never move backwards, so a retired identifier can never be associated with a new person by accident.
- Self-healing collisions with a full audit trail, and an explicit decision where healing would rename a live account.
- Reuse: the service is caller-agnostic and keyed on object and attribute, so internally managed Metaverse Objects and workflow-driven generation become further callers later.

## Technical Architecture

### Current state

- **Mapping model.** `SyncRuleMapping` (`src/JIM.Models/Logic/SyncRuleMapping.cs`) carries `Sources` (one attribute or one expression per `SyncRuleMappingSource`), `Priority`, `NullIsValue`, `InitialExportOnly`, `Enabled`, `InboundValueProcessing`, `CaseNormalisation`. `GetSourceType()` derives `AttributeMapping` / `ExpressionMapping` from the sources. The authoring dialog, DTOs and cmdlets are all built on that shape.
- **Inbound flow is synchronous and in-memory.** `SyncEngine.FlowInboundAttributes` → `ProcessMapping` → `ProcessExpressionMapping` (`src/JIM.Application/Servers/SyncEngine.AttributeFlow.cs`) evaluate each mapping against the Connected System Object only (`ExpressionContext(metaverseAttributes: null, ...)`), stage results in `mvo.PendingAttributeValueAdditions` / `PendingAttributeValueRemovals`, and resolve contention through `AttributePriorityContext`, `ApplyNoValueOutcome` and `TakeOverProvenance` (#91, #1292). Persistence happens at the page flush in `SyncTaskProcessorBase`, which also captures each object's changed attributes for export evaluation (`_pendingExportEvaluations`). Nothing in this path performs I/O.
- **Export flow.** `SyncEngine.ExportStaging.CreateAttributeValueChanges` evaluates export mappings against the Metaverse Object (`ExpressionContext(mvAttributeDictionary, null)`) and produces Pending Export attribute value changes. Export staging is driven by the changed-attribute set (`SyncEngine.ExportEvaluation.HasRelevantChangedAttributes`).
- **Export execution.** `ExportExecutionServer.ExecuteUsingCallsWithBatchingAsync` → `ExportBatchAsync` → `ProcessBatchSuccessAsync`. A failed `ConnectedSystemExportResult` carries an optional `ConnectedSystemExportErrorType`; `MarkExportFailed` applies retry backoff and, at `MaxRetries`, `PendingExportStatus.Failed`. `RetryFailedExportsAsync` resets *every* Failed export of a system to Pending. Successful non-delete exports are applied optimistically to the connector space (`ApplyOptimisticExportUpdatesAsync`, #1079), so a Connected System Object holding a value means the target accepted it. The parallel path reloads each batch in its own context; the files path (`ExecuteUsingFilesWithBatchingAsync`) has its own result loop. `SyncExportTaskProcessor.cs:455` maps export error types to `ActivityRunProfileExecutionItemErrorType`.
- **Classification precedents.** `LdapConnectorExport.IsPlaceholderConstraintViolation` maps LDAP result codes to `PlaceholderMemberConstraintViolation`; every other non-success LDAP path is a generic `ResultCode != Success` check, and `EntryAlreadyExists` is handled nowhere. `ScimExportErrorClassifier.Classify(statusCode, scimType)` maps 412 to `ConcurrencyConflict` and `invalidValue` to `MissingDependency`; a 409 `uniqueness` error is not classified today.
- **Connector capabilities are mirrored by reflection.** `ConnectorCapabilityMirror` copies every matching-named boolean from `IConnectorCapabilities` to `ConnectorDefinition`; `SeedingServer.ApplyConnectorDeclarations` reconciles on startup; `JIM.Web` reads them through `ConnectorDefinitionDto`.
- **Value lookups are case-sensitive.** `IX_MetaverseObjectAttributeValues_AttributeId_StringValue` and its connector-space sibling are plain btrees on `StringValue`; `GetConnectedSystemObjectsByAttributeValuesAsync` lowercases in SQL (which defeats the index) and hydrates whole objects. The Metaverse side has only the single-value `GetMetaverseObjectByTypeAndAttributeAsync`. Every new repository member also needs its `JIM.InMemoryData` implementation and a `ReadOnlySyncRepositoryGuard` entry.
- **Causality.** `CausalEdgeType` (0–3) and `CausalReasonCode` (0–8) in `CausalEdgeEnums.cs`, `ActivityRunProfileExecutionItemSyncOutcomeType` (ends at `DeprovisionQueued`), `ActivityRunProfileExecutionItemErrorType` (ends at `ClassMembershipRequirementsNotMet`), all append-only with ordinal tests in `test/JIM.Models.Tests/Activities/`. The web renders through `OutcomeDisplayMap`, `CausalityModelBuilder`, `CausalityCauseWording` and `CausalitySummaryBuilder`; the worker records cross-item edges through `ExportCausalEdgeBuilder`, which today only records a cause that already exists.
- **Per-object review flag (#892).** `SyncTaskProcessorBase.ProcessScopeReviewPendingMetaverseObjectsAsync` drains a per-Metaverse-Object flag in both full and delta synchronisation, gives each object an execution item and re-evaluates its exports for every target. This is the existing Metaverse-side pickup mechanism.
- **Delta synchronisation** pages by `GetConnectedSystemObjectsModifiedSinceAsync(systemId, LastSyncCompletedAt, ...)` with a pre-computed count; the watermark is per Connected System.
- **Parallel runs.** `Worker.cs` dispatches every `WorkerTaskExecutionMode.Parallel` task together; schedules create parallel groups, so two synchronisations of different systems can run at once.
- **Per-run state seam.** `Worker.cs` constructs a fresh `SyncEngine` per run; `SyncTaskProcessorBase` builds `_attributePriorityContext` once per run and threads it into every flow call. Sync Preview goes through `SyncRuleAttributeFlowPreviewAdapter` and `SyncRuleAttributeFlowProposalMaterialiser`.
- **Uniqueness primitive and parked-state precedents.** `ExampleDataValueTrackerStore` (lock-free, per execution); `ConnectedSystemServer.ReleaseParkedInitialPasswordsIfDeliveryChangedAsync` → `InitialPasswordDeliveryServer.ReleaseParkedForSyncRuleAsync`.
- **Surfaces.** Mapping REST endpoints in `SynchronisationController` (`CreateSyncRuleMapping`, `UpdateSyncRuleMapping` as PATCH through `SyncRuleMappingSettingsUpdate`), DTOs in `SyncRuleMappingDtos.cs`; `New-JIMSyncRuleMapping` has four parameter sets, `Set-JIMSyncRuleMapping` takes `[bool]` settings; the Add/Edit dialog is inline in `SyncRuleAttributeFlowTab.razor`; attention indicators (`InitialPasswordAttentionIndicator`, the `Attention` column on `SyncRuleList.razor`, the trio on `ConnectedSystemList.razor`); `OperationsPasswordsTab.razor` with `PasswordSynchronisationController`'s queue endpoints is the closest analogue to a filtered list with per-row and bulk actions; `MvoDetailsTable.razor` renders attribute rows on `View.razor`.
- **Integration harness.** Scenario scripts under `test/integration/scenarios/`, highest is 22 (Scenario 22, OpenLDAP password policy, landed after this plan was written). `Generate-TestCSV.ps1` emits `first.last<Index>` for every user's `samAccountName` with no switches to omit columns; `Invoke-Scenario1` asserts on that exact value in six places and keys CSV edits on the `samAccountName` column. The OpenLDAP image has no `unique` overlay; there is no under-privileged bind precedent.

### Proposed solution

```
Authoring (portal / REST / PowerShell)
  SyncRuleMapping + SyncRuleMappingGeneration (token kind and settings, exclusions, never reuse, Collision Remediation)
  Sources[0].Expression = base value (existing expression engine and editor)
  GeneratedValueSequence per target attribute (forward-only counter)
                     │
Synchronisation run (import mode)          Export staging (export mode)
  engine records a GenerationRequest         engine records a GenerationRequest
  when the generated mapping is the          for the Pending Export change
  winning contributor and no assignment
  exists (adopt-existing and sticky short-circuits first)
                     │                                   │
                     └──────────── UniqueValueGenerationServer ────────────┘
                        candidates (base + token) → gates: reservation, retired register,
                        Metaverse, connector space, probe (release 3)
                        → GeneratedValueAssignment (Proposed) → value applied
                     │
Export run (release 4)
  target rejects → connector classifies UniqueValueAlreadyInUse and attributes it
     Collision Remediation on and unanchored → next candidate, assignment Remediated,
        value revised in the Metaverse (import) or on the object (export),
        object flagged for review (#892); next synchronisation re-stages the
        whole export with dependent values recomputed; next export carries it
     anchored, unknown, or exhausted → NeedsDecision, export Parked
     off or unattributable → ordinary export error with advice
                     │
Causality: outcomes GeneratedValueAssigned / GeneratedValueRemediated / GeneratedValueRetired,
  error GeneratedValueCollisionUnresolved, edge ExportRejectionCausedGeneratedValueRevision
  written by the re-staging synchronisation; stat counters
```

### Key decisions

1. **A generated mapping is an expression mapping plus a settings row.** The base value stays in `Sources[0].Expression`, so the expression editor, `ExpressionInputResolver`, Missing Input Behaviour (#1361) and the expression DTO fields are reused. `SyncRuleMappingGeneration` (one-to-one, cascade delete) is the discriminator; `GetSourceType()` returns `GeneratedMapping`. The base expression is optional when the token is a sequence or random token.

2. **Value = base + uniqueness token.** `TokenKind { OnlyIfTaken, Sequence, Random }`. Only-if-taken keeps the collision suffix (number or letter, start, separator). Sequence draws from the attribute's counter with increment, optional fixed width and an explicit overflow behaviour (stop, or allow longer). Random is a GUID, or lower-case hex or digits of a configured length from `RandomNumberGenerator`. Token placement is JIM's rule: before the first `@`, otherwise appended. This reverses the PRD's earlier "no sequence" Non-Goal (PRD design decision 12).

3. **Counters are attribute-scoped and forward-only.** `GeneratedValueSequence` is keyed on the target attribute (Metaverse or Connected System attribute), never deleted automatically, seeded from the highest existing numeric value on first use, reserved in blocks per page with an atomic `UPDATE ... RETURNING`, and moved only upward, and only by raising a flow's start value (the next number is the higher of the counter and any flow's start; the save confirms the skip and change capture audits it). No separate "set the counter" action exists. Gaps from unused block tails are documented as normal.

4. **Never reuse a value, on by default.** `RetiredGeneratedValue` (attribute, normalised value, retired at, from which object, reason) is written whenever an assignment is deleted and the flow's `NeverReuse` is on; a fifth gate treats retired values as taken. Sequence tokens are always never-reuse. The register is never pruned (PRD decisions 14, 16).

5. **Adopt before generate, in both modes.** Before generating, the service checks whether the object already holds a value for the attribute that a participating target has accepted (import mode: any joined non-excluded Connected System Object holds it for the attribute an export mapping flows it to; export mode: the Connected System Object holds it). If so, the assignment is created Committed with that value and nothing is generated. This closes the brownfield-join and withdrawn-source renames the review found (PRD decision 15).

6. **The engine stays synchronous; generation resolves in the worker, inline per object, with run-scoped caches.** The engine records a `PendingGeneratedValue` on the Metaverse Object (object, mapping, evaluated base value, or `BaseUnavailable` when a missing input means wait) where a generated mapping is the winning contributor, and incumbent detection (`FindEffectiveIncumbentSyncRuleId`) treats a pending generation as owning the attribute. The worker resolves the object's pending generations immediately after its inbound Attribute Flow, before the changed attributes are captured, so change tracking, outcomes, export evaluation and drift detection see the value exactly as they see any other contribution. *Revised during Phase 2 (2026-09-23):* the original design deferred resolution to one pass per page, but the worker captures changes, builds outcomes and queues export evaluation per object, so a page-end pass would have meant restructuring that whole tail. Cost is kept down instead by run-scoped state on `UniqueValueResolveOptions`: assignments are prefetched once per page, sequence numbers are reserved in blocks of 100 that outlive a call, and gate queries run only for an object that does not yet have a value (a one-off cost per object). Assignments are persisted after the page's Metaverse Objects, once their ids exist. Sync Preview resolves in dry-run mode (local gates only, nothing persisted).

7. **Dependent values are ordinary derived Attribute Flows** (Metaverse-Derived Attribute Flows PRD). A generated mapping's base expression may read `mv["..."]` once that PRD lands; requests are then resolved level by level, interleaved with the derived pass. Nothing derived is generated or sticky (PRD decision 17; review finding 3).

8. **Remediation revises and flags; it never retries inside the export run.** On an attributable, unanchored rejection the export server asks the service for the next candidate, writes the revised value through a `MetaverseServer` method that records the attribute change, provenance and an optimistic concurrency check atomically (import mode) or updates the Connected System Object (export mode), records the remediating execution item id on the assignment, and sets the object's #892 review flag. The next synchronisation of any joined system re-stages the export with every dependent value recomputed and writes the causal edge from the recorded item to the new queueing item. A retried export inside the run would carry stale dependent values and fail again (review findings 1, 2, 7, 14, 15; PRD decision 16).

9. **Attribution never guesses.** The connector names the colliding attribute where the server does: Active Directory `0x524` (account name), `0x21C8` with `Att 90290` (UPN), `0x2071` (DN); Samba names the attribute in its message. Otherwise the export carries exactly one generated value; otherwise the probe attributes it (release 3); otherwise an ordinary export error with advice. Classification is a table of (server family, result code, message pattern) with fixtures for real Active Directory strings, because Samba's codes differ (review findings 1, 11).

10. **Anchoring is derived, case-insensitively.** A value is anchored when another non-excluded joined Connected System Object holds it in its connector space for the attribute an export mapping flows it to. A participating system with no completed full import since its last connector space clear answers "cannot tell", which resolves to NeedsDecision. Export-mode assignments are never anchored.

11. **"Allow the rename" records an authorisation; the worker performs the rename** on the next rejection, since `JIM.Web` cannot probe. The confirmation names every system that will change and states that the new value is decided at the next export.

12. **NeedsDecision parks the export under its own status.** `PendingExportStatus.Parked` (appended), so the Connected System's bulk "Retry failed exports" ignores it and a remediation does not consume `ErrorCount` (review finding 13).

13. **Uniqueness is case-insensitive and enforced across runs.** Expression indexes on `LOWER("StringValue")` for both value tables; id-only raw-SQL gates; a unique index on `(attribute, LOWER(value))` across live assignments with the losing run drawing the next candidate; a process-wide reservation set in the worker for parallel schedule steps (review findings 4, 5, 6, 10).

14. **The probe proves visibility, not absence.** A control value JIM already holds rides in every batch; a batch without its control is `CouldNotDetermine`. This detects a blind bind, not a per-container denial, so NotFound is advisory and remediation stays the arbiter. UPN probes use the global catalogue where the connector has one (review finding 12).

15. **Attempt limit scope.** Per object per run for generation; per assignment lifetime for remediation (`RemediationCount`), exceeding which enters NeedsDecision.

16. **Probe batching.** LDAP up to 50 candidates per OR filter from a lazy window of 10; SCIM classification then `or` filters of 20; SQL `IN`.

17. **The assignment is state, not history.** Deleted with its object or its generation row by cascade, and at page flush when another contributor wins or the value is recalled; retirement (decision 4) is what keeps the value's memory. Nothing expires. See Assignment lifecycle.

18. **Configuration surfaces come before the connector work.** Integration scenarios configure JIM through PowerShell and REST, and Scenario 1's conversion needs only the local gates.

19. **Delivery in four releases** (PRD decision 19), each independently shippable and each closing the review findings that belong to it.

### Data model

**`SyncRuleMappingGeneration`** (one-to-one with `SyncRuleMapping`, cascade delete):

| Field | Type | Notes |
|---|---|---|
| `SyncRuleMappingId` | int, unique FK | Discriminator: presence means generated mapping |
| `TokenKind` | enum `GeneratedValueTokenKind { OnlyIfTaken = 0, Sequence = 1, Random = 2 }` | |
| `SuffixStyle`, `SuffixStart` | enum `{ Number, Letter }`, int (default 1) | Only-if-taken |
| `SequenceStart`, `SequenceIncrement`, `FixedWidth`, `OnWidthExceeded` | long, int (1), int? (null = none), enum `{ StopAndReport = 0, AllowLonger = 1 }` | Sequence |
| `RandomFormat`, `RandomLength` | enum `{ Guid, Hex, Digits }`, int? | Random |
| `Separator` | string?, default null | Between base and token |
| `AttemptLimit` | int, default 1000 | Design decision 4 |
| `NeverReuse` | bool, default true | Forced true for Sequence |
| `CollisionRemediation` | bool, default true | Release 4; honoured only where a participating connector classifies |
| `Exclusions` | collection of `SyncRuleMappingGenerationExclusion (GenerationId, ConnectedSystemId)` | Per-system exclusion from availability checks |

**`GeneratedValueSequence`** (per target attribute):

| Field | Type | Notes |
|---|---|---|
| `MetaverseAttributeId` / `ConnectedSystemObjectTypeAttributeId` | int? / int? | Exactly one; unique |
| `NextValue` | long | The floor; only ever increases |
| `AssignedCount` | long | Display |
| `LastMovedAt`, `LastMovedBySyncRuleMappingId` | DateTime?, int? | Which flow's start value last moved it |

Not deleted when the mapping or its rule is deleted; deleted only with the attribute.

**`RetiredGeneratedValue`** (release 2):

| Field | Type | Notes |
|---|---|---|
| `MetaverseAttributeId` / `ConnectedSystemObjectTypeAttributeId` | int? / int? | Exactly one |
| `NormalisedValue` | string | Lower-cased; unique with the attribute |
| `RetiredAt`, `Reason` | DateTime, enum `{ ObjectDeleted, Regenerated, Superseded, Recalled }` | |
| `FromObjectDisplayName`, `FromObjectId` | string?, Guid? | For the history event; the object may no longer exist |

**`GeneratedValueAssignment`**:

| Field | Type | Notes |
|---|---|---|
| `Id` | Guid | |
| `MetaverseObjectId` / `MetaverseAttributeId` | Guid? / int? | Import mode; filtered unique index on the pair |
| `ConnectedSystemObjectId` / `ConnectedSystemObjectTypeAttributeId` | Guid? / int? | Export mode; filtered unique index on the pair; exactly one mode populated (check constraint) |
| `Value`, `NormalisedValue` | string, string | Unique index on (attribute, `NormalisedValue`) across live assignments (decision 13) |
| `PreviousValue` | string? | Value before the last remediation |
| `State` | enum `GeneratedValueAssignmentState { Proposed = 0, Committed = 1, Remediated = 2, NeedsDecision = 3 }` | |
| `SyncRuleMappingGenerationId` | int, FK cascade | The generation row that produced it |
| `Adopted` | bool | True when the value was adopted rather than generated (decision 5) |
| `RemediationCount` | int | Decision 15 |
| `RenameAuthorised`, `RenameAuthorisedAt`, `RenameAuthorisedByName` | bool, DateTime?, string? | Decision 11; cleared on the next successful export |
| `RejectedByConnectedSystemId`, `AnchoredByConnectedSystemId` | int?, int? | For the NeedsDecision error and list |
| `RemediatedByActivityRunProfileExecutionItemId` | Guid? | The cause the re-staging synchronisation writes the edge from; cleared once written |
| `NeedsDecisionEnteredAt`, `NeedsDecisionActivityRunProfileExecutionItemId` | DateTime?, Guid? | |
| `Created`, `LastUpdated`, `CommittedAt` | DateTime, DateTime, DateTime? | |

Cascade delete from the owning object and the generation row. Index on `State`.

**Appended enum members** (all in release 1's first phase so the ordinal tests change once):

| Enum | Member |
|---|---|
| `SyncRuleMappingSourcesType` | `GeneratedMapping = 4` |
| `PendingExportStatus` | `Parked` |
| `ConnectedSystemExportErrorType` | `UniqueValueAlreadyInUse` |
| `ActivityRunProfileExecutionItemErrorType` | `GeneratedValueExhausted`, `GeneratedValueWidthExceeded`, `GeneratedValueCollisionUnresolved` |
| `ActivityRunProfileExecutionItemSyncOutcomeType` | `GeneratedValueAssigned`, `GeneratedValueAdopted`, `GeneratedValueRetired`, `GeneratedValueRemediated` |
| `CausalEdgeType` | `ExportRejectionCausedGeneratedValueRevision = 4` |
| `CausalReasonCode` | `GeneratedValueAlreadyInUse = 9`, `GeneratedValueAnchoredElsewhere = 10`, `GeneratedValueRenameAuthorised = 11` |

### Assignment lifecycle

An assignment is **state, not history**: it exists exactly while a generated mapping is responsible for that object's attribute. History lives in Activities and causality; the value's memory lives in the retired values register. The assignment table is bounded at one row per (object, generated attribute).

| Trigger | Mechanism |
|---|---|
| Metaverse Object or Connected System Object deleted, including a connector space clear | FK cascade from the object; retirement written first when `NeverReuse` is on |
| Generated mapping removed (recall or keep, #1537), its Synchronisation Rule deleted, or its source type changed | FK cascade from `SyncRuleMappingGeneration`; a kept value becomes an ordinary orphan value; retirement written when `NeverReuse` is on |
| Another contributor wins the attribute, or the value is recalled or cleared | Page-flush reconciliation deletes any assignment whose value or provenance no longer matches, drops a Parked export with it, and retires the value |
| Mapping disabled; object awaiting deletion under a grace period | Retained |

Rules that keep deletion safe: an object's own current value is always free for it (every gate excludes the requesting object); adopt before generate (decision 5); missing knowledge never permits a rename (decision 10). The counter is unaffected by any of this (decision 3).

### The service

`UniqueValueGenerationServer` in `src/JIM.Application/Servers/`, exposed on `JimApplication` as `UniqueValues`:

- `UniqueValueCandidates` (static, pure): `Sequence(baseValue, settings, nextSequenceNumber?, random)` yields candidates per token kind; owns placement, padding, the letter strategy and the width check.
- `UniqueValueReservationSet` (process-wide in the worker, thread-safe; generalised from `ExampleDataValueTrackerStore`): keyed on (scope, attributeId, normalised value).
- `SequenceAllocator`: reserves a block per page with `UPDATE ... SET NextValue = NextValue + @block RETURNING`, seeds on first use.
- Gates, in order, batched per page and case-insensitive: `ReservationGate`, `RetiredGate` (release 2), `MetaverseGate` (new raw-SQL id-only lookup over the `LOWER` index), `ConnectorSpaceGate` (new raw-SQL id-only lookup; existing method is unsuitable, see Current state), `ProbeGate` (release 3, worker-only).
- `ResolveAsync(requests, options)`: adopt-existing check, sticky short-circuit, then candidates through the gates; exhaustion and width failures named per object; dry-run for previews.
- `TryRemediateAsync`, `IsAnchoredAsync`, `CommitAsync`, `EnterNeedsDecisionAsync`, `AuthoriseRenameAsync`, `RetryAsync`, `ReleaseNeedsDecisionForMappingAsync`, `ListNeedsDecisionAsync` (release 4); `GetSequenceStateAsync`, `GetAssignmentsForObjectAsync`, `StartAgainAsync` (purge the attribute's retired values and reset the counter to the flow's start value; existing values and assignments untouched, no recall, because #1537's recall stages removal exports that would strip the connector-space values FR 30 adopts; one Activity) (release 1). No per-Metaverse Object regenerate action: deferred to #614 with Set Value.

### Connector capabilities (release 3 and 4)

- `IConnectorUniquenessProbe`: `ProbeAsync(attribute, candidates, controlValue, settings, logger, ct)` → `UniquenessProbeOutcome { Found, NotFound, CouldNotDetermine }` per candidate. LDAP searches from the partition root (global catalogue for forest-wide attributes) with a batched OR filter and the control value.
- `IConnectorCapabilities` gains `SupportsUniquenessProbe` and `SupportsUniquenessRejectionClassification`, mirrored on `ConnectorDefinition` by `ConnectorCapabilityMirror`.
- Classification per decision 9: LDAP (`EntryAlreadyExists` and `ConstraintViolation` with message-code attribution), SCIM (409 `uniqueness`), SQL (PostgreSQL `23505`, SQL Server `2627`/`2601`).

## Implementation Phases

Each phase is a PR off `main`, TDD throughout, `dotnet build JIM.sln` and `dotnet test JIM.sln` clean before each. Surface parity is delivered per capability: a phase that adds an administrator-facing capability ships portal, REST and PowerShell together. The PRD moves to `engineering/prd/doing/` with Phase 1.

### Release 1: generation

#### Phase 1: Model, persistence and vocabulary

1. `SyncRuleMappingGeneration`, `SyncRuleMappingGenerationExclusion`, `GeneratedValueSequence`, `GeneratedValueAssignment` and their enums; `SyncRuleMapping.Generation` navigation; `GetSourceType()` returns `GeneratedMapping`; validation: exactly one expression source (optional for sequence and random), single-valued Text target (Number allowed for sequence and digit tokens without fixed width).
2. Every appended enum member from the table above, with the ordinal tests extended; `RetiredGeneratedValue` is release 2 but its outcome member is appended now.
3. Migrations: the three tables; `LOWER("StringValue")` expression indexes on both value tables; the assignment's filtered unique indexes and the cross-assignment `(attribute, NormalisedValue)` unique index; `Parked` status. Named in the style of `AddInboundValueProcessingToSyncRuleMapping`, appended after `20260905125649_AddPasswordChangeOrigin`.
4. Repository: id-only raw-SQL lookups by attribute and normalised values for Metaverse and connector space; assignment CRUD, by-object and by-attribute lookups; sequence block allocation and seeding query (highest numeric value for an attribute); each member implemented in `JIM.PostgresData`, `JIM.InMemoryData` and registered with `ReadOnlySyncRepositoryGuard`.
5. Configuration change capture: `ConfigurationSnapshotService` / `ConfigurationDiffService` include the generation settings; `ConfigurationChangeClassifier` classifies a base-expression or token change as affecting new objects only.
6. Lifecycle by cascade (assignment from object and generation row; counter only from the attribute); database-tier tests for each cascade.
7. Tests: model validation, ordinal tests, persistence and lookup tests in `test/JIM.Worker.Tests/`.

#### Phase 2: Generation engine

1. `UniqueValueCandidates` for all three tokens (placement, padding, width check, letters, cryptographic random); `UniqueValueReservationSet`; `SequenceAllocator`; the three local gates; `ResolveAsync` with adopt-existing, sticky short-circuit, exhaustion, width failure and dry-run.
2. `SyncEngine.AttributeFlow`: a generated mapping is evaluated like an expression mapping up to producing a value; when it is the winning contributor (priority, `NullIsValue`, Missing Input Behaviour resolved first) the engine records a `GenerationRequest` and a placeholder pending addition. `SyncEngine.ExportStaging`: the same for export mappings, attached to the Pending Export change.
3. Worker: `SyncTaskProcessorBase` resolves each page's requests before persistence, applies values with provenance, appends to changed attributes, creates assignments, records `GeneratedValueAssigned` / `GeneratedValueAdopted` outcomes and the `GeneratedValueExhausted` / `GeneratedValueWidthExceeded` errors; page-flush lifecycle reconciliation; the process-wide reservation set lives on the worker and is handed to each run. `Worker.cs` wiring. Export staging through `ExportEvaluationServer`.
4. Review-flag integration: *moved to release 4 (2026-09-23).* In release 1 nothing sets the #892 flag for a generated attribute ("Generate a new value" is deferred to #614), so there is nothing for the drain to resolve; Collision Remediation is the first writer, and Phase 8 wires the drain with it.
5. Sync Preview: dry-run resolution in both directions shows the candidate (or the adopted value), states when generation would fail, and carries a note that the run checks again. *Revised 2026-09-23:* teaching `SyncRuleAttributeFlowProposalMaterialiser` the source type moves to Phase 3, because a proposal can only carry generation settings once the dialog and DTOs that author them exist.
6. Stat counters on `ActivityRunProfileExecutionStats` (`TotalGeneratedValues`, `TotalGeneratedValuesAdopted`, `TotalGeneratedValueFailures`) with their raw-SQL aggregation in `ActivitiesRepository` and `Worker.CalculateActivitySummaryStats`, and the `CausalitySummaryBuilder` clause.
7. Tests (red first): candidate sequencing per token; placement; padding and overflow; letter strategy; random formats; reservation set under contention; block allocation and seeding; gate ordering and short-circuit; case-insensitive hits; own value free; adopt-existing for brownfield join, clear and re-import, withdrawn source; stickiness across re-runs; priority supersession deleting the assignment; recall deleting the assignment; intra-page collisions; exhaustion; export mode keyed on the Connected System Object; cross-run uniqueness index conflict path. Runtime check on the light stack: CSV import generating Account Name and Employee Number.
8. File the example-data migration follow-up issue: [#1789](https://github.com/TetronIO/JIM/issues/1789).
9. **Delivered notes (2026-09-23).** Export mode keys the assignment on the Connected System Object and never writes the Metaverse Object; its value is resolved when export evaluation stages the Pending Export and committed after the provisioning object is persisted. Every caller of contributor re-election (the worker's import, out-of-scope and obsoletion paths, Synchronisation Rule deletion recall, Sync Preview) passes a resolver; the server-side recall and deprovisioning paths pass none, so a re-elected generated mapping there waits for the generating system's next synchronisation, and a generated export change they stage is stripped rather than persisted blank. **Known gap:** drift detection skips generated export mappings (its expected value would be the base expression, not the assigned value), so a generated value changed in the target outside JIM is reasserted only when export evaluation next runs for that object; comparing against the assignment belongs with release 4's anchoring work.

#### Phase 3: Configuration and Metaverse Object surfaces (portal, REST, PowerShell)

1. Portal (`SyncRuleAttributeFlowTab.razor`, inline dialog): a third "Source Type" item, "JIM generates it", with the form in the Generated Value Options mockups: target, base expression (existing editor and `ExpressionTester`), uniqueness token radio group with the sub-controls per kind (style, start, separator; start, increment, a "Pad to a fixed width" switch revealing width with room remaining and the overflow behaviour; random format and length with value-space hint), "Never reuse a value" switch (on), a read-only "When JIM generates this value" statement with the inputs read from the expression via `ExpressionInputResolver`, the Missing Input Behaviour choice defaulting to "wait" for generated mappings, attempt limit (advanced), live preview with the count of existing objects that would receive a value, read-only sequence state panel, save-time confirmation when a raised start value skips numbers; `Generated` row chip in both layouts with a `GetMappingTypeChipColour` case; the row menu gains "View retired values" (release 2) and "Start again…" with the typed confirmation as mocked (screen 02b). Metaverse Object page (`MvoDetailsTable.razor` on `View.razor`): `Generated by JIM` chip and "View history" (the attribute-scoped Timeline), delivered through or consistent with #399's provenance display; no "Generate a new value" (deferred to #614).
2. REST: `SyncRuleMappingDto`, `CreateSyncRuleMappingRequest`, `UpdateSyncRuleMappingRequest` gain a `generation` object; `SourceType` renders `GeneratedMapping`; direction gating follows the `InitialExportOnly` block; `SyncRuleMappingSettingsUpdate` carries the settings through PATCH. New: `GET metaverse-objects/{id}/generated-values`, `GET sync-rules/{id}/mappings/{mappingId}/sequence` (read-only state), `POST sync-rules/{id}/mappings/{mappingId}/generation/restart` (Start again; returns the counts it forgot and reset). OpenAPI updated; API tests beside `SyncRuleMappingUpdateApiTests.cs`.
3. PowerShell: `New-JIMSyncRuleMapping` gains `ImportGenerated` and `ExportGenerated` parameter sets (`-Generate`, `-TokenKind`, `-SuffixStyle`, `-SuffixStart`, `-SequenceStart`, `-SequenceIncrement`, `-FixedWidth`, `-OnWidthExceeded`, `-RandomFormat`, `-RandomLength`, `-Separator`, `-AttemptLimit`, `-NeverReuse`, `-ExcludeConnectedSystemId`); `Set-JIMSyncRuleMapping` the same as nullable settings; `Get-JIMGeneratedValue` (per Metaverse Object), `Get-JIMGeneratedValueSequence`, `Restart-JIMGeneratedValues` (Start again, `-Confirm` by default); Pester in `src/JIM.PowerShell/Tests/`.
4. `ConnectedSystemServer` mapping create and settings-update validation: target type versus token (Number targets accept only sequence or random digits, no base expression, no padding; the reason is returned to every surface); exclusions must be participating systems; `NeverReuse` forced on for sequence; a start value raised above the counter moves it and is reported in the save response so the portal can confirm; a lower one is accepted and has no effect.
5. Docs: `docs/powershell/synchronisation-rules.md`, the API reference, and the concept section in `docs/configuration/synchronisation-rules.md` beside "Initial Export Only (outbound)": generated values, tokens, counters, never-reuse (recommended on; the rehire exception), what a target-side collision looks like in this release, and that changing a generated value is deferred to #614.

#### Phase 4: Integration and release

1. `Invoke-Scenario23-UniqueValueGeneration.ps1` (setup, run, teardown through `Run-IntegrationTests.ps1`) covering the PRD's Modes, Strategy, Tokens, Never reuse (sequence only in this release), Adopt before generate, Start again, Failure, Stability and Surface parity rows, plus the local-gate rows of Gates; the mixed-capability case uses the SQL connector with a unique constraint, because a CSV target cannot reject a duplicate.
2. `Generate-TestCSV.ps1` gains `-OmitItOwnedAttributes` touching its three emission sites, folded into the cache key in `Get-OrGenerate-TestCSV.ps1` / `Test-CsvCache.ps1`. `Setup-Scenario1.ps1` replaces the `samAccountName → Account Name` import mapping with a generated one; `Invoke-Scenario1` is rewritten to derive expected account names from `Lower(first).Lower(last)` with JIM's suffix rule and to key CSV edits on `employeeId` (six assertion sites and the CSV-edit sites listed in Current state). Email and UPN stay as they are until release 2.
3. `CHANGELOG.md` `✨` entries for Unique Value Generation, sequence and random tokens, and "never reuse"; engineering developer-guide section on the service and the deferred-request seam; the causality reference (none exists today) listing every outcome, error, edge and reason member.

### Release 2: derived values and the retired register

#### Phase 5: Metaverse-Derived Attribute Flows

Delivered under its own PRD and issue (see header). Once landed: generated base expressions may read `mv["..."]`; `ResolveAsync` runs per level, interleaved with the derived pass; the #892 review processing runs the derived pass after generation.

#### Phase 6: Retired values register and Scenario 1 completion

1. `RetiredGeneratedValue` table and migration (`LOWER` unique index), `RetiredGate`, retirement on every assignment deletion path (cascade paths write the retirement in the same transaction; the flush reconciliation writes it directly), `GeneratedValueRetired` history event on the Metaverse Object's timeline, "Never reuse a value" honoured (release 1 persists the switch and forces it for sequences; this phase gives it effect for the other tokens).
2. Scenario 1 completed: Email and UPN derived from Account Name; `Invoke-Scenario1` expectations for email and UPN derived the same way. Scenario 22 gains the Never reuse rows for name-based and random tokens and the derived-values rows.
3. Docs: never-reuse semantics and the register; the audit trail of retirements.

### Release 3: probing

#### Phase 7: Connector capabilities

1. `IConnectorUniquenessProbe`, `UniquenessProbeOutcome`, the two capability flags (matching names for `ConnectorCapabilityMirror`; migration in the style of `AddSupportsPasswordPolicyDiscoveryCapability`), declarations on every connector including the mocks, `ConnectorDefinitionDto` exposure, `ConnectorFactory` wiring.
2. LDAP: probe from the partition root (global catalogue for UPN) with batched OR filter and the control value; classification per decision 9 with fixtures for real Active Directory and Samba strings.
3. SCIM: classification (`scimType: uniqueness`); probe with `or` filters. SQL: classification and `IN` probe, deferrable to a follow-up if the phase runs long; say so in the PR if deferred.
4. `ProbeGate` in the worker's gate chain; the dialog's participating-system list with probe and classification columns (mock 02 of the PRD artefact), read from `ConnectorDefinition`, also exposed as a read on the mapping DTO and `Get-JIMSyncRuleMapping`.
5. Harness: an ACL-restricted bind account in the Samba image (`samba-tool dsacl`) and the OpenLDAP `unique` overlay in the OpenLDAP image, so the under-privileged bind and OpenLDAP mail uniqueness cases are producible. Scenario 22 gains the probe rows.
6. Tests: filter construction and batching, control-value handling, classification fixtures (unit, no directory); Samba runtime verification of a brownfield object outside the import scope and the restricted bind.

### Release 4: Collision Remediation

#### Phase 8: Remediation, anchoring and Needs Decision

1. `ExportExecutionServer.ProcessBatchSuccessAsync` failure branch (calls path; the files path records the classified error only): attribute the rejection (decision 9), look up the assignment, check the switch and the connector's classification capability, check anchoring or `RenameAuthorised` (decision 10), then `TryRemediateAsync` → revise through the atomic `MetaverseServer` write (import) or the object (export), record the remediating item id, set the review flag, and leave the export Pending for the re-staged change; no in-run retry, no `ErrorCount` increment. Parallel batches share the process-wide reservation set; the per-batch context re-persists through `batchRepo`.
2. Anchored, unknown or exhausted: `EnterNeedsDecisionAsync`; export to `Parked`; error `GeneratedValueCollisionUnresolved` naming the rejecting and anchoring systems with advice.
3. Switch off or unattributable: ordinary export error with advice; assignment unchanged.
4. Release on configuration change mirroring `ReleaseParkedInitialPasswordsIfDeliveryChangedAsync`; Parked exports return to Pending.
5. Causality: `SyncExportTaskProcessor` maps the new export error type; `GeneratedValueRemediated` outcome with the attribute row Set and previous value; the re-staging synchronisation (in `SyncTaskProcessorBase`, at queueing time) writes `ExportRejectionCausedGeneratedValueRevision` from the recorded item with the right reason code and clears the field; `TotalGeneratedValuesRemediated` and `TotalGeneratedValuesNeedingDecision` with their summary-band clause.
6. Tests: attribution table; remediation revise-and-flag; re-staging with recomputed dependants; anchored; unknown after clear; authorised rename; switch off; unattributable; `Parked` ignored by bulk retry; edge written at queueing; parallel batch safety. Runtime: Samba AD with a conflicting account created after the probe.

#### Phase 9: Needs Decision surfaces and transparency (portal, REST, PowerShell)

1. Portal: Needs Decision tab on Operations modelled on `OperationsPasswordsTab.razor` (filter by Connected System and Synchronisation Rule, summary tile, per-row and bulk "Try again"); "Authorise rename on next export" confirmation naming every system that will change and stating the new value is decided at the next export (mock 06, reworded); `GeneratedValueAttentionIndicator` following `InitialPasswordAttentionIndicator` on `SyncRuleList.razor`, `ConnectedSystemList.razor` and the Metaverse Object; `Corrected` chip and the attribute-scoped causality Timeline (mock 04); Activity Timeline events and exits (mock 05); `OutcomeDisplayMap`, `CausalityModelBuilder`, `CausalityCauseWording`, `CausalitySummaryBuilder`; bUnit tests.
2. REST: `GET generated-values/needs-decision` (paged) and `GET .../summary`, `POST generated-values/{id}/authorise-rename`, `POST generated-values/{id}/try-again`, and a bulk `POST generated-values/needs-decision/try-again`, answering `202 Accepted` with the #1121 `wait` shape; API tests.
3. PowerShell: `Get-JIMGeneratedValueDecision`, `Approve-JIMGeneratedValueRename`, `Reset-JIMGeneratedValueDecision`; Pester; docs.
4. Scenario 22 gains the Collision Remediation and Anchoring rows; connector pages and capability matrix; `CHANGELOG.md` `✨` entry for Collision Remediation; PRD and plan move to `done/` when the issue closes.

## Review findings and where they land

| # | Finding (2026-09-19 review) | Addressed by |
|---|---|---|
| 1 | In-run retry unsound; `EntryAlreadyExists` ambiguity | Decisions 8, 9; Phase 8 |
| 2 | Ordinary generation renames live accounts | Decision 5; Phase 2 |
| 3 | Derived values versus stickiness | Decision 7; derived-flows PRD; Phases 5, 6 |
| 4 | Case-insensitive uniqueness and unindexed lookups | Decision 13; Phase 1 |
| 5 | Cross-run race | Decision 13; Phases 1, 2 |
| 6 | Delta pickup should reuse #892 | Decision 8; Phases 2, 8 |
| 7 | Deferred request not wired into what follows | Decision 6; Phase 2 |
| 8 | Metaverse write without history or concurrency | Decision 8; Phase 8 |
| 9 | Needs Decision needs its own status | Decision 12; Phases 1, 8 |
| 10 | Causal edge written where the effect does not exist | Decision 8; Phase 8 |
| 11 | Scenario 1 conversion larger than stated | Phase 4 |
| 12 | Probe claims overstated | Decision 14; Phase 7 |
| 13 | Scenario 22 cases the harness cannot produce | Phases 4, 7 |
| Minor | Surface parity gaps; export-mode value in DN; stat aggregation sites; `int.MaxValue` row; exit naming; security tests | Phases 3, 9; docs note that export-mode values cannot feed the DN expression; Phase 2.6; risk table corrected; Phase 9 wording; expression-security tests run against the inbound context in Phase 5 |

## Success Criteria

The PRD's Acceptance Criteria, plus:

- No connector call path reachable from `JIM.Web`.
- 100k-object import with a generated text flow within 10% of the same import with a plain expression flow, measured with the local stack and the `LOWER` indexes in place.
- Every appended enum member has an ordinal test.
- Scenario 1 converted (Account Name in release 1; Email and UPN in release 2) and green; Scenario 22 green for each release's rows; the full suite green.

## Benefits

- One form covers account names, employee numbers, badge numbers and correlation ids.
- Never-reuse by default and forward-only counters give an auditable answer to "could this identifier ever have belonged to someone else".
- Self-healing collisions with a complete audit trail, and an explicit decision where healing would rename a live account.
- One generation service, one assignment model, one review-flag pickup and one dependency graph that internally managed Metaverse Objects, workflows and a future Set Value reuse.

## Dependencies

- All landed: #1087 / #1495, #1121 (Phases 1–3), #1079, #223, #843, #91, #1361, #892.
- Planned: Metaverse-Derived Attribute Flows PRD (release 2).
- No new NuGet packages.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Probe cost dominates large imports | Probes only for candidates that cleared the local gates, batched per page and system; lazy window; the NFR benchmark gates release 3. |
| Silent ACL filter or forest-wide attribute gives a false NotFound | NotFound is advisory (decision 14); remediation is the arbiter; global catalogue for UPN. |
| Remediation revises the wrong value on a multi-value export | Decision 9: no attribution, no revision. |
| Ping-pong across runs | Anchoring stops the second revision; `RemediationCount` ceiling; NeedsDecision is terminal. |
| Engine sync/async boundary leaks I/O into `SyncEngine` | The deferred-request pattern; a unit test asserts `ProcessMapping` never touches the service. |
| Two parallel runs generate the same value | Process-wide reservation set plus the cross-assignment unique index with a defined conflict path. |
| Counter reset by mapping recreation | Counter keyed on the attribute, never deleted with the flow. |
| Retired register grows without bound | By design; it grows with leavers only and is small; documented. |
| Sole-contributor generated mapping and the `int.MaxValue` priority sentinel | Sole contributors take the engine's fast path by design; `AutoAssignImportMappingPriorityAsync` densifies when other contributors exist. No dialog prompt needed. |
| Export-mode generated value cannot feed the DN expression | Documented: export mode targets non-RDN attributes; export expressions read the Metaverse only. |
| A release between phases freezes a migration | Migrations are append-only; each release's tables land in final shape within it. |
