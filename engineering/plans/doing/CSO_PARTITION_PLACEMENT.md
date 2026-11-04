# Truthful Connected System Object Partition

- **Status:** Doing (Phases 0 to 2 complete)
- **Issue:** [#2723](https://github.com/TetronIO/JIM/issues/2723)
- **Related:** [#2715](https://github.com/TetronIO/JIM/issues/2715) (per-Partition watermarks; this is its Follow-up 1), [#2720](https://github.com/TetronIO/JIM/issues/2720)
- **Design:** approved 4 November 2026 (decisions D1 to D5 below, all as recommended)
- **Last Updated:** 2026-11-04

## Overview

Set each Connected System Object's Partition from where the object actually is, not from the Run Profile that first read it. A Partition-scoped run (a Full Import, or a Delta Import that read everything) then detects the deletion of objects first created by an unscoped run or provisioned by JIM, retries their unconfirmed exported creates, and can retire them when their Object Type changes.

The Connector owns where an object is; JIM owns the policy (prefer a selected Partition, keep a known value when the Connector has no answer). There is no backfill migration: every read restamps.

## Business Value

- A deployment that runs only Partition-scoped Run Profiles misses deletions today, which leaves stale Connected System Objects (and the Metaverse Objects joined to them) in place indefinitely.
- A provisioned object whose create was exported but never confirmed is never retried by a scoped run.
- The deletion limit's percentage base counts only objects stamped with the run's Partition, so the limit is stricter than configured.

## The Problem

`ConnectedSystemObject.PartitionId` is written from the Run Profile, not from the object:

| Writer | Today |
|--------|-------|
| `SyncImportTaskProcessor.cs:3890` (create) | `_connectedSystemRunProfile.Partition?.Id`: null for an unscoped Run Profile |
| `SyncImportTaskProcessor.cs:2988-2991` | Fills a null value only on a scoped run that reads the object again |
| `ExportEvaluationServer.cs:3041-3051` | Pending Provisioning object created with no Partition |
| `ExportExecutionServer.cs:3767-3865` | Applies the written DN, never a Partition |

Every Partition-scoped reader filters `cso.PartitionId == partitionId`, so null-Partition objects are invisible to it:

| Reader | Effect today |
|--------|--------------|
| Deletion detection: `SyncImportTaskProcessor.cs:788-796` to `BuildDeletionDetectionQuery` (`ConnectedSystemRepository.cs:4329-4337`) | Deleted objects created unscoped or provisioned are never marked deleted by a scoped run (the issue) |
| Unconfirmed-create retry: `SyncImportTaskProcessor.cs:808, 1950, 1976` to `ConnectedSystemRepository.cs:4469` (Summary-tier SQL) and `4434` (full graph) | A scoped run never retries a provisioned create no import confirmed |
| Object Type change retirement (#2049): `SyncImportTaskProcessor.cs:1449-1527` | A scoped run cannot retire a held object created unscoped |
| Deletion-limit base: `SyncImportTaskProcessor.cs:1468, 3401-3402` to `SyncRepository.cs:70-77` | The base is too small, so the limit trips early |
| Progress count: `SyncImportTaskProcessor.cs:789` to `ConnectedSystemRepository.cs:3432-3439` | `ObjectsToProcess` undercounts |
| Content-hash skip: `SyncImportTaskProcessor.cs:2396` | A null-Partition object is never skipped by a scoped run, and always skipped by an unscoped one |
| Scope preview: `ConnectedSystemScope.cs:128-131`, `ConnectedSystemScopeSelectionPreviewAdapter.cs:193-200` | Null-Partition objects are "undetermined" and left out of preview counts |
| `GET .../connector-space/count?partitionId` (`SynchronisationController.cs:1499-1512`), `Get-JIMConnectedSystemObject -Count -PartitionId` | Counts are low |

The in-memory twins in `src/JIM.InMemoryData/SyncRepository.cs` (367, 375, 828, 843, 858, 873, 888, 918, 949) filter the same way.

Export Run Profiles accept a Partition (`ConnectedSystemRunProfile.cs:67`) but nothing reads it at run time, so exports are unaffected.

## Decisions

| | Decision | Rejected |
|-|----------|----------|
| **D1** | A Connector capability, `IConnectorPartitionPlacement`, answers which Partition an object identifier (for LDAP, the DN) is in. The LDAP Connector implements it with `LdapConnectorUtilities.PartitionForDn`, the same rule #2715 uses to judge which Partition's watermark covers a change, so the two cannot disagree. JIM calls it with each non-delete import object's secondary External ID (else its External ID) | The Connector stamping a Partition on each import object and export result: equally correct, but the rule is applied in two places and JIM cannot re-place an object later. JIM composing `IConnectorContainment.IsWithinContainer`: a string-suffix rule that misplaces an escaped comma in the leaf RDN |
| **D2** | A provisioned object is placed when its export succeeds, from the DN the Connector wrote (`ConnectedSystemExportResult.SecondaryExternalId`, `LdapConnectorExport.cs:2051-2053`), in `ExportExecutionServer.BatchUpdateCsosAfterSuccessfulExportAsync` | At evaluation: no Connector there, and a reused Pending Provisioning object's DN can change before export. Only at the confirming import: a scoped run still never retries an unconfirmed create |
| **D3** | Every non-delete read restamps when the placement differs. No answer keeps the stored value (on create, the Run Profile's Partition, as today). Placement prefers the longest matching selected Partition, then any Partition. A Partition-only change gets no execution item, one summary log line per run | Stamping once at create: a wrong or missing value would never heal |
| **D4** | #2715's no-watermark fallback stays whole-run (reads every target Partition in full); per-Partition fallback (`CompletelyReadPartitionIds`) is [#2739](https://github.com/TetronIO/JIM/issues/2739) | Doing it here: about the size of this whole issue, for speed only |
| **D5** | `FK_ConnectedSystemObjects_ConnectedSystemPartitions_PartitionId` becomes `ON DELETE SET NULL`, in its own pull request at the bottom of a stack. Today it is NO ACTION (`20260407072530_AddPartitionIdToConnectedSystemObject.cs:24-29`), so a hierarchy refresh that removes a Partition objects still reference (`ConnectedSystemServer.cs:4920-4940`) is expected to fail its save with 23503; this change makes far more objects reference one | Refusing the refresh (worse experience); cascading (deletes objects behind deletion detection's back) |

## Design

### Placement policy

A JIM.Models helper (`ConnectedSystemPartitionPlacement`) applies D3 over the capability. It is prepared once per run from the Connected System's Partitions, so Partition DNs are parsed once, not once per object.

### At import

Every non-delete LDAP import object carries its `distinguishedName`: a secondary External ID cannot be deselected (`ConnectedSystemServer.cs:5890-5895`), and the Connector synthesises it where the directory does not return it (`LdapConnectorImport.cs:1336-1345`). No change source changes; the processor places each object in its hydration pre-pass. Deletes are never placed.

- **Outside every Partition:** keep the stored value (on create, the Run Profile's Partition); count and warn once per run.
- **In a deselected or untargeted Partition:** placed truthfully, preferring a matching selected Partition.
- **A Connector without the capability:** today's behaviour, unchanged.

### At export execution

After a successful create or rename, place from `SecondaryExternalId`, else the object's stored DN, and write with a narrow `ISyncRepository.UpdateConnectedSystemObjectPartitionsAsync` (raw `UPDATE ... FROM unnest` carrying a `// bulk-sql-column-exemption:` comment; tracked instances fixed up per `src/CLAUDE.md`; in-memory twin and `ReadOnlySyncRepositoryGuard` entry). `PrepareConnectorForExport` (`ExportExecutionServer.cs:608-627`) holds the Connector and the Connected System; confirm its Partitions are loaded.

### Unchanged

Watermarks (positions per Partition, not object stamps), Export Run Profiles, the Connected System Object detail surfaces (the Partition is not exposed there), Activity statistics.

## Implementation Phases

Each phase starts with failing tests.

### Phase 0: Partition removal sets objects' Partition to null (D5, bottom pull request) ✅

- Red: `ConnectedSystemHierarchyPersistenceDatabaseTests.RefreshHierarchy_PartitionHoldingConnectedSystemObjectsNoLongerInTheDirectory_RemovesItAndClearsTheirPartitionAsync` (RequiresPostgres).
- `OnDelete(DeleteBehavior.SetNull)` in `JimDbContext`, a migration, `DeletePathForeignKeyCoverageTests`, `MigrationDesignerChainTests`, `dotnet ef migrations has-pending-model-changes`.
- Confirmed by the red test, which drives `ImportConnectedSystemHierarchyAsync` with the Connected System loaded change-tracked, as the REST endpoint loads it: the save failed with `23503: update or delete on table "ConnectedSystemPartitions" violates foreign key constraint "FK_ConnectedSystemObjects_ConnectedSystemPartitions_PartitionId" on table "ConnectedSystemObjects"`. Recording that failure on the Activity saves on the same context, which still holds the Partition marked deleted, so it failed again with the same error and the Activity was left **In Progress** with no error message. The portal's **Retrieve Hierarchy** never reached it: it saves a graph loaded on another context, so a removed Partition is reported as removed but its row (with its Containers and objects) stays. On the REST path the removed Partition's Containers are kept with no Partition and no Connected System. None of these three is changed here.

### Phase 1: Capability and the LDAP rule ✅

- `IConnectorPartitionPlacement` in JIM.Models; LDAP implementation over `PartitionForDn`.
- `test/JIM.Worker.Tests/Connectors/LdapConnectorPartitionPlacementTests.cs`: one Partition; nested suffixes return the longest match; outside every Partition returns null; an unreadable DN returns null; an escaped comma in the leaf RDN is not placed in a look-alike suffix.
- `JIM.Models.Tests` policy tests: an inner deselected suffix prefers the selected outer; no selected match falls back to any Partition; a Connector without the capability returns null.
- A conformance test: every Connector with `SupportsPartitions` implements the capability.

### Phase 2: Import processor ✅

- Placement in the hydration pre-pass; restamp replaces `SyncImportTaskProcessor.cs:2988-2991`; the skip predicate becomes "stored equals placement"; one summary log line.
- `Workflows/ImportPartitionPlacementTests.cs`: an unscoped Full Import stamps each object's Partition; unscoped-created then deleted is marked deleted by a scoped Full Import (the issue); a scoped Full Import does not mark another Partition's object deleted; a moved object is restamped with no execution item; placed nowhere keeps the stored value; no capability keeps the Run Profile stamp; a reported delete leaves the Partition; a confirming import stamps a provisioned object; the deletion-limit base counts unscoped-created objects; an Object Type change retires an unscoped-created held object.
- `ImportContentHashSkipTests`: stored Partition differs, not skipped and restamped; matches, skipped; unscoped run with a null stored Partition, not skipped.

### Phase 3: Provisioning (D2)

- `Workflows/ProvisionedObjectPartitionTests.cs`: a successful create stamps the Partition written to; a scoped Full Import retries a never-confirmed exported create (red today); does not retry one in another Partition; provisioned, confirmed by an unscoped import, then deleted, is marked deleted by a scoped Full Import; an export result without a DN uses the provisioned DN.
- `CsoPartitionIdPersistenceDatabaseTests`: the narrow update sets only the Partition column; tracked instances match and a later save succeeds. `BulkSqlColumnConventionTests` passes with the exemption comment.

### Phase 4: Integration, documentation, changelog, plan lifecycle

See below. Move this plan to `engineering/plans/done/` in the pull request that closes #2723.

## Integration Testing

**Scenario 009** (`Invoke-Scenario-009-PartitionScopedImports.ps1`): a new step, `UnscopedCreatedDeletion`, run last, on OpenLDAP, 389 Directory Server and Samba AD. Add one user per Partition; Full Import (Unscoped), after which `Get-JIMConnectedSystemObject -Count -PartitionId` includes them (red today); delete them in the directory; Full Import (Scoped) deletes only its own Partition's user, then Full Import (Scoped - Second) the other.

**Every other reader** (searched `-PartitionId`, `Scoped)`, `UseScopedImport`, `ExportNotConfirmed`, `partitionId=`):

| Scenario | Observes | Needs |
|----------|----------|-------|
| 002, 008 | Full Import (Scoped) on the target after provisioning | Run at Medium on Samba AD and OpenLDAP as regression guards |
| 014, 024 (AD lab) | `ExportNotConfirmed`, unscoped Run Profiles only | None |
| 025 | Not the count or deletion paths | None |

## Documentation and Changelog

- `docs/configuration/run-profiles.md`: a Partition-scoped run's deletion detection and limits cover every object in that Partition, however it was created.
- `engineering/developer/connectors.md`: the capability.
- Changelog 🐛: a partition-targeted import now detects the deletion of objects first imported by an unscoped Run Profile or provisioned by JIM, and retries a provisioned object no import confirmed (#2723).

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| The first Full Import after the upgrade loses the content-hash skip for restamped objects | One-off; measured at Large on OpenLDAP |
| Objects deleted before the upgrade keep a null Partition and only an unscoped complete read marks them deleted | Pre-release: no deployments carry such rows; and no wave of deletions at upgrade |
| A genuine move: a scoped read of the old Partition before the new one's read marks the object deleted | Inherent to deletion by absence, and true today |
| Placement cost per object | Partition DNs parsed once per run; measured at Large |

## Follow-ups (not needed for #2723)

- **D4:** per-Partition fallback for a Delta Import with no watermark for one of its Partitions: [#2739](https://github.com/TetronIO/JIM/issues/2739), blocked by #2723.
- **The Run Profile's Partition foreign key** has the same NO ACTION shape (snapshot 6835-6837): removing a Partition a Run Profile targets is expected to fail the refresh. Setting it null would silently widen a scoped Run Profile to every Partition, so it needs its own decision: [#2740](https://github.com/TetronIO/JIM/issues/2740).

## Inferred, Not Yet Confirmed

- OpenLDAP subordinate glue returning nested-suffix entries in a superior search; AD domain-NC searches not returning DomainDnsZones objects.
- Partitions being loaded at export preparation.
- Whether an Active Directory cross-domain move keeps the objectGUID and reports a delete in the source Partition.

## Departures

What was built differently from the plan above, and why.

| Phase | Plan | Built | Why |
|-------|------|-------|-----|
| 1 | The LDAP Connector implements the capability with `LdapConnectorUtilities.PartitionForDn` | The rule moved into `LdapPartitionPlacer`; `PartitionForDn` (the watermark judge) now delegates to it | One rule either way, and the placer can parse each Partition's DN once per run rather than once per object; the watermark path keeps its per-call form |
| 1 | `IConnectorPartitionPlacement` answers which Partition an identifier is in | `PreparePartitionPlacement(partitions)` returns an `IConnectorPartitionPlacer` whose `GetPartition(identifier)` answers | The prepared placer is what lets the Connector parse Partitions once; the policy prepares one over the selected Partitions and, only where some are deselected, one over them all |
| 1 | No answer for a Connector without the capability | `ConnectedSystemPartitionPlacement.Prepare` also has no answer for a Connected System with no Partitions | Placing among no Partitions would only count every object as placed nowhere |
| 2 | No answer keeps the stored value | No answer keeps the stored value; where nothing is stored either, the Run Profile's Partition, for an existing object as for a new one | The old backfill becomes the last fallback (`PartitionIdToRecord`: placement, else stored, else the Run Profile's), so create and update agree, nothing is ever cleared, and a Connector without the capability keeps exactly today's behaviour, skip predicate included |
| 2 | The skip predicate becomes "the stored Partition equals the placement" | "The stored Partition equals the Partition the run would record" | The same where the object is placed; elsewhere it is today's predicate, so an object placed nowhere is not denied its skip on every run |
| 2 | Every non-delete import object is placed | Every object the run might create or update: not a delete, and not one the Connector could not read (an object-level import error); one with an unreadable attribute value is placed | Nothing is created or updated from an object the Connector could not read, so counting it as placed nowhere would only raise a false warning |
| 2 | One summary log line: restamped, placed nowhere | The line also counts objects placed in a Partition, and the import span carries all three | The placed count is the denominator that makes the other two readable |
| 2 | `CsoPartitionIdPersistenceDatabaseTests` covers the restamp if not already covered | A characterisation test (`PartitionMovedWithNothingElseChanged`), green from the start | The raw bulk update writes `PartitionId` for every object in the update list, changed or not, and an import always puts every compared object in that list; the existing backfill test covered null to a value, the new one covers one Partition to another through the `ISyncRepository` call the import makes |
