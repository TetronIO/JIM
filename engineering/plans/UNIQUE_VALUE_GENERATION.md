# Unique Value Generation and Collision Remediation

- **Status:** Planned
- **Issue:** [#242](https://github.com/TetronIO/JIM/issues/242)
- **PRD:** [`../prd/PRD_UNIQUE_VALUE_GENERATION.md`](../prd/PRD_UNIQUE_VALUE_GENERATION.md)
- **Related:** [#223](https://github.com/TetronIO/JIM/issues/223) Initial Export Only (per-mapping flag precedent), [#1121](https://github.com/TetronIO/JIM/issues/1121) Initial Password Provisioning (parked state, release on configuration change, queue-and-follow), [#1087](https://github.com/TetronIO/JIM/issues/1087) / [#1495](https://github.com/TetronIO/JIM/issues/1495) causality views, [#1079](https://github.com/TetronIO/JIM/issues/1079) optimistic export apply, [#91](https://github.com/TetronIO/JIM/issues/91) attribute priority, [#1361](https://github.com/TetronIO/JIM/issues/1361) Missing Input Behaviour
- **UI mockups:** [Unique Value Generation: Design and Mockups](https://claude.ai/artifact/G9R6cK7WR7QwmPukctFpkb)
- **Plan explainer:** [Unique Value Generation Plan](https://claude.ai/artifact/WsfoqDR7PrwiQZLQBPCt9q) (what changes per layer, the data model, the three data flows, the assignment lifecycle and the phases)
- **Last Updated:** 2026-09-16

## Overview

JIM cannot generate the identifiers an IDAM team owns (account name, email, UPN); the demo data hides that by putting them in the HR feed. This plan delivers a "JIM generates it" source type on import and export Attribute Flows, a unique value service with four availability gates (in-run reservations, Metaverse, connector spaces, live target probe), a JIM-owned sticky **generated value assignment** per object and attribute, and **Collision Remediation**: when a target rejects a generated value as already in use, JIM revises its own assignment and exports again, unless the value is anchored by another target, in which case an administrator is given a decision with three exits. Everything surfaces through the existing causality model.

The design was settled in the PRD's eleven design decisions and is not re-opened here. This document decides *how*, resolves the PRD's four open questions, and records three things the PRD glossed over that change the shape of the work (see Key decisions).

## Business Value

- Demo credibility and real-deployment fit: HR feeds stop carrying IT-owned attributes (Scenario 1 becomes the canonical example).
- Self-healing: a collision in the target is handled by JIM in the same export run, with a full audit trail, rather than by an administrator reading an export error.
- Safety: JIM never renames a live account silently; the anchoring rule turns that case into an explicit decision.
- Reuse: the service is caller-agnostic and keyed on object and attribute, so internally managed identities and workflow-driven generation become further callers later.

## Technical Architecture

### Current state

- **Mapping model.** `SyncRuleMapping` (`src/JIM.Models/Logic/SyncRuleMapping.cs`) carries `Sources` (one attribute or one expression per `SyncRuleMappingSource`), `Priority`, `NullIsValue`, `InitialExportOnly`, `Enabled`, `InboundValueProcessing`, `CaseNormalisation`. `GetSourceType()` derives `AttributeMapping` / `ExpressionMapping` from the sources. The authoring dialog, DTOs and cmdlets are all built on that shape.
- **Inbound flow is synchronous and in-memory.** `SyncEngine.FlowInboundAttributes` → `ProcessMapping` → `ProcessExpressionMapping` (`src/JIM.Application/Servers/SyncEngine.AttributeFlow.cs`) evaluate each mapping against the Connected System Object only (`ExpressionContext(metaverseAttributes: null, ...)`), stage results in `mvo.PendingAttributeValueAdditions` / `PendingAttributeValueRemovals`, and resolve contention through `AttributePriorityContext`, `ApplyNoValueOutcome` and `TakeOverProvenance` (#91, #1292). Persistence happens at the page flush in `SyncTaskProcessorBase`. Nothing in this path performs I/O.
- **Export flow.** `SyncEngine.ExportStaging.CreateAttributeValueChanges` evaluates export mappings against the Metaverse Object (`ExpressionContext(mvAttributeDictionary, null)`) and produces Pending Export attribute value changes.
- **Export execution.** `ExportExecutionServer.ExecuteUsingCallsWithBatchingAsync` → `ExportBatchAsync` → `ProcessBatchSuccessAsync`. A failed `ConnectedSystemExportResult` carries an optional `ConnectedSystemExportErrorType`; `MarkExportFailed` applies retry backoff and, at `MaxRetries`, `PendingExportStatus.Failed` (manual intervention). Successful non-delete exports are applied optimistically to the connector space (`ApplyOptimisticExportUpdatesAsync`, #1079), so a Connected System Object holding a value means the target accepted it. `SyncExportTaskProcessor.cs:455` maps export error types to `ActivityRunProfileExecutionItemErrorType`.
- **Classification precedents.** `LdapConnectorExport.IsPlaceholderConstraintViolation` (`src/JIM.Connectors/LDAP/LdapConnectorExport.cs:1947`) maps LDAP result codes to `PlaceholderMemberConstraintViolation`; every other non-success LDAP path is a generic `ResultCode != Success` check, and `EntryAlreadyExists` is handled nowhere. `ScimExportErrorClassifier.Classify(statusCode, scimType)` maps 412 to `ConcurrencyConflict` and `invalidValue` to `MissingDependency`; a 409 `uniqueness` error is not classified at all today.
- **Connector capabilities are mirrored by reflection.** `ConnectorCapabilityMirror` (`src/JIM.Models/Staging/ConnectorCapabilityMirror.cs`) copies every matching-named boolean from `IConnectorCapabilities` to `ConnectorDefinition`, and `SeedingServer.ApplyConnectorDeclarations` reconciles them on startup. Adding a flag is a property on each side plus a migration; `JIM.Web` reads it through `ConnectorDefinitionDto` and `GetConnectedSystemCapabilities`.
- **Value lookups.** `IConnectedSystemRepository.GetConnectedSystemObjectsByAttributeValuesAsync(connectedSystemId, attributeId, values)` is batched; the Metaverse side has only the single-value `GetMetaverseObjectByTypeAndAttributeAsync`, no batched id-only variant. Every new repository member also needs its `JIM.InMemoryData` implementation and a `ReadOnlySyncRepositoryGuard` entry.
- **Causality.** `CausalEdgeType` (0–3) and `CausalReasonCode` (0–8) in `src/JIM.Models/Activities/CausalEdgeEnums.cs`, `ActivityRunProfileExecutionItemSyncOutcomeType` (ends at `DeprovisionQueued`) in `ActivityEnums.cs`, `ActivityRunProfileExecutionItemErrorType` (ends at `ClassMembershipRequirementsNotMet`), all append-only with ordinal tests in `test/JIM.Models.Tests/Activities/`. The web renders through `OutcomeDisplayMap`, `CausalityModelBuilder` and `CausalityCauseWording` (`src/JIM.Web/Causality/`); the worker records cross-item edges through `ExportCausalEdgeBuilder`.
- **Per-run uniqueness primitive.** `ExampleDataValueTrackerStore` (`src/JIM.Application/Servers/`): lock-free, keyed on (object type, attribute, value); internal to example data.
- **Parked-state precedent.** `ConnectedSystemServer.ReleaseParkedInitialPasswordsIfDeliveryChangedAsync` → `InitialPasswordDeliveryServer.ReleaseParkedForSyncRuleAsync` releases parked work when the configuration that parked it changes.
- **Delta synchronisation selects by Connected System Object modification.** `SyncDeltaSyncTaskProcessor` pages through Connected System Objects modified since the run's watermark. A change made on the Metaverse side outside an import is invisible to it.
- **Per-run state seam.** `Worker.cs` constructs a fresh `SyncEngine` per run and hands it to the processors; `SyncTaskProcessorBase` builds `_attributePriorityContext` once per run and threads it into every flow call. The export path constructs `ExportExecutionServer` (which owns its own `SyncEngine`) with the repository and connector factories. Sync Preview goes through `SyncRuleAttributeFlowPreviewAdapter` and `SyncRuleAttributeFlowProposalMaterialiser` (`src/JIM.Application/Servers/Preview/`).
- **Surfaces.** Mapping REST endpoints live in `SynchronisationController` (`CreateSyncRuleMapping`, `UpdateSyncRuleMapping` as PATCH through `SyncRuleMappingSettingsUpdate`, `StartSyncRuleAttributeFlowPreview`) with DTOs in `src/JIM.Web/Models/Api/SyncRuleMappingDtos.cs` (`SourceType` string, `InitialExportOnly`, `Enabled`, value processing). `New-JIMSyncRuleMapping` has four parameter sets (`ImportAttribute`, `ImportExpression`, `ExportAttribute`, `ExportExpression`); `Set-JIMSyncRuleMapping` takes `[bool]` settings so omission means "leave alone". The Add/Edit dialog is inline in `SyncRuleAttributeFlowTab.razor` (`MudSelect` "Source Type" with `Attribute` / `Expression`, a `@switch` body, the `InitialExportOnly` checkbox in the export branch, row chips through `Helpers.GetMappingTypeChipColour`). Attention indicators already exist (`InitialPasswordAttentionIndicator`, the `Attention` column on `SyncRuleList.razor`, the indicator trio on `ConnectedSystemList.razor`), and `OperationsPasswordsTab.razor` with `PasswordSynchronisationController`'s queue endpoints is the closest analogue to a filtered list with per-row and bulk actions.

### Proposed solution

```
Authoring (portal / REST / PowerShell)
  SyncRuleMapping + SyncRuleMappingGeneration (strategy, start, separator, limit, exclusions, Collision Remediation)
  Sources[0].Expression = base value (existing expression engine and editor)
                     │
Synchronisation run (import mode)          Export staging (export mode)
  engine records a GenerationRequest         engine records a GenerationRequest
  when the generated mapping is the          for the Pending Export change
  winning contributor and no assignment
  exists (sticky short-circuit)
                     │                                   │
                     └──────────── UniqueValueGenerationServer ────────────┘
                        candidates (pure) → gates: reservation, Metaverse,
                        connector space, probe (IConnectorUniquenessProbe)
                        → GeneratedValueAssignment (Proposed) → value applied
                     │
Export run
  target rejects → connector classifies UniqueValueAlreadyInUse
     Collision Remediation on and unanchored → next candidate, assignment Remediated,
        Pending Export updated, retried in-run → Committed on acceptance
        (import mode: Metaverse value revised; delta sync picks the object up)
     anchored → NeedsDecision (Allow the rename | Retry | Leave it), export parked
     off or unclassified → ordinary export error
                     │
Causality: outcome GeneratedValueRemediated, error GeneratedValueCollisionUnresolved,
  edge ExportRejectionCausedGeneratedValueRevision + reason codes; stat counters
```

### Key decisions

1. **A generated mapping is an expression mapping plus a settings row, not a third source shape.** The base value stays in `Sources[0].Expression`, so the expression editor, `ExpressionInputResolver`, Missing Input Behaviour (#1361) and the expression DTO fields are all reused unchanged. A new `SyncRuleMappingGeneration` entity (one-to-one, cascade delete) is the discriminator: `GetSourceType()` returns a new `GeneratedMapping` member when it is present. This is the smallest change to every surface that already understands expression mappings.

2. **The engine stays synchronous; generation is resolved as a deferred pass per page.** Gates need database queries and connector calls; `ProcessMapping` performs no I/O and must not start. Instead the engine records a `GenerationRequest` (object, mapping, evaluated base value) wherever a generated mapping would contribute, and the worker resolves all of a page's requests in one batch through the service before the page flush, then applies the values with the same provenance the engine would have stamped. This is the pattern reference attributes already use (a final pass after the main flow), it batches every gate for free (one Metaverse query, one connector-space query, one probe request per page), and it keeps priority decisions where they are: a request is only recorded when the generated mapping is the winning contributor. Sync Preview resolves requests in dry-run mode (local gates only, nothing persisted, no probe).

3. **Derived values resolve in the same flow (PRD Open Question 1).** Requests within one object are resolved in dependency order: a base expression may reference `mv[...]`, which for a generated mapping exposes the Metaverse Object's effective values *plus* the values resolved earlier in this pass for the same object. The decisive reason is not convenience: a one-run lag would need the *next* synchronisation to re-evaluate an object whose Connected System Object has not changed, which delta synchronisation never does. The lag would only converge under full synchronisation.

4. **Remediation in import mode revises the Metaverse value directly, and delta synchronisation learns to pick the object up.** This is the first thing the PRD glossed over. JIM owns the assignment, so writing the revised value to the Metaverse Object during the export run is within the stated boundary ("JIM revises only values it generated"). But nothing then carries the change to the other targets under delta synchronisation, for the reason in decision 3. The plan therefore extends delta selection with one general rule: a Metaverse Object whose generated value assignment changed since the watermark is re-synchronised (its joined Connected System Objects are included in the page) so export evaluation stages the Pending Exports for the other targets. This is the "Metaverse-side change pickup" that Design Decision 9 says a future Set Value would need; it is built once here and generic (keyed on the assignment's `LastUpdated`, no new Metaverse Object column). The alternative, staging exports for other systems from inside the export run, was rejected because it makes an export run perform synchronisation.

5. **Anchoring is derived, never stored.** A value is anchored when any *other* non-excluded Connected System Object joined to the same Metaverse Object holds the value in its connector space for the attribute an export mapping flows it to. Because optimistic apply (#1079) writes connector-space values only on export success and imports write them from the target, "the connector space holds it" is exactly "the target accepted it". Export-mode assignments are never anchored.

6. **"Allow the rename" records an authorisation; the worker performs the rename.** `JIM.Web` cannot probe (no connector dependency), so the portal and API action sets `RenameAuthorised` on the assignment and re-queues the parked Pending Export. On the next export run the target rejects the same value again, remediation finds the authorisation, treats the value as unanchored, generates the next candidate (with probes) and revises the Metaverse value; delta pickup (decision 4) then renames the account in the anchoring system. This is the schedule-consistent shape the PRD asks for and it keeps every connector call in the worker.

7. **Attributing a rejection to one generated attribute.** The second thing the PRD glossed over: Active Directory's `EntryAlreadyExists` does not say which attribute collided, and an export can carry two generated values (account name and UPN). Rule: a connector's classification may name the attribute; where it does not and the export carries exactly one generated value, that value is the one; where it carries several, the probe (if available) attributes the collision by checking each generated value; where neither can attribute it, the rejection is recorded as an ordinary export error whose advice explains why. JIM never guesses which value to revise.

8. **The probe proves it can see before it says NotFound.** A directory applies access control as a silent filter, so an empty result is not evidence of absence. Every LDAP probe batch includes one **control value** JIM already holds in that connector space for the same attribute; if the control is not returned, the whole batch is `CouldNotDetermine`. This costs nothing extra and is more reliable than trying to interpret result codes.

9. **Attempt limit scope (PRD Open Question 2).** Generation: per object per run (the candidate loop tries up to the limit within one resolution). Remediation: counted on the assignment for its lifetime (`RemediationCount`), with the same limit as the ceiling; exceeding it enters NeedsDecision rather than looping across runs.

10. **Probe batching (PRD Open Question 3).** LDAP: up to 50 candidates per OR filter, drawn lazily from the candidates that cleared the local gates (first window of 10, widening only if all are Found). SCIM: classification in the first connector phase; probe (`filter=userName eq "a" or userName eq "b"`, batches of 20) in the same phase, sequenced last and deferrable. SQL: classification of unique-constraint violations and an `IN` probe, deferrable.

11. **Example data migration (PRD Open Question 4)** is a follow-up issue filed at the end of Phase 2, not v1.

12. **The assignment is state, not history.** It is deleted the moment a generated mapping stops being responsible for the value (object gone, mapping gone or retyped, another contributor winning, value recalled), by FK cascade or by a page-flush reconciliation; nothing expires and nothing accumulates. See Assignment lifecycle.

13. **Missing knowledge never permits a rename, and export mode never generates over a value the target holds.** Both rules exist because a connector space clear removes what anchoring and stickiness read; see Assignment lifecycle.

14. **Third gloss: the configuration surfaces come before the connector work.** Integration scenarios configure JIM through PowerShell and REST, and Scenario 1's conversion needs only the local gates. Landing the three configuration surfaces right after the engine means the conversion and the dedicated scenario start early and the connector phase is verified against a real feed.

### Data model

**`SyncRuleMappingGeneration`** (new table, one-to-one with `SyncRuleMapping`, cascade delete):

| Field | Type | Notes |
|---|---|---|
| `SyncRuleMappingId` | int, unique FK | Discriminator: presence means generated mapping |
| `CollisionStrategy` | enum `GeneratedValueCollisionStrategy { Number = 0, Letter = 1 }` | |
| `StartValue` | int, default 1 | First suffix used on collision; the bare base value is always tried first |
| `Separator` | string?, default null | Inserted between base and suffix |
| `AttemptLimit` | int, default 1000 | Design Decision 4 |
| `CollisionRemediation` | bool, default true | Honoured only where a participating connector classifies |
| `Exclusions` | collection of `SyncRuleMappingGenerationExclusion (GenerationId, ConnectedSystemId)` | Per-system exclusion from availability checks |

Suffix position is not stored: before the first `@` if present, else appended (pure function).

**`GeneratedValueAssignment`** (new table):

| Field | Type | Notes |
|---|---|---|
| `Id` | Guid | |
| `MetaverseObjectId` / `MetaverseAttributeId` | Guid? / int? | Import mode; filtered unique index on the pair |
| `ConnectedSystemObjectId` / `ConnectedSystemObjectTypeAttributeId` | Guid? / int? | Export mode; filtered unique index on the pair; exactly one mode populated (check constraint) |
| `Value` | string | Current assigned value |
| `PreviousValue` | string? | Value before the last remediation |
| `State` | enum `GeneratedValueAssignmentState { Proposed = 0, Committed = 1, Remediated = 2, NeedsDecision = 3 }` | |
| `SyncRuleMappingGenerationId` | int, FK cascade | The generation row that produced it. Removing the mapping, deleting its Synchronisation Rule or changing its source type all delete the generation row, and the assignment goes with it (see Lifecycle) |
| `RemediationCount` | int | Decision 9 |
| `RenameAuthorised`, `RenameAuthorisedAt`, `RenameAuthorisedByName` | bool, DateTime?, string? | Decision 6; cleared on the next successful export of the value |
| `RejectedByConnectedSystemId`, `AnchoredByConnectedSystemId` | int?, int? | Populated on NeedsDecision entry for the error message and the list |
| `NeedsDecisionEnteredAt`, `NeedsDecisionActivityRunProfileExecutionItemId` | DateTime?, Guid? | Links the list row to the item that raised it |
| `Created`, `LastUpdated`, `CommittedAt` | DateTime, DateTime, DateTime? | `LastUpdated` drives delta pickup (decision 4) |

Cascade delete from the owning Metaverse Object or Connected System Object and from the generation row. Index on `State` for the NeedsDecision list and on `LastUpdated` for delta pickup.

### Assignment lifecycle

An assignment is **state, not history**: it exists exactly while a generated mapping is responsible for that object's attribute. The history of what was generated, corrected or decided lives in Activities and causality, which have their own retention. The table is therefore bounded at one row per (object, generated attribute), never grows with events, and needs no retention job.

| Trigger | Mechanism |
|---|---|
| Metaverse Object or Connected System Object deleted, including a connector space clear | FK cascade from the object |
| Generated mapping removed (recall or keep, #1537), its Synchronisation Rule deleted, or its source type changed away from "JIM generates it" | FK cascade from `SyncRuleMappingGeneration`; a kept value becomes an ordinary orphan value as #1537 defines |
| Another contributor wins the attribute (priority takeover), or the value is recalled or cleared | Page-flush reconciliation in the worker: for objects in the page that have assignments, delete any whose current value or provenance no longer matches (one query per page, only when assignments exist). A parked NeedsDecision export whose assignment is deleted is dropped with it |
| Mapping disabled; Metaverse Object awaiting deletion under a grace period | Retained: the value is dormant or still reserved, not gone |

Three rules follow from deletion being ordinary:

- **An object's own current value is always free for it.** After an assignment is deleted and later regenerated (a mapping re-created, a connector space cleared and re-imported), the gates would otherwise see the value held by the requesting object itself and move it to the next candidate. Every gate excludes the requesting object.
- **Export mode never generates over a value the target already has.** A Connected System Object that already holds a value for the target attribute (a joined pre-existing account, or an account re-imported after a clear) adopts that value and records the assignment as Committed. Generation fires only when the target has no value. Without this, a clear and re-import would lose the sticky knowledge and rename accounts back to their base value.
- **Missing knowledge never permits a rename.** Anchoring reads the connector space, and a cleared connector space is empty until its next full import. A participating Connected System with no completed full import since its last clear answers "cannot tell", and "cannot tell" resolves to NeedsDecision, never to remediation; the same rule the probe's `CouldNotDetermine` follows.

**Appended enum members** (all in Phase 1 so the ordinal tests change once):

| Enum | Member |
|---|---|
| `SyncRuleMappingSourcesType` | `GeneratedMapping = 4` |
| `ConnectedSystemExportErrorType` | `UniqueValueAlreadyInUse` |
| `ActivityRunProfileExecutionItemErrorType` | `GeneratedValueExhausted`, `GeneratedValueCollisionUnresolved` |
| `ActivityRunProfileExecutionItemSyncOutcomeType` | `GeneratedValueAssigned`, `GeneratedValueRemediated` |
| `CausalEdgeType` | `ExportRejectionCausedGeneratedValueRevision = 4` |
| `CausalReasonCode` | `GeneratedValueAlreadyInUse = 9`, `GeneratedValueAnchoredElsewhere = 10`, `GeneratedValueRenameAuthorised = 11` |

`GeneratedValueAssigned` is added beyond the PRD's list so the attribute history has a first event to scope to ("Generated by JIM" needs an origin event, not only a chip).

### The service

`UniqueValueGenerationServer` in `src/JIM.Application/Servers/`, exposed on `JimApplication` as `UniqueValues`, with these parts, each unit-testable without a run:

- `UniqueValueCandidates` (static, pure): `Sequence(baseValue, settings)` yields the bare base then suffixed candidates; owns suffix position and the letter strategy (`a`..`z`, `aa`..).
- `UniqueValueReservationSet` (per run, thread-safe; generalised from `ExampleDataValueTrackerStore`): keyed on (`scope`, `attributeId`, `value`) where scope is Metaverse or a Connected System id. Created by the worker per run, threaded into the engine and the export server like `AttributePriorityContext`.
- Gates, in order, each answering for a batch of candidates: `ReservationGate`, `MetaverseGate` (new repository method: Metaverse Object ids by attribute and string values; the index exists), `ConnectorSpaceGate` (existing `GetConnectedSystemObjectsByAttributeValuesAsync`, once per participating non-excluded system), `ProbeGate` (worker-only; `IConnectorUniquenessProbe` through `ConnectorFactory`; three-state; control value per batch). The participating systems for import mode are derived from export mappings sourced from the target Metaverse attribute; for export mode it is the one system.
- `ResolveAsync(IReadOnlyList<GenerationRequest>, UniqueValueResolutionOptions)` → per request: assigned value and new/existing assignment, or an exhaustion failure naming the attribute, last candidate and rejecting gate. Sticky short-circuit first: an existing Committed/Remediated/NeedsDecision assignment returns its value with no gates. Dry-run option for Sync Preview.
- `TryRemediateAsync(assignment, rejectingSystemId, reservationSet)` → next candidate through the gates, assignment to Remediated with `PreviousValue`, or `Anchored(byConnectedSystemId)` / `Exhausted`.
- `IsAnchoredAsync(assignment, rejectingSystemId)` per decision 5.
- `CommitAsync`, `EnterNeedsDecisionAsync`, `AuthoriseRenameAsync`, `RetryAsync`, `ReleaseNeedsDecisionForMappingAsync` (configuration change), `ListNeedsDecisionAsync` (paged, filterable by Connected System and Synchronisation Rule).

The service never knows which caller asked (FR 21); the deferred-request pattern is the seam a workflow step or an administrator action reuses.

### Connector capabilities

- `IConnectorUniquenessProbe` (`src/JIM.Models/Interfaces/`): `Task<IReadOnlyDictionary<string, UniquenessProbeOutcome>> ProbeAsync(ConnectedSystemObjectTypeAttribute attribute, IReadOnlyCollection<string> candidates, string? controlValue, List<ConnectedSystemSettingValue> settings, ILogger logger, CancellationToken ct)`; `UniquenessProbeOutcome { Found, NotFound, CouldNotDetermine }`. The LDAP implementation searches from the partition root (deliberately wider than the container scope) with a batched OR filter.
- `IConnectorCapabilities` gains `SupportsUniquenessProbe` and `SupportsUniquenessRejectionClassification`, mirrored on `ConnectorDefinition` and reconciled on startup like `SupportsPasswordPolicyDiscovery`, so the authoring dialog reads them from the database and `JIM.Web` gains no connector dependency.
- Classification: LDAP maps `EntryAlreadyExists` and attribute-uniqueness `ConstraintViolation` (Active Directory's forest-wide UPN check, OpenLDAP's unique overlay) to `UniqueValueAlreadyInUse`, naming the attribute where the server's diagnostic message does; `ScimExportErrorClassifier` gains the 409 `uniqueness` case (RFC 7644 section 3.12) and leaves 412 as `ConcurrencyConflict`; SQL maps PostgreSQL `23505` and SQL Server `2627`/`2601`.

## Implementation Phases

Each phase is a PR off `main`, TDD throughout, `dotnet build JIM.sln` and `dotnet test JIM.sln` clean before each. Phases 1 and 2 are inert until Phase 3 exposes configuration; a release cut between them freezes their migrations, which is fine because they are append-only. The PRD moves to `engineering/prd/doing/` with Phase 1.

### Phase 1: Model, persistence and vocabulary

1. `SyncRuleMappingGeneration`, `SyncRuleMappingGenerationExclusion`, `GeneratedValueAssignment` and their enums in `JIM.Models`; `SyncRuleMapping.Generation` navigation; `GetSourceType()` returns `GeneratedMapping`; `SyncRuleMapping.IsValid`-level rule: a generated mapping has exactly one expression source, a single-valued text target, and (import mode) is the only generated mapping for that target attribute across rules is *not* required (priority handles it).
2. Every appended enum member from the table above, with the ordinal tests extended (`SyncOutcomeTypeOrdinalTests`, `CausalEdgeOrdinalTests`, and the error type equivalent).
3. Two migrations (mapping generation; assignments), appended after `20260905125649_AddPasswordChangeOrigin`, named in the style of `AddInboundValueProcessingToSyncRuleMapping`.
4. Repository: `IMetaverseRepository.GetMetaverseObjectIdsByAttributeValuesAsync(attributeId, values)` as raw SQL over the existing `(AttributeId, StringValue)` index (the batched sibling of `GetMetaverseObjectByTypeAndAttributeAsync`); assignment CRUD and by-object lookups; NeedsDecision paged listing; the delta-pickup query (Metaverse Object ids whose assignment `LastUpdated` is after a watermark and which are joined to a given Connected System). Each member implemented in `JIM.PostgresData`, `JIM.InMemoryData` and registered with `ReadOnlySyncRepositoryGuard`.
5. Configuration change capture: `ConfigurationSnapshotService` / `ConfigurationDiffService` include the generation settings so Configuration Change Preview, diff and drift see them; `ConfigurationChangeClassifier` classifies a base-expression change on a generated mapping as affecting *new* objects only (committed values are sticky).
6. Lifecycle by cascade: assignments hang off the generation row and the owning object, so mapping removal (either #1537 choice), Synchronisation Rule deletion, source type change, object deletion and connector space clear all delete them at the database. Database-tier tests for each cascade.
7. Tests: model validation, ordinal tests, database-tier persistence and lookup tests in `test/JIM.Worker.Tests/` (database tests job).

### Phase 2: Generation engine

1. `UniqueValueCandidates`, `UniqueValueReservationSet`, gates without probe, `UniqueValueGenerationServer.ResolveAsync` with sticky short-circuit, exhaustion and dry-run.
2. `SyncEngine.AttributeFlow`: a generated mapping is evaluated like an expression mapping up to the point of producing a value; instead of applying it, the engine records a `GenerationRequest` on the flow result when the mapping is the winning contributor (priority, `NullIsValue` and Missing Input Behaviour all resolved first). `mv[...]` is available to generated base expressions (decision 3) and requests are dependency-ordered per object.
3. `SyncEngine.ExportStaging`: same for export mappings; the request is attached to the Pending Export attribute value change and resolved before staging.
4. Worker: `SyncTaskProcessorBase` owns the reservation set for the run beside `_attributePriorityContext` and resolves each page's requests before flush; applies values with provenance (`ContributedBySyncRuleId`) and creates assignments; records `GeneratedValueAssigned` outcomes and the `GeneratedValueExhausted` error via the standard RPEI path. Both full and delta synchronisation; `Worker.cs` passes the service alongside the `SyncEngine` it constructs per run. Export staging resolves through `ExportEvaluationServer`.
5. Delta pickup (decision 4): `SyncDeltaSyncTaskProcessor` selection unions the Metaverse Objects from the Phase 1 query.
6. Sync Preview: `SyncRuleAttributeFlowPreviewAdapter` and `SyncRuleAttributeFlowProposalMaterialiser` learn the source type; dry-run resolution renders "would generate `<candidate>`" with the gate that was consulted (zero side effects, per `engineering/SYNC_PREVIEW_ZERO_SIDE_EFFECTS.md`).
7. Stat counters on `ActivityRunProfileExecutionStats` (`TotalGeneratedValues`, `TotalGeneratedValueExhaustions`) and the `CausalitySummaryBuilder` clause for them.
8. Lifecycle at flush: the page-flush reconciliation that deletes assignments whose value or provenance no longer matches (priority takeover, recall, clear), dropping any parked export with them; every gate excludes the requesting object; export mode adopts an existing target value as Committed instead of generating.
9. Tests (red first): candidate sequencing and suffix position; letter strategy; reservation set under contention; gate ordering and short-circuit; stickiness across re-runs; priority supersession deleting the assignment; recall deleting the assignment; regeneration after deletion keeping the object's own value; export mode adopting a pre-existing value; intra-page collisions; exhaustion; dependency ordering with `mv[...]`; export-mode keyed on the Connected System Object; delta pickup selection. Runtime check on the light stack: CSV import generating Account Name into the Metaverse.
10. File the example-data migration follow-up issue (decision 11).

### Phase 3: Configuration surfaces (portal, REST, PowerShell)

Surface parity is delivered per capability, so this phase ships all three for configuration together.

1. Portal (`SyncRuleAttributeFlowTab.razor`, whose Add/Edit dialog is inline): a third "Source Type" item, "JIM generates it", with its own `@switch` branch; generation form (base expression with the existing `MudTextField` and `ExpressionTester`, "Assign the value to" reusing the target selects, strategy, start value, separator, attempt limit); live preview of the first candidate and collision sequence for sample inputs (pure `UniqueValueCandidates` call, no gates); derived system list with probe and classification columns read from `ConnectorDefinition`; per-system exclusion; Collision Remediation switch in its three states (FR 5); `Generated` row chip beside the existing `Initial Export Only` chip in both the table and card layouts, with a `GetMappingTypeChipColour` case. Mocks 01–03 in the artefact.
2. REST: `SyncRuleMappingDto`, `CreateSyncRuleMappingRequest` and `UpdateSyncRuleMappingRequest` gain a `generation` object (strategy, startValue, separator, attemptLimit, collisionRemediation, excludedConnectedSystemIds); `SourceType` renders `GeneratedMapping`; direction gating in `CreateSyncRuleMapping` follows the existing `InitialExportOnly` block; `SyncRuleMappingSettingsUpdate` carries the settings through PATCH; OpenAPI document updated; API tests beside `SyncRuleMappingUpdateApiTests.cs`.
3. PowerShell: `New-JIMSyncRuleMapping` gains `ImportGenerated` and `ExportGenerated` parameter sets (`-Generate` switch plus `-CollisionStrategy`, `-StartValue`, `-Separator`, `-AttemptLimit`, `-CollisionRemediation`, `-ExcludeConnectedSystemId`); `Set-JIMSyncRuleMapping` gains the same as `[bool]` / nullable settings so omission leaves them alone; `Get-JIMSyncRuleMapping` output shape documented; Pester in `src/JIM.PowerShell/Tests/SyncRuleMappings.Tests.ps1`.
4. `ConnectedSystemServer` mapping create and settings-update paths validate: target must be single-valued text; exclusions must be participating systems; remediation switch persisted as configured even where no connector classifies (the UI disables it; the engine gates on capability at run time).
5. Docs: `docs/powershell/synchronisation-rules.md` cmdlet sections and the API reference; the concept section in `docs/configuration/synchronisation-rules.md` (beside "Initial Export Only (outbound)") waits for Phase 8.

### Phase 4: Connector capabilities

1. `IConnectorUniquenessProbe`, `UniquenessProbeOutcome`, capability flags on `IConnectorCapabilities` and `ConnectorDefinition` (matching names so `ConnectorCapabilityMirror` picks them up; migration in the style of `AddSupportsPasswordPolicyDiscoveryCapability`), declarations on every connector including the mocks, `ConnectorDefinitionDto` exposure, `ConnectorFactory` wiring.
2. LDAP: probe from the partition root with batched OR filter and the control value (decision 8); classification of `EntryAlreadyExists` and uniqueness `ConstraintViolation` with attribute extraction from the diagnostic message where present.
3. SCIM: classification (`scimType: uniqueness`); probe with `or` filters, sequenced last.
4. SQL: classification and `IN` probe, deferrable to a follow-up if the phase runs long; say so in the PR if deferred.
5. `ProbeGate` in the worker's gate chain; capability-aware system list in the dialog now shows real values.
6. Tests: LDAP filter construction and batching, control-value handling, result-code classification (unit, no directory); SCIM classifier; Samba AD runtime verification of a brownfield object outside the import scope and an under-privileged bind.

### Phase 5: Collision Remediation and anchoring

1. `ExportExecutionServer.ProcessBatchSuccessAsync` failure branch: on `UniqueValueAlreadyInUse`, attribute the rejection (decision 7), look up the assignment, check the mapping's switch and the connector's classification capability, check anchoring (decision 5, or `RenameAuthorised`), then `TryRemediateAsync` → update the Pending Export's attribute value change, revise the Metaverse value in import mode (with provenance and change history), re-export within the run bounded by the limit; on acceptance `CommitAsync`. Parallel batches share the run's reservation set.
2. Anchored, anchoring unknown (a participating system with no completed full import since its last clear), or remediation exhausted: `EnterNeedsDecisionAsync`; Pending Export to `Failed` (no automatic retry); execution item error `GeneratedValueCollisionUnresolved` naming the rejecting and anchoring systems with remediation advice.
3. Switch off or unclassified: ordinary export error, assignment unchanged (FR 15).
4. Release on configuration change: `ConnectedSystemServer` calls `ReleaseNeedsDecisionForMappingAsync` when a generated mapping's settings change, mirroring `ReleaseParkedInitialPasswordsIfDeliveryChangedAsync`; released exports return to `Pending`.
5. Causality: `SyncExportTaskProcessor` maps the new export error type; `GeneratedValueRemediated` outcome with the attribute row Set and previous value; `ExportCausalEdgeBuilder.RecordGeneratedValueRevision` writes the `ExportRejectionCausedGeneratedValueRevision` edge from the rejecting export item to the revised object's next queueing item with the right reason code; `TotalGeneratedValuesRemediated` and `TotalGeneratedValuesNeedingDecision` on the stats with their summary-band clause.
6. Tests: remediation loop (accepted on second candidate; exhausted; anchored; authorised rename; switch off; unclassified; multi-generated-value attribution with and without probe), release on configuration change, edge and reason code recording, parallel batch safety. Runtime: Samba AD with a pre-created conflicting account created after the probe.

### Phase 6: Needs Decision surfaces (portal, REST, PowerShell) and transparency

1. Portal: Needs Decision list as an Operations tab modelled on `OperationsPasswordsTab.razor` (filter by Connected System and Synchronisation Rule, summary tile, per-row and bulk Retry); Allow the rename confirmation naming every system that will change and when (mock 06), from the list and from the identity; a `GeneratedValueAttentionIndicator` following `InitialPasswordAttentionIndicator` on `SyncRuleList.razor`'s Attention column, `ConnectedSystemList.razor` and the identity page; `MvoDetailsTable.razor` attribute row chips (`Generated by JIM`, `Corrected`) and the attribute-scoped causality Timeline (mock 04) hosted on `View.razor` beside the password panel; Activity Timeline events and exits (mock 05). `OutcomeDisplayMap`, `CausalityModelBuilder`, `CausalityCauseWording` and `CausalitySummaryBuilder` for the new members; bUnit tests in `test/JIM.Web.Tests/`.
2. REST: `GET generated-values/needs-decision` (paged) and `GET .../summary`, `POST generated-values/{id}/allow-rename` and `POST generated-values/{id}/retry` answering `202 Accepted` with the #1121 `wait` shape (`IPasswordChangeOutcomeWaiter` generalised or a sibling waiter keyed on the assignment); API tests.
3. PowerShell: `Get-JIMGeneratedValueDecision`, `Approve-JIMGeneratedValueRename`, `Reset-JIMGeneratedValueDecision` (verbs confirmed against the module's approved-verb usage at implementation), Pester tests, cmdlet docs.

### Phase 7: Integration testing

1. `Invoke-Scenario22-UniqueValueGeneration.ps1` (numbered after Scenario 21) with setup, run and teardown through `Run-IntegrationTests.ps1`, covering every row of the PRD's case table: modes, strategies, all four gates including the under-privileged bind, remediation on and off, mixed capability with a CSV target, anchoring and each exit, exhaustion, full and delta stability, supersession, lifecycle (supersession and recall deleting the assignment; a connector space clear and re-import in export mode adopting the target's value rather than renaming; a clear in import mode sending a rejection to NeedsDecision instead of remediating), causality assertions, and REST versus PowerShell parity.
2. `Generate-TestCSV.ps1` gains an `-OmitItOwnedAttributes` switch touching its three emission sites (hr-users row, the second dataset, the cross-domain header array), with the switch folded into the cache key in `Get-OrGenerate-TestCSV.ps1` / `Test-CsvCache.ps1` so cached archives for unconverted scenarios stay valid. `Setup-Scenario1.ps1` replaces the `samAccountName → Account Name` import mapping with a generated one, adds generated Email and UPN from it (exercising decision 3), and keeps the export mappings and DN expression unchanged; `Invoke-Scenario1` expectations are unchanged.
3. Audit of every scenario that references `samAccountName`, `email` or `userPrincipalName` (invoke scripts 1, 4, 5, 6, 10, 12, 13; setups 1, 2, 8, 10, 12, 13; data files for 4 and 5; the Samba populators) against the PRD's criterion (convert IT-owned flows; keep join keys and external identifiers, so the cross-domain scenarios 2 and 8 keep theirs), each conversion in its own commit, the decision per scenario recorded in the PR.
4. Full suite green in the sandbox (image builds with the proxy CA, per `CLAUDE.md`).

### Phase 8: Documentation and changelog

1. `docs/configuration/synchronisation-rules.md`: a "Generated values" section beside "Initial Export Only (outbound)" covering the source type, the four gates, Collision Remediation, anchoring, Needs Decision and its exits, import versus export mode, and what the probe can and cannot see; cross-links from `docs/concepts/expressions.md` and `docs/concepts/attribute-priority.md`.
2. `docs/connectors/index.md` capability matrix and the LDAP, SCIM and SQL connector pages: which connectors probe and which classify.
3. Engineering: developer guide section on the service, the deferred-request seam and the assignment model; a causality reference listing every outcome, error, edge and reason member (none exists today; `engineering/plans/CAUSAL_PROVENANCE.md` is the nearest artefact), created here because this feature is the first to append to all four enums at once.
4. `CHANGELOG.md`: `✨` entries for Unique Value Generation and Collision Remediation under `[Unreleased]`; the PRD and this plan move to `done/` with the issue closed.

## Success Criteria

The PRD's Acceptance Criteria, verbatim, plus:

- No connector call path reachable from `JIM.Web` (build-level: `JIM.Web` references no connector project).
- 100k-object import with a generated text flow within 10% of the same import with a plain expression flow on the local stack (NFR).
- Every appended enum member has an ordinal test.
- Scenario 1 converted and green; Scenario 22 green; the full suite green.

## Benefits

- Self-healing collisions with a complete audit trail, and an explicit decision where healing would rename a live account.
- Realistic demo and integration data without IT-owned attributes in HR feeds.
- One generation service, one assignment model and one Metaverse-side pickup mechanism that internally managed identities, workflows and a future Set Value reuse rather than re-implement.

## Dependencies

- All landed: #1087 / #1495 causality, #1121 (Phases 1–3), #1079, #223, #843, #91, #1361. No new NuGet packages: LDAP filters use `System.DirectoryServices.Protocols` already in the LDAP connector; SCIM uses the existing client.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Probe cost dominates large imports | Probes run only for candidates that cleared the local gates, batched per page and per system; the lazy window (decision 10) bounds requests; the NFR benchmark gates Phase 4. |
| Silent ACL filter makes the probe report NotFound for values that exist | Control value per batch (decision 8); a `CouldNotDetermine` batch falls back to local gates and remediation handles the residual collision. |
| Remediation revises the wrong value on a multi-value export | Decision 7: no attribution, no revision; recorded as an ordinary error with advice. |
| Ping-pong across runs (two systems each rejecting the other's accepted value) | Anchoring stops the second revision; `RemediationCount` ceiling stops any residual loop; NeedsDecision is the terminal state. |
| Delta pickup query selects too much | Keyed on the assignment's `LastUpdated` with an index; only Metaverse Objects with a changed assignment since the watermark, joined to the system being synchronised. |
| Engine sync/async boundary leaks I/O into `SyncEngine` | The deferred-request pattern keeps `SyncEngine` free of I/O; a unit test asserts `ProcessMapping` never touches the service. |
| A release between phases freezes a migration the next phase wants to reshape | Migrations are append-only anyway; Phase 1 lands both tables in their final shape and later phases add columns only. |
| Parallel export batches remediate the same value concurrently | The run reservation set is shared and atomic; the assignment update uses optimistic concurrency. |
| DN (RDN) collisions are mistaken for attribute collisions | The RDN attribute is remediated only if it is itself a generated mapping; otherwise the rejection is an ordinary error naming the DN. |
| Priority sentinel (`int.MaxValue`) means a new generated mapping never wins where other contributors exist | The dialog prompts to order the priority list when the target attribute already has contributors, as the existing priority UI does. |
