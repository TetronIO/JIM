# Per-Partition Delta Import Watermarks

- **Status:** Doing (Phases 1 to 4 complete; the changelog source, Phase 5 onwards, outstanding)
- **Issue:** [#2715](https://github.com/TetronIO/JIM/issues/2715)
- **Depends on:** [#2720](https://github.com/TetronIO/JIM/issues/2720) (merged in PR #2721, `ConnectedSystemImportResult.CompleteRead`)
- **Related:** [#2723](https://github.com/TetronIO/JIM/issues/2723) (truthful `ConnectedSystemObject.PartitionId`), [#2724](https://github.com/TetronIO/JIM/issues/2724) (clearing leaves the SQL and SCIM watermarks)
- **Design:** Option B, approved 3 November 2026; the open questions below were decided the same day
- **Last Updated:** 2026-11-03

## Overview

Give every Partition of a Connected System its own Delta Import watermark, held by JIM in a new table, so a Run Profile that reads one Partition moves only that Partition's starting point. Today the LDAP Connector keeps one watermark for the whole Connected System, so a Delta Import (or a Full Import) of Partition A advances the watermark past changes in Partition B that B's own Delta Import has not read yet, and B skips them for good.

The Connector still owns what a watermark means. JIM stores one opaque value per Partition, hands each run the values for the Partitions it targets, and records the values the run returns only once the run has staged everything it read (the #1868 rule, unchanged).

Only the LDAP Connector has Partitions. The SQL, SCIM 2.0 Client, SCIM 2.0 Service Provider and File Connectors keep their Connected System-level watermark exactly as today.

## Business Value

- Partition-targeted Delta Imports stop silently losing changes. This is a synchronisation correctness bug: JIM reports success while the Metaverse drifts from the directory.
- Administrators can schedule each Partition independently (a busy domain hourly, a quiet one daily) and run partition-scoped Full Imports, without either corrupting the other's Delta Imports.
- The one-off full read each Delta Import Run Profile makes after the upgrade also repairs any change #2715 has already caused JIM to skip, and detects deletions by absence (#2720) on the way.
- Parallel Schedule steps that run two partition-scoped imports of one Connected System at once stop racing on a single watermark.

## The Problem

### Where the watermark lives today

| What | Where |
|------|-------|
| The stored watermark | `ConnectedSystem.PersistedConnectorData`, one string per Connected System |
| LDAP's form of it | `LdapConnectorRootDse` serialised as JSON: `HighestCommittedUsn` (+ `InvocationId`, `DnsHostName`), `LastChangeNumber` + `LastChangeTime`, `LastAccesslogTimestamp` + `LastAccesslogTimestampIsLogged`, beside the directory's capabilities and the pinned domain controller (`src/JIM.Connectors/LDAP/LdapConnectorRootDse.cs`) |
| Captured | `LdapConnectorImport.GetRootDseInformationAsync` (line 734) calls `ILdapDeltaSource.CaptureWatermarkAsync` (line 787) on page 1 of every Full and Delta Import |
| Recorded | `SyncImportTaskProcessor.PerformImportAsync`, end of run (lines 1106 to 1118), and the close-connection override (line 632) |
| What a Run Profile reads | `ConnectedSystemExtensions.GetTargetPartitions` (`src/JIM.Models/Staging/ConnectedSystemExtensions.cs:175`): the targeted Partition, or every selected one |

The run reads only its target Partitions, but writes a watermark that every Run Profile of the Connected System then starts from.

### Timeline: two Run Profiles, one skipped change

OpenLDAP, Partitions `dc=corp,dc=local` (Corp) and `dc=partners,dc=local` (Partners). Run Profiles **Delta Import (Corp)** and **Delta Import (Partners)**, each targeting one Partition.

| Time | Event | Stored watermark |
|------|-------|------------------|
| 09:00 | Full Import (unscoped) | 09:00 |
| 09:10 | Directory: `uid=pat,ou=people,dc=partners,dc=local` changes (P1) | 09:00 |
| 09:20 | Directory: `uid=cal,ou=people,dc=corp,dc=local` changes (C1) | 09:00 |
| 09:30 | Delta Import (Corp) reads Corp writes from 09:00: imports C1. Captures the latest write within its Partition (#2712): 09:20 | **09:20** |
| 09:40 | Delta Import (Partners) reads Partners writes from **09:20**: P1 (09:10) is not returned | 09:20 or later |

P1 is never imported by a Delta Import. Without #2712's confinement the capture would be the latest write anywhere, which skips P1 just the same. The USN and changelog sources fail identically, and a Full Import (Corp) at 09:30 would do the same to Partners.

## Technical Architecture

### Decisions at a glance

| Area | Decision |
|------|----------|
| Storage | New table `ConnectedSystemPartitionWatermarks`, one row per Partition, opaque `Value` owned by the Connector |
| Connector contract | `IConnectorPartitionWatermarks` (set before the run, describe for display) + `ConnectedSystemImportResult.PartitionWatermarks` |
| Complete reads | `CompleteRead` stays the whole-run answer. `CompletelyReadPartitionIds` is **not** added in this change; see [Decision 1](#decisions) |
| Recording | At the existing end-of-run point, atomically with `PersistedConnectorData`; nothing on failure, cancellation, withheld deletes, incomplete deletion detection or the close override |
| LDAP watermark fields | Removed from `LdapConnectorRootDse`; new `LdapPartitionWatermark` per Partition |
| LDAP fallback | Any target Partition without a usable watermark: the whole run reads every object in its Partitions (as #2720) |
| Existing data | No migration of the old blob. First Delta Import of each Run Profile reads in full, once |
| Non-LDAP Connectors | Unchanged |

### Data model

New entity `src/JIM.Models/Staging/ConnectedSystemPartitionWatermark.cs`:

| Column | Type | Notes |
|--------|------|-------|
| `PartitionId` | `int`, PK | FK to `ConnectedSystemPartitions.Id`, `ON DELETE CASCADE` |
| `ConnectedSystemId` | `int`, not null, indexed | FK to `ConnectedSystems.Id`, `ON DELETE CASCADE`. Redundant with the Partition's own, kept for the per-Connected System read and the sweep |
| `Value` | `text`, not null | Opaque; the Connector's own format. A row with no value has no meaning, so "no watermark" is the absence of a row |
| `RecordedAt` | `timestamp with time zone`, not null | UTC; when a completed run last recorded the row, changed or not |

- **No navigation properties** on `ConnectedSystem` or `ConnectedSystemPartition`. That keeps the rows out of configuration snapshots (`ConfigurationSnapshotService.BuildPartitions` names its fields explicitly, `src/JIM.Application/Services/ConfigurationSnapshotService.cs:735`), out of `MarkConnectedSystemGraphForUpdate`, and out of the OpenAPI property walk.
- Configure both foreign keys explicitly in `JimDbContext` with `OnDelete(DeleteBehavior.Cascade)`, beside the "Configuration ownership" block. PostgreSQL accepts the two cascade paths (Connected System to Partition to row, and Connected System to row).
- Partition identity survives a hierarchy refresh: `ConnectedSystemServer` matches Partitions by `ExternalId` (around line 4740), so a refresh keeps rows; a Partition the directory no longer has is removed and its row goes with it by cascade.

### Migration

One migration, `AddConnectedSystemPartitionWatermarks`:

- Creates the table, both FKs and `IX_ConnectedSystemPartitionWatermarks_ConnectedSystemId`.
- Adds `Activities.ImportPartitionWatermarksFrom` and `Activities.ImportPartitionWatermarksTo` (`text`, nullable); see [Activity, portal, REST API and PowerShell](#activity-portal-rest-api-and-powershell).
- No data migration (pre-release; see [Existing data](#existing-data)).

Released migrations are append-only (`released-migrations.lock`, `ReleasedMigrationImmutabilityTests`), so the migration's timestamp must sort after the newest listed id; it will, being new. If `main` gains a migration before this merges, `dotnet ef migrations remove` and re-add after merging `main`, or `MigrationDesignerChainTests` fails naming the columns. Check `dotnet ef migrations has-pending-model-changes` before pushing.

### Deletion paths, factory reset, clearing and deselection

| Path | What happens to the rows | Change needed |
|------|--------------------------|---------------|
| Delete a Connected System (`ConnectedSystemRepository.DeleteConnectedSystemAsync`, line 8602) | Step 5 deletes the Partitions; the rows cascade | Add the table to `ConnectedSystemDeletion.RemovedTables` in `DeletePathForeignKeyCoverageTests` with the comment "removed by cascade from ConnectedSystemPartitions (step 5)". Both FKs cascade, so the guard passes either way; the entry keeps it honest per `src/CLAUDE.md`. Add an explicit `RemoveRange` to `DeleteConnectedSystemViaTrackedFallbackAsync` (line ~8930): the in-memory provider cascades only tracked dependents |
| Factory reset (`SystemRepository.ResetSystemAsync`) | `TRUNCATE ... ConnectedSystems ... CASCADE` empties the table | None; add an assertion to `SystemResetDatabaseTests` |
| Hierarchy refresh removes a Partition | Cascade | Test only |
| Partition deselected | Row must go: a Partition reselected later must not resume from a watermark taken before objects were obsoleted while it was out of scope | Delete in two places, one SQL statement: when a save leaves a Partition deselected (`ConnectedSystemServer.UpdateConnectedSystemAsync` and `UpdateConnectedSystemPartitionAsync`, line 7263), and in the processor's end-of-run write. The first is the robust one (it covers a deselect, then a run that obsoletes the Partition's objects but fails before its end-of-run write, then a reselect); the second covers any path the first misses |
| Clear the Connector Space (`ClearConnectedSystemObjectsAsync`, line 6947) | Rows must go: an empty Connector Space has no baseline | Add a `DELETE` to `DeleteAllConnectedSystemObjectsAndDependenciesAsync`, inside its transaction (see Decision 4) |

### Raw SQL

**The write is raw SQL, so the BulkColumns rules apply.** The upsert is the table's write path, not a narrow fix-up, so it is not exempt:

- New `src/JIM.PostgresData/Repositories/PartitionWatermarkBulkColumns.cs`, modelled on `ServiceHeartbeatBulkColumns` (the existing upsert precedent, used by `SystemRepository.UpsertServiceHeartbeatAsync`, line 293): insert list `PartitionId, ConnectedSystemId, Value, RecordedAt`; update list `Value, RecordedAt`; exclusions `PartitionId` (the conflict key) and `ConnectedSystemId` (a Partition never changes Connected System).
- Statement shape, column lists from `BulkSqlHelpers.ToQuotedList`, typed parameters (`NullableParam` is not needed; nothing is nullable):

  ```sql
  INSERT INTO "ConnectedSystemPartitionWatermarks" (<list>)
  SELECT v."PartitionId", p."ConnectedSystemId", v."Value", v."RecordedAt"
  FROM unnest(@ids, @values) ... v
  JOIN "ConnectedSystemPartitions" p ON p."Id" = v."PartitionId" AND p."ConnectedSystemId" = @connectedSystemId
  ON CONFLICT ("PartitionId") DO UPDATE SET "Value" = EXCLUDED."Value", "RecordedAt" = EXCLUDED."RecordedAt";
  ```

  The join does two jobs: a Partition removed by a hierarchy refresh while the run was going is skipped rather than failing the run after it has staged everything (an FK violation there would be the worst possible moment), and a Partition of another Connected System can never be written.
- `ON CONFLICT` rather than an EF read-modify-write, because two Run Profiles of one Connected System can run at once: `TaskingRepository.GetNextWorkerTasksToProcessAsync` (line 241) batches every task of a parallel Schedule step, and two steps may target the same Connected System. Last writer wins, and that is safe: each run's value only claims "this run has read and staged everything up to here", so a regression costs a re-read, never a skipped change.
- One repository method, `RecordImportWatermarksAsync(connectedSystemId, persistedConnectorData, upserts, deletes)`, writes `PersistedConnectorData` (as `UpdateConnectedSystemPersistedConnectorDataAsync` does today, line 548), the upserts, the null-value deletes and the deselected-Partition sweep in one transaction, enrolling in an ambient one if present (the `DeleteConnectedSystemAsync` pattern). It touches no tracked state on relational providers; the non-relational fallback uses an isolated context, as the existing persisted-data write does, so it never flushes the run's tracked graph.
- `RecordedAt` parameters follow `UpsertServiceHeartbeatAsync`'s typing (the `EnableLegacyTimestampBehavior` caveat in `src/CLAUDE.md`).
- Reads (`GetPartitionWatermarksAsync(connectedSystemId)`) happen once per run and once per Activity page render: EF, `AsNoTracking`.

### Connector contract

New `src/JIM.Models/Interfaces/IConnectorPartitionWatermarks.cs`:

```csharp
public interface IConnectorPartitionWatermarks
{
    /// Every Partition the run targets, by id, with the watermark JIM recorded for it, or null where none is recorded.
    void SetPartitionWatermarks(IReadOnlyDictionary<int, string?> watermarks);

    /// Describes one recorded Partition watermark for the Activity page, API and PowerShell. Never throws.
    ImportWatermarkDescription? DescribePartitionWatermark(string? value, ILogger logger);
}
```

New property on `ConnectedSystemImportResult` (`src/JIM.Models/Staging/ConnectedSystemImportResult.cs`):

```csharp
/// The watermark this run reached for each Partition it read, by Partition id: a value to record, or null to clear
/// that Partition's watermark (so its next Delta Import reads everything). A Partition left out keeps what it has.
public Dictionary<int, string?>? PartitionWatermarks { get; set; }
```

- Injected by the processor in the call-based branch beside the certificate provider, credential protection and SCIM store (`SyncImportTaskProcessor.cs` lines 419 to 445), before `OpenImportConnection`. The map is built once at run start and never changes, exactly as `originalPersistedData` is replayed to every page (line 491), so an RFC 2696 paging cookie built from it on page 1 stays valid on page 2.
- Taken from the first page that returns one, matching how `PersistedConnectorData` is captured today (line 557).
- `DescribePartitionWatermark` replaces the LDAP Connector's `IConnectorImportWatermarkDescription.DescribeImportWatermark` (`LdapConnector.cs:506`), which goes: the LDAP Connector's Connected System-level data no longer holds a watermark. The interface stays for SQL, SCIM 2.0 Client and SCIM 2.0 Service Provider.
- `CompleteRead`'s remark naming #2715 is rewritten: it means every Partition the run targets, and that is now the whole answer.

### Import processor

All in `src/JIM.Worker/Processors/SyncImportTaskProcessor.cs`.

1. **Run start.** When the Connector implements `IConnectorPartitionWatermarks`: load the rows (`ISyncServer.GetConnectedSystemPartitionWatermarksAsync`), build `{ partitionId: value-or-null }` over `_connectedSystem.GetTargetPartitions(_connectedSystemRunProfile)`, inject it, and record it on the Activity as `ImportPartitionWatermarksFrom` (Partition id, the Partition's name at run time, value).
2. **Each page.** On the page that first carries `PartitionWatermarks`, validate before a single object is processed, failing the run with `InvalidDataException` ("This is a fault in the Connector", the wording of `RecordWhetherTheConnectorReadEveryObject`, line 4918) when:
   - a key is not one of the run's target Partitions;
   - the Connector does not implement `IConnectorPartitionWatermarks` (a Connector cannot return what JIM never handed it).
3. **Deciding what to record**, at today's point (line 1106): nothing when the run failed or was cancelled (it never gets there), when Connector-reported deletes were withheld (#2089, line 716), when a Delta Import that read every object did not finish deletion detection (#2720, line 765), or when `CloseImportConnection` returned an override (line 626). Otherwise: upsert every non-null value, delete the row of every null value, sweep the rows of deselected Partitions, and write `PersistedConnectorData`, in one `RecordImportWatermarksAsync` call.
4. **Activity.** `ImportPartitionWatermarksTo` is the values recorded (for every target Partition the map named), null when nothing was recorded, exactly as `ImportWatermarkTo` is (line 1117).
5. **Deletion detection** is unchanged: scoped to `_connectedSystemRunProfile.Partition?.Id` (line 734), run when the run read every object in its scope. A whole-run fallback reads every target Partition, so the existing scope is exactly right.
6. **Logging.** One summary line at the end naming each target Partition and whether its watermark was recorded, kept, cleared or swept (Synchronisation Integrity: log summary statistics), values through `LogSanitiser.Sanitise`.

The line numbers in the approved note ("upsert target rows at ~1063", "injection ~394-420") are a little stale; the points above are where the code is today.

### LDAP Connector

#### Shared changes

- **`LdapPartitionWatermark`** (new, `src/JIM.Connectors/LDAP/LdapPartitionWatermark.cs`), serialised as the row's `Value`:

  | Field | Source | Purpose |
  |-------|--------|---------|
  | `Kind` | all | `LdapDeltaSourceKind`; a watermark of another kind (the Connected System now reaches a different directory type) counts as none |
  | `Usn`, `InvocationId`, `DnsHostName` | USN | The position, and the domain controller that issued it, because USNs mean something only against that controller (#230) |
  | `ChangeNumber`, `ChangeTime` | changelog | Position, and the restore check (#2008) |
  | `AccesslogTimestamp`, `AccesslogTimestampIsLogged` | accesslog | Position, and whether it names a logged entry the continuity check can read back |

- **`LdapConnectorRootDse` loses** `HighestCommittedUsn`, `LastChangeNumber`, `LastChangeTime`, `LastAccesslogTimestamp` and `LastAccesslogTimestampIsLogged`. It **keeps** the directory's facts: `DirectoryType`, `VendorName`, `VendorVersion`, `DnsHostName`, `InvocationId`, `PinnedDirectoryServer`, `NamingContexts`, `ConfigContext`, `SupportedControls`, `DefaultNamingContext`, `ChangelogDn`, `FirstChangeNumber`, `AdvertisedLastChangeNumber`. `InvocationId` and `DnsHostName` must stay: `GetDetectedCapabilities` shows them (`LdapConnector.cs:444`), `ResolveEffectiveServer` and the pin need the record, and Scenario 025 reads the "Invocation Id" capability. `PersistedConnectorData` therefore stays, holding the record without a watermark; `System.Text.Json` ignores the legacy fields on read, and the next write drops them.
- **`ILdapDeltaSource`** (`src/JIM.Connectors/LDAP/ILdapDeltaSource.cs`) becomes per-Partition:
  - `CaptureWatermarksAsync(rootDseEntry, rootDse, previous: IReadOnlyDictionary<int, LdapPartitionWatermark?>, targetPartitions, timeout)` returns `IReadOnlyDictionary<int, LdapPartitionWatermark?>`, null meaning "none could be taken" (today's `LeaveWatermarkEmpty`).
  - `VerifyContinuity(partition, previous: LdapPartitionWatermark, current: LdapConnectorRootDse)`, called per Partition; a refusal names the Partition and keeps today's wording and remedy.
  - `HasBaseline(LdapPartitionWatermark?)`.
  - `ReadChangesAsync(context, ...)`, with `LdapDeltaReadContext` carrying the per-Partition watermarks in place of `PreviousRootDse`'s positions.
- **`LdapConnector`** implements `IConnectorPartitionWatermarks`, holds the injected map and passes it into each `LdapConnectorImport` (constructed per page, `LdapConnector.cs:991`). A map never set reads as "no watermarks".
- **`LdapConnectorImport.GetDeltaImportObjectsAsync`** (line 293):
  - The source is still chosen from the start-of-run Connected System record, because every page is handed that same record; choosing from the freshly read rootDSE would let page 1 and page 2 choose differently. Whether to fall back is likewise a pure function of start-of-run state (injected map, target Partitions, source kind), so every page of a fallback takes the full-read path again.
  - **Fallback:** when any target Partition has no usable watermark, or there is no readable Connected System record at all, read every object in the run's Partitions (`CompleteRead = true`, #2720) and warn with `DeltaImportFallbackToFullImport`, naming the Partitions that had none. Continuity checks are skipped on that path (a full read needs none); the readiness check still runs first, as today. A Connected System with no record today throws "Run a full import first" (line 306); it now falls back like any other Partition without a baseline (Decision 2).
  - Otherwise, verify continuity per Partition, then read changes.
- **`GetFullImportObjectsAsync`** (line 160) captures and returns watermarks for its target Partitions only.
- **DN to Partition mapping**: a helper in `LdapConnectorUtilities` that maps a DN to the target Partition with the longest matching suffix, on RDN boundaries through `LdapDistinguishedName` rather than the raw `EndsWith` the accesslog read uses today (`LdapAccesslogDeltaSource.cs:691`). A mis-mapping now attributes a change to the wrong watermark, so the boundary matters more than it did. OpenLDAP and 389 Directory Server can host one suffix beneath another (subordinate databases), which is why it is the longest match.

#### Active Directory and Samba AD (USN)

- **Read** (`LdapUsnDeltaSource.ReadChangesAsync`, line 153): the existing per-Partition loop searches changed objects with `uSNChanged>=W_p+1` and tombstones after `W_p`, with each Partition's own `W_p`.
- **Capture**: `HighestCommittedUSN`, the controller's `invocationId` and `dnsHostName`, read once from the rootDSE before any search, assigned to every target Partition. Safe: every target Partition's read starts after that point is taken.
- **Continuity**, per Partition: `VerifyDomainControllerIdentity` (`LdapConnectorUtilities.cs:666`) compares the Partition watermark's `InvocationId`, falling back to `DnsHostName`, with the current rootDSE's; then the USN must not have gone below `W_p`. This also fixes a latent cross-domain-controller hazard: today Partition B's run could record controller 2's USN and Partition A's next run would read from it without any identity mismatch.
- `VerifyPartitionsAreHostedByConnectedServer` is unchanged.

#### OpenLDAP (accesslog)

- **Read** (`LdapAccesslogDeltaSource.ReadChangesAsync`, line 605): one paged search whose filter ORs a clause per target Partition:

  ```
  (&(objectClass=auditWriteObject)(reqResult=0)
    (|(&(reqDN:dnSubtreeMatch:=<A>)(reqStart>=<W_A>))
      (&(reqDN:dnSubtreeMatch:=<B>)(reqStart>=<W_B>))))
  ```

  Built by extending `WriteOperations` (line 333). In code, each entry is mapped to its Partition by longest suffix (an entry in a nested suffix matches both clauses), dropped if its `reqStart` is below its own Partition's watermark, and dropped if equal to it (today's exact-match skip, line 706, made per Partition).
- **Capture, once per run** (replacing `QueryAccesslogForLatestTimestamp`'s single-previous walk):
  1. Server-side sort available: the latest write in the log, for every target Partition (as today).
  2. Any target Partition without a watermark: walk the whole log (as a first import) and assign the latest write to every target Partition.
  3. Otherwise walk the writes within the run's Partitions from the **oldest** target watermark, `W_min`; call the latest `T_in`. Each Partition with `T_in > W_p` takes `T_in`.
  4. The rest (nothing newer than their own watermark within the run's Partitions) take the latest write anywhere from the oldest of their watermarks, so a quiet Partition keeps advancing with the log rather than waiting for the purge to remove its entry (#2712's reason for the second walk). Nothing logged from there (purge or restore): walk the whole log; the continuity check then refuses, as today.

  At most the walks today's code makes. Each value is a logged write's `reqStart` (or the generated timestamp of an empty log), never later than the end of the log when the change read starts, so assigning another Partition's write to a Partition costs at most a re-read.
- **Continuity**: `IsStillLogged` (`VerifyContinuity`, line 388) for each distinct logged `W_p`; a refusal names the Partitions whose entry has gone.
- **Readiness** is unchanged: one accesslog for every Partition.

#### 389 Directory Server and generic directories (changelog)

- **Read** (`LdapChangelogDeltaSource.ReadChangesAsync`): search from the **oldest** target watermark plus one, as today's cursor; the change number pagination token is unchanged. Drop an entry whose subject DN (the new DN on a rename, as today) maps to a Partition whose watermark is at or above its change number, and an entry outside every target Partition (today `IsDnInScope` passes everything when no Container states an opinion, line 820).
- **Capture**: the advertised `lastChangeNumber`, or the highest enumerated, and its `changeTime`, once per run, for every target Partition. This source does not narrow by the previous watermark, so "capture from the oldest W" has no work to do here.
- **Continuity** per distinct `W_p`: trimmed past it, gone backwards behind it, its own change no longer held or now carrying another `changeTime` (#2004, #2008).

### Existing data

After the migration no Partition has a row, so the first Delta Import of every LDAP Run Profile reads every object in its Partitions, detects deletions by absence within its deletion limits (#2720), records a watermark per Partition, and completes as **Complete with warning** naming the Partitions. Its next run reads changes.

Seeding rows from the old single watermark would be wrong, not just unnecessary: that value is exactly the state #2715 corrupts, and a Partition seeded from it would keep the changes it has already skipped. The one-off full read is what repairs them. JIM is pre-release, so there is no compatibility path to keep: the legacy fields simply stop being read. The warning text describes the situation, not the upgrade ("JIM holds no Delta Import watermark for the partition X"), so it stays true after this release; the changelog announces the one-off.

### Connectors without Partitions

Checked in the code: each declares `SupportsPartitions => false`, so `GetTargetPartitions` returns nothing for them and the processor injects nothing.

| Connector | Watermark today | Under this change |
|-----------|-----------------|-------------------|
| SQL Connector | Per Object Type, in `PersistedConnectorData` (`SqlConnectorImport.cs:147`, `SqlConnectorWatermark`); `CompleteRead = true` on its full read (line 129) | Unchanged |
| SCIM 2.0 Client Connector | Last-modified time in `PersistedConnectorData` (`ScimConnectorImport.cs:144`); `CompleteRead = _importPlan.ReadsEveryResource` (`ScimConnector.cs:381`) | Unchanged |
| SCIM 2.0 Service Provider Connector | Change sequence (`ScimServiceProviderConnector.cs:201`), also read by `ScimServer` and the journal range (`ScimStoreRepository.cs:293`) | Unchanged |
| File Connector | None; Full Import only (`SupportsDeltaImport => false`) | Unchanged |
| Mock Call Connector | Test fixture | Gains optional per-Partition behaviour for workflow tests only |

A future Connector with Partitions implements `IConnectorPartitionWatermarks`; until then nothing else needs to know the capability exists.

### Activity, portal, REST API and PowerShell

The Activity already shows a read range per scope: `ImportWatermarkPoint.Scope` (`src/JIM.Models/Staging/ImportWatermarkPoint.cs`) and `ImportReadRangeField.razor` render one line per scope, including "From the start, up to ..." for a scope with no starting point. Partitions become scopes.

| Surface | Change |
|---------|--------|
| Model | `Activity.ImportPartitionWatermarksFrom` / `To`: JSON arrays of `{ partitionId, partitionName, value }`. The name is captured at run time so the record reads correctly after a rename or deletion. JSON on the Activity rather than a child table: Activities are append-only audit records, retained after their Connected System is deleted, and need no foreign key to a Partition that may go |
| Server | `ConnectedSystemServer.GetImportReadRangeAsync` (line 4038) describes each Partition value with `DescribePartitionWatermark` and merges the points into one `From` and one `To`, each point's `Scope` set to the Partition's recorded name. The Connected System-level description stays for the other Connectors |
| Portal | `ImportReadRangeField`: the reading logic needs no change; **Show Connector state** also lists each Partition's recorded value before and after the run. A small UI change, so an artefact accompanies it per root `CLAUDE.md` |
| REST API | `ActivityDto` gains `importPartitionWatermarksFrom` / `importPartitionWatermarksTo` (arrays of `{ partitionId, partitionName, value }`); `importReadRange` points carry the Partition as `scope`. Summaries written for the reader (published) |
| PowerShell | `Get-JIMActivity` passes the DTO through; regenerate `docs/powershell/activities.md` with `scripts/Update-PowerShellReference.ps1` |

Write parity does not arise: watermarks are written only by imports. Clearing, deselection and deletion clear rows on every surface because they happen in the server and repository layers every surface calls.

A **current** watermark per Partition (Partitions tab, partition DTO, `Get-JIMConnectedSystemPartition`) is new read capability, not parity with something that exists; see Decision 3.

## Implementation Phases

Red first in every phase: each test below is written, run, and seen to fail for the stated reason before the code it covers. Targeted builds and tests per phase (`dotnet build test/JIM.Worker.Tests/`, `dotnet test test/JIM.Worker.Tests/`); the whole solution before the pull request.

### Phase 1: Table, migration and repository ✅

- Entity, `JimDbContext` configuration, migration, `PartitionWatermarkBulkColumns`, `IConnectedSystemRepository` methods (`GetPartitionWatermarksAsync`, `RecordImportWatermarksAsync`, `DeleteWatermarksOfDeselectedPartitionsAsync`), the tracked-fallback delete, the clear, the deselection hook in `ConnectedSystemServer`, `ISyncServer` pass-throughs.
- Tests:
  - `test/JIM.Worker.Tests/Repositories/ConnectedSystemPartitionWatermarkDatabaseTests.cs` (`RequiresPostgres`, pattern `ServiceHeartbeatRepositoryDatabaseTests` and `ConnectedSystemPersistedConnectorDataUpdateDatabaseTests`):
    - `RecordImportWatermarksAsync_NewAndExistingPartitions_UpsertsEveryFieldAndReadsThemBackAsync` (the round trip: every column asserted)
    - `RecordImportWatermarksAsync_NullValue_DeletesThatPartitionsRowAsync`
    - `RecordImportWatermarksAsync_PartitionRemovedDuringTheRun_SkipsItWithoutFailingAsync`
    - `RecordImportWatermarksAsync_PartitionOfAnotherConnectedSystem_WritesNothingForItAsync`
    - `RecordImportWatermarksAsync_ContextTracksTheRunsGraph_WritesOnlyTheWatermarksAsync`
    - `RecordImportWatermarksAsync_WritesPersistedConnectorDataInTheSameTransactionAsync`
    - `RecordImportWatermarksAsync_TwoWritersForOnePartition_LastWriterWinsWithoutErrorAsync`
    - `DeleteWatermarksOfDeselectedPartitionsAsync_DeselectedPartition_RemovesOnlyItsRowAsync`
  - `BulkInsertColumnCompletenessTests`: `PartitionWatermarkBulkColumns_MatchMappedColumnsExactly`, `PartitionWatermarkUpdateColumns_HaveAConsciousHomeForEveryColumn`.
  - `ConnectedSystemDeletionDatabaseTests`: `DeleteConnectedSystemAsync_PartitionsHoldWatermarks_DeletesThemWithTheSystemAsync`.
  - `ConnectedSystemHierarchyPersistenceDatabaseTests`: `RefreshHierarchy_PartitionNoLongerInTheDirectory_RemovesItsWatermarkAsync`.
  - `SystemResetDatabaseTests`: `ResetSystemAsync_PartitionWatermarksExist_RemovesThemAsync`.
  - Clear: `ClearConnectedSystemObjectsAsync_PartitionsHoldWatermarks_RemovesThemAsync` (`RequiresPostgres`, beside the existing clear coverage).
  - Deselection: `UpdateConnectedSystemPartitionAsync_PartitionDeselected_RemovesItsWatermarkAsync` and `UpdateConnectedSystemAsync_PartitionDeselected_RemovesItsWatermarkAsync` (`RequiresPostgres`, `NoTracking` context to match JIM.Web).
  - `ConfigurationChangeCaptureCoverageTests`: `RecordImportWatermarksAsync_DoesNotCreateActivityOrCaptureSnapshotAsync` (sibling of the existing persisted-data test, line 206).
  - `DeletePathForeignKeyCoverageTests`, `MigrationDesignerChainTests`, `ReleasedMigrationImmutabilityTests`: run, with the `RemovedTables` entry added.

### Phase 2: Contract and processor ✅

- `IConnectorPartitionWatermarks`, `ConnectedSystemImportResult.PartitionWatermarks`, the Activity fields, processor steps 1 to 6 above.
- Test connector: extend `test/JIM.Worker.Tests/Workflows/PagedImportConnector.cs` (or a sibling `PartitionWatermarkTestConnector`) to accept injected watermarks, return per-Partition values, and record what it was handed.
- `test/JIM.Worker.Tests/Workflows/ImportPartitionWatermarkTests.cs`:
  - `DeltaImport_TargetsOnePartition_HandsTheConnectorOnlyThatPartitionsWatermarkAsync`
  - `DeltaImport_PartitionWithNoRecordedWatermark_HandsTheConnectorNullForItAsync`
  - `DeltaImport_OfOnePartition_LeavesTheOtherPartitionsWatermarkUnchangedAsync` (#2715 at the processor)
  - `FullImport_TargetsOnePartition_LeavesTheOtherPartitionsWatermarkUnchangedAsync` (the scoped Full Import half of #2715)
  - `Import_CompletesNormally_RecordsTheWatermarkOfEveryTargetedPartitionAsync`
  - `DeltaImport_ConnectorReturnsNullForAPartition_RemovesThatPartitionsWatermarkAsync`
  - `DeltaImport_ConnectorReturnsAWatermarkForAPartitionTheRunDidNotTarget_FailsTheRunAndRecordsNothingAsync`
  - `Import_ConnectorWithoutTheCapabilityReturnsPartitionWatermarks_FailsTheRunAsync`
  - `Import_StagingFailsAfterEveryPageIsRead_LeavesEveryPartitionWatermarkUnchangedAsync`
  - `Import_Cancelled_LeavesEveryPartitionWatermarkUnchangedAsync`
  - `DeltaImport_ReportedDeletesWithheld_LeavesEveryPartitionWatermarkUnchangedAsync`
  - `DeltaImport_ReadEveryObjectButDeletionDetectionIncomplete_LeavesEveryPartitionWatermarkUnchangedAsync`
  - `DeltaImport_CloseImportConnectionReturnsData_RecordsNoPartitionWatermarkAsync`
  - `Import_CompletesNormally_SweepsTheWatermarksOfDeselectedPartitionsAsync`
  - `Import_CompletesNormally_RecordsEachPartitionsWatermarksOnTheActivityAsync`
  - `Import_StagingFails_RecordsWhereEachPartitionStartedButNoWatermarkReachedAsync`
  - `DeltaImport_LaterPagesAreHandedTheSameWatermarksAsTheFirstAsync`
- Existing `ImportPersistedConnectorDataTests` and `DeltaImportCompleteReadTests` stay green unchanged: that is the proof the Connected System-level path and #2720 are untouched.
- `test/JIM.Worker.Tests/Servers/ConnectedSystemImportReadRangeTests.cs`: `GetImportReadRangeAsync_PartitionWatermarks_DescribesOnePointPerPartitionScopedByItsNameAsync`, `GetImportReadRangeAsync_PartitionDeletedSinceTheRun_ScopesByTheRecordedNameAsync`.

### Phase 3: LDAP model, contract and the USN source ✅

- `LdapPartitionWatermark`, the `LdapConnectorRootDse` field removal, the `ILdapDeltaSource` and `LdapDeltaReadContext` changes, `LdapConnector` implementing the contract, the fallback and source-selection rules in `LdapConnectorImport`, the DN-to-Partition helper, the USN source.
- Tests:
  - `test/JIM.Worker.Tests/Connectors/LdapPartitionWatermarkTests.cs`: serialisation round trip per kind; `DescribePartitionWatermark_*` cases moved from `ConnectorImportWatermarkDescriptionTests` (null, empty, unreadable, each kind).
  - `LdapUsnDeltaSourceTests`:
    - `ReadChangesAsync_TwoPartitionsWithDifferentWatermarks_SearchesEachFromItsOwnUsnAsync`
    - `ReadChangesAsync_TwoPartitions_SearchesEachPartitionsTombstonesFromItsOwnUsnAsync`
    - `CaptureWatermarksAsync_RecordsTheIssuingControllersInvocationIdAndHostNameForEveryTargetPartitionAsync`
    - `VerifyContinuity_PartitionWatermarkFromAnotherDomainController_ThrowsNamingThePartitionAsync`
    - `VerifyContinuity_UsnBelowThePartitionsWatermark_ThrowsAsync`
  - `LdapConnectorImportDomainControllerIdentityTests`: existing cases re-expressed against a Partition watermark; the message must still contain "invocationId has changed", both ids and "Full Import" (Scenario 025 asserts on all three).
  - `LdapConnectorUtilitiesTests`: `PartitionForDn_NestedSuffixes_ChoosesTheLongestMatch`, `PartitionForDn_SuffixWithoutAnRdnBoundary_DoesNotMatch`.
  - `test/JIM.Worker.Tests/Connectors/LdapConnectorImportPartitionWatermarkTests.cs` (new, modelled on `LdapConnectorImportAccesslogWatermarkTests`: a scripted directory driving the whole import), the shared cases:
    - `GetDeltaImportObjectsAsync_APartitionWithoutAWatermark_ReadsEveryObjectInTheRunsPartitionsAndNamesItAsync`
    - `GetDeltaImportObjectsAsync_NoConnectedSystemRecord_ReadsEveryObjectInsteadOfFailingAsync`
    - `GetDeltaImportObjectsAsync_WatermarkOfAnotherKind_ReadsEveryObjectAsync`
    - `GetDeltaImportObjectsAsync_FallbackOnALaterPage_StaysAFullReadAsync`
    - `GetFullImportObjectsAsync_ScopedToOnePartition_ReturnsAWatermarkForThatPartitionOnlyAsync`

- **Interim left for Phases 4 and 5.** The contract change forced both sources onto the per-Partition signatures, so each now does the simplest thing that cannot skip a change: `CaptureWatermarksAsync` takes one watermark exactly as the single Connected System-level one was taken (the accesslog walk starts from the **oldest** target Partition's watermark, or the whole log when any target Partition has none; the changelog capture is unchanged) and gives that one value to every target Partition; `ReadChangesAsync` reads every target Partition from the **oldest** target watermark (`LdapDeltaReadContext.OldestWatermark`), keeping today's exact-match skip and today's raw `EndsWith` partition check in the accesslog read; `VerifyContinuity` checks each distinct watermark against today's rules, but its refusal text does not yet name the Partitions (the USN source's does). This re-reads what a Partition with a later watermark already read and misses nothing. The single-value captures survive as internal `CaptureWatermarkAsync` methods so the existing #2004, #2008, #2704 and #2712 tests still drive them unchanged. Phases 4 and 5 replace all of this with the per-Partition rules above; `LdapConnectorUtilities.PartitionForDn` is ready for them.

### Phase 4: The accesslog source ✅

- Filter, per-Partition skip, the capture rule, per-Partition continuity.
- `LdapConnectorImportPartitionWatermarkTests` (accesslog cases):
  - `GetDeltaImportObjectsAsync_TwoPartitionsWithDifferentWatermarks_ReadsEachFromItsOwnAsync` (asserts the change read's filter)
  - `GetDeltaImportObjectsAsync_WriteBeforeOrAtItsPartitionsWatermark_IsNotImportedAsync`
  - `GetDeltaImportObjectsAsync_NestedSuffixes_JudgesAWriteByTheLongestMatchingPartitionAsync`
  - `GetDeltaImportObjectsAsync_DeltaOfOnePartitionThenDeltaOfAnother_TheSecondImportsTheChangeTheFirstPassedAsync` (the timeline above, end to end: the bug's reproduction at the Connector)
  - `GetFullImportObjectsAsync_ScopedFullImportThenDeltaOfAnotherPartition_TheDeltaImportsTheChangeTheFullImportPassedAsync`
  - `GetDeltaImportObjectsAsync_Capture_WalksFromTheOldestTargetWatermarkAsync`
  - `GetDeltaImportObjectsAsync_NothingNewWithinAQuietPartition_AdvancesItWithTheLogAsync`
  - `GetDeltaImportObjectsAsync_OnePartitionsWatermarkPurged_RefusesNamingThatPartitionAsync`
  - `GetDeltaImportObjectsAsync_LaterPage_SendsTheSameFilterSoThePagingCookieStaysValidAsync`
- `LdapConnectorImportAccesslogWatermarkTests` and `LdapAccesslogDeltaSourceTests` re-expressed in per-Partition terms; every #2704 and #2712 behaviour they pin must survive.
- **As built.** This replaced the accesslog half of the Phase 3 interim: the single-value `CaptureWatermarkAsync` is gone, and its tests drive `CaptureWatermarksAsync`, the capture's unconfined twins (no Partition named) merged into their within-Partition ones, since every capture now names its Partitions. Two end-to-end tests (`DeltaOfOnePartitionThenDeltaOfAnother`, `ScopedFullImportThenDeltaOfAnotherPartition`) already passed against the interim, because Phases 2 and 3 confine each run's watermarks to its own Partitions; they were shown red against a reintroduction of the single-watermark shape (a run returning its watermark for every Partition of the Connected System). Added beyond the list: `GetDeltaImportObjectsAsync_TwoPartitionsWatermarksPurged_RefusesOnceNamingBothAsync` and its USN twin, and source-level cases for the per-Partition skip, the RDN boundary, a quiet Partition, several quiet Partitions sharing one walk, and a Partition with no DN.

### Phase 5: The changelog source

- `LdapChangelogDeltaSourceTests`:
  - `ReadChangesAsync_TwoPartitions_ReadsFromTheOldestWatermarkAsync`
  - `ReadChangesAsync_EntryAtOrBelowItsPartitionsWatermark_IsNotImportedAsync`
  - `ReadChangesAsync_EntryOutsideEveryTargetPartition_IsSkippedAsync`
  - `ReadChangesAsync_RenameIntoAPartition_IsJudgedByTheNewDnsPartitionAsync`
  - `CaptureWatermarksAsync_AssignsTheAdvertisedLastChangeNumberToEveryTargetPartitionAsync`
  - `VerifyContinuity_ChangelogTrimmedPastOnePartitionsWatermark_ThrowsNamingThatPartitionAsync`
  - `VerifyContinuity_OnePartitionsWatermarkChangeNowHasAnotherChangeTime_ThrowsAsync`
- `LdapConnectorImportPartitionWatermarkTests`: `GetDeltaImportObjectsAsync_ChangelogDeltaOfOnePartitionThenAnother_TheSecondImportsTheChangeTheFirstPassedAsync`.
- `DirectoryServer389LdapTests` re-expressed where they read `LastChangeNumber`.

### Phase 6: Activity, portal, REST API and PowerShell

- `ActivityDto` fields (the `GetImportReadRangeAsync` merge landed in Phase 2), `ImportReadRangeField` state toggle, PowerShell reference regeneration.
- Tests: `ActivitiesControllerTests.GetActivityAsync_ImportWithPartitionWatermarks_ReturnsEachPartitionsRecordedValuesAsync`; `test/JIM.Web.Tests/ImportReadRangeFieldTests.cs`: `ImportReadRangeField_PartitionWatermarks_ShowsOneLinePerPartitionAsync` and `ImportReadRangeField_PartitionWithNoStartingWatermark_ReadsFromTheStartAsync` (behaviour, not wording).
- Run `scripts/Generate-OpenApiDoc.ps1 -NoBuild` (new DTO members) and boot the stack once to confirm the Activity page.
- Decision 3: the Partitions tab, partition DTO and `Get-JIMConnectedSystemPartition` gain the current watermark, with an artefact for the UI and Pester tests for the cmdlet's output shape.
- UI shape, approved on the mockups ([artefact](https://claude.ai/artifact/MLS2kV3NzkoPm9x9mtATu6)) on 3 November 2026:
  - **Activity, Show Connector state:** a table, not a pair of value boxes (boxes clipped long values with no way to read the rest). Columns: Partition (its recorded name), Before this run, After this run. Values wrap in full. After this run reads "Unchanged" when the run recorded the same value or left the Partition out, and says nothing was recorded when the run kept every watermark (`ImportPartitionWatermarksTo` null). The table scrolls sideways on a narrow screen rather than clipping.
  - **Partitions tab:** one line under each managed Partition's name: "Next Delta Import reads changes after <described position> · Recorded <relative time>", or, with no row, "No watermark recorded. The next Delta Import reads every object in this partition, as a Full Import does, and records one." A deselected Partition shows nothing.

### Phase 7: Integration scenarios

See [Integration testing](#integration-testing).

### Phase 8: Documentation, changelog, plan lifecycle

See [Documentation and changelog](#documentation-and-changelog). Move this plan to `engineering/plans/done/` in the pull request that closes #2715.

## Integration Testing

### Scenario 009 (PartitionScopedImports): the main exercise

`test/integration/Setup-Scenario-009.ps1` creates today only Full Import (Scoped), Full Import (Scoped - Second, RFC directories), Full Import (Unscoped) and Full Synchronisation. `Invoke-Scenario-009-PartitionScopedImports.ps1` runs only Full Imports. Extend both:

- **Setup**: add **Delta Import (Scoped)**, **Delta Import (Scoped - Second)** (OpenLDAP and 389 Directory Server) and **Delta Import (Unscoped)**, each with a small page size (for example 5) so a Micro run crosses Connector page boundaries and exercises the paging cookie with the per-Partition filter.
- **New steps** (add each to the `-Step` `ValidateSet`):

| Step | Does | Asserts |
|------|------|---------|
| `DeltaBaseline` | After the existing Full Imports, run both scoped Delta Imports | Each completes with no warning (each Partition has a row from its Full Import) |
| `CrossPartitionDelta` | Change one user in each Partition; Delta Import (Scoped); then Delta Import (Scoped - Second) | The first imports only the first Partition's change; **the second imports the second Partition's change** (fails today: the #2715 regression) |
| `ScopedFullThenDelta` | Change a user in the second Partition; Full Import (Scoped); Delta Import (Scoped - Second) | The Delta Import imports the change |
| `UnscopedDelta` | Change a user in each; Delta Import (Unscoped); then both scoped Delta Imports | The unscoped run imports both; the scoped runs import nothing new; `importReadRange` has one point per Partition |
| `PartitionDeletion` | Delete a user in the second Partition; Delta Import (Scoped); Delta Import (Scoped - Second) | Only the second run marks it deleted (accesslog `reqEntryUUID`, changelog deleted-entry copy) |
| `ReselectedPartition` | Deselect then reselect the second Partition (no run between); Delta Import (Unscoped) | `CompleteWithWarning`, `DeltaImportFallbackToFullImport`, warning names the second Partition, no deletions; a following Delta Import (Scoped - Second) has no warning |

- **Samba AD** (one domain Partition): run `DeltaBaseline`, `CrossPartitionDelta` (both Run Profiles target the same Partition, so they share one row) and `UnscopedDelta`, proving the USN path end to end.
- **Run** on OpenLDAP and 389 Directory Server, plus Samba AD. Iterate at Micro; sign off at Medium at least (the single-page rule in `test/CLAUDE.md`), Large on OpenLDAP and 389 Directory Server if time allows, since that is their Pre-Release template.

### Every other scenario that observes the watermark or the fallback

Searched `test/integration` for `DeltaImportFallbackToFullImport`, `watermark`, `PersistedConnectorData`, `importWatermark`, `importReadRange`, `accesslog` and `-PartitionId`.

| Scenario | What it observes | Needs |
|----------|------------------|-------|
| 008 CrossDomainEntitlementSync | Allows `DeltaImportFallbackToFullImport` on RFC directories (`Invoke-Scenario-008-...ps1` lines 527 to 562, 1009, 1820, 1941); has Full Import (Scoped) Run Profiles (`Setup-Scenario-008.ps1` lines 1193, 1224), but each system selects one Partition only (line 544, others deselected at 586), so scoped and unscoped runs share one row | No change. Run at Medium on OpenLDAP to confirm no new warning. The allowance comments stay accurate |
| 016 SqlConnectorMatrix | Expects the fallback warning after SQL mode changes (`utils/Scenario-016-Helpers.ps1` line 387) | No change (SQL keeps its Connected System-level watermark). Run once to confirm the non-LDAP path |
| 021 RunProfileSafeguards | A second withheld Delta Import withholds the same deletes, proving the watermark did not advance (lines 553 to 612); baseline Delta Import asserted with no warning (line 567) | No change: withheld deletes record no Partition row; the baseline Delta Import follows an unscoped Full Import of the same Partitions. Run |
| 001 HRToIdentityDirectory | Full Import establishes the baseline for Delta Imports (comment line 599); 20 Delta Import runs, unscoped, one Partition | No change. Run |
| 025 ActiveDirectoryDeltaImportIntegrity (AD lab only) | Error text: "invocationId has changed", both ids, "Full Import" (lines 549 to 559); the "Invocation Id" capability before and after (line 304) | Keep all three substrings in the per-Partition refusal and keep `InvocationId` on the rootDSE record. Cannot run before merge: dispatch `gh workflow run ad-lab.yml -f scenario=25` after merging and report on the pull request |
| 015 ScimConnector | A deliberately stale SCIM watermark (line 447) | No change |
| 002, 014, 022, 032 | Mention watermarks or the accesslog in comments only, or run no Delta Import | None |

The new message texts are read by no scenario besides 009 and 025. Record the search, the changed scenarios (009), the runs and the AD-lab dispatch (025) in the pull request's Testing section.

## Documentation and Changelog

Customer docs write **partition** in lower case (`docs/.conformance/vocabulary.psd1`, `TitleCase = $false`), unlike this plan; keep that in every page below. Rewrite what the change makes wrong; never append history (MNT-1, MNT-2).

| Page | Change |
|------|--------|
| `docs/configuration/run-profiles.md`, "Partition and file path" (lines 50 to 54) | Each partition keeps its own Delta Import watermark; a Run Profile reading one partition moves only that partition's; a Delta Import whose partitions include one with no watermark reads every object in its partitions once, says so, and detects deletions as a Full Import does |
| `docs/configuration/import-watermarks.md` | Activity table: one line per partition for the LDAP Connector; By Connector table: LDAP rows "for each partition"; **Show Connector state**: the per-partition values; REST and PowerShell paragraph: `importPartitionWatermarksFrom` / `To`, and `scope` naming the partition |
| `docs/connectors/jim-ldap-connector-delta-import.md` | Intro and diagram `desc` (lines 10 to 34): watermark per partition; accesslog paragraph (line 45): the read is per partition from its own watermark, and the purge age is measured against each partition's Delta Imports; changelog paragraph (line 49): read from the oldest of the run's partitions, trimmed-past checked per partition; fallback bullet (line 60): names the partitions; a first Delta Import with no record reads everything |
| `docs/connectors/jim-ldap-connector-permissions.md` (line 121) | Keep `olcAccessLogPurge` longer than twice the longest gap between Delta Imports **of any one partition**: a partition read only by a weekly partition-targeted Run Profile needs more than two weeks |
| `docs/connectors/jim-ldap-connector-troubleshooting.md` (lines 97 to 108) | The refusal names the partition; the remedy is a Full Import that reads it (scoped to it or unscoped) |
| `docs/configuration/partitions-and-containers.md` | Deselecting a partition clears its watermark, so after reselecting it the next Delta Import reads it in full |
| `docs/configuration/connector-space.md`, "Clearing the Connector Space" | Decision 4: clearing also clears each partition's watermark |
| `docs/powershell/activities.md` | Regenerated by `scripts/Update-PowerShellReference.ps1` |
| `engineering/developer/connectors.md` ("Describing your import watermark", line 68) | A section on keeping a watermark per Partition: the interface, the tri-state result, first-page capture, the rules the processor enforces |

**Changelog** (`## [Unreleased]`):

- **Fixed**: "🐛 A Delta Import that reads one LDAP partition no longer moves the starting point of another, so partition-targeted Delta Imports stop skipping changes (#2715)."
- **Changed**: "🔄 Each LDAP partition now keeps its own Delta Import watermark; the first Delta Import of each Run Profile after upgrading reads every object in its partitions once, which also picks up any change a partition-targeted import skipped (#2715)."

The 🔄 entry needs the docs above in the same pull request (`Lint-DocsCoupling.ps1`).

## Success Criteria

- The two-Run-Profile timeline above, scripted against OpenLDAP and 389 Directory Server (Scenario 009 `CrossPartitionDelta`), imports the second Partition's change; it fails on today's code.
- A partition-scoped Full Import leaves every other Partition's row untouched (`ScopedFullThenDelta`).
- Every existing test in `ImportPersistedConnectorDataTests`, `DeltaImportCompleteReadTests`, `LdapConnectorImportAccesslogWatermarkTests` and the SQL and SCIM delta suites passes, re-expressed only where it reads a moved field.
- Scenarios 001, 008, 016 and 021 pass with no new warnings; Scenario 025 passes in the AD lab after merge.
- `dotnet build JIM.sln` and `dotnet test JIM.sln`: zero warnings, zero failures; the `RequiresPostgres` fixtures above pass.

## Dependencies

- **#2720 / PR #2721** must land first (or this work stacks on its branch): the fallback relies on `CompleteRead` and its deletion detection.
- **#917, Bounded-Memory Delta Import** (`engineering/plans/BOUNDED_MEMORY_DELTA_IMPORT.md`, Planned) keeps the watermark rule ("recorded only after the last page is staged", invariant 7), so the two plans compose; whichever lands second keeps the per-Partition write at the end-of-run point.
- No new packages.

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| The one-off full read per Delta Import Run Profile is heavy at scale (a 1M-object Partition) and lands in the first scheduled run after the upgrade | Announced in the changelog and the run's warning; deletion limits still apply; it repairs changes #2715 already skipped, which nothing else would |
| Purge age becomes a per-Partition concern: a rarely run partition-targeted Delta Import can outlive `olcAccessLogPurge` or the Retro Changelog's maximum age, and be refused | Documented per partition; the refusal names the partition and the remedy; Decision 3 makes each Partition's last recorded watermark visible |
| A whole-run fallback reads every Partition of an unscoped Run Profile when only one lacks a watermark (for example a newly selected Partition) | Correct and safe; the cost is the price of deferring per-Partition fallback (Follow-up 1) |
| Assigning a watermark taken from one Partition's write to another | Safe by construction (every Partition's read starts before the capture's end); pinned by the accesslog capture tests |
| Mapping a DN to the wrong Partition (nested suffixes, suffix without an RDN boundary) | Longest-suffix, RDN-boundary mapping through `LdapDistinguishedName`; dedicated tests |
| Upsert failing at the very end of a run (Partition removed mid-run) | Guarded insert joins `ConnectedSystemPartitions`; test |
| Two runs of one Connected System at once (parallel Schedule steps) | `ON CONFLICT` upsert; last writer wins safely; test |
| The paging cookie invalidated by a filter that differs between pages | The injected map is fixed for the run; test asserts the same filter on page 2 |
| A Connector returning watermarks for Partitions it was not given | Run fails before staging; test |
| The OR filter grows with the number of Partitions | A clause per target Partition; Connected Systems have a handful. Measure at Scenario 009's two and note it |
| Losing `InvocationId` from capabilities breaks Scenario 025 and the Connected System page | It stays on `LdapConnectorRootDse`; only the copy on each USN Partition watermark is new |

## Decisions

Decided on 3 November 2026. Questions 1 to 3 were put to the user and accepted as recommended; 4 to 7 were adopted as recommended, being implementation choices within the approved design.

1. **`CompletelyReadPartitionIds` is not added.** Scoping deletion detection to a set of Partitions relies on `ConnectedSystemObject.PartitionId`, which is null for every object created by an unscoped Run Profile (`SyncImportTaskProcessor.cs:3829` stamps the Run Profile's Partition, null when it targets none; the backfill at line 2929 also needs a scoped run) and for every object JIM provisions (`ExportEvaluationServer.cs:3041`). A Partition-set scope would then miss those objects' deletions, and widening it to include null would mark objects in other Partitions deleted. Fixing #2715 does not need it: the whole-run fallback is complete and correct. The prerequisite is #2723.
2. **A Delta Import of a Connected System with no LDAP record at all reads every object** instead of failing with "Run a full import first", as for any Partition without a watermark. It is what #2720 decided and what the SQL and SCIM Connectors already do, and a Delta Import that is the Connected System's first run then just works.
3. **Each Partition's current watermark is shown, read-only,** on the Partitions tab, the partition DTO and `Get-JIMConnectedSystemPartition`, in this pull request. The purge-age risk is now per Partition, and "where does this Partition's next Delta Import start, and when was it last recorded" is the question an administrator will ask.
4. **Clearing the Connector Space removes the Partition rows**: an empty Connector Space has no baseline. The same gap for the SQL and SCIM Connectors' Connected System-level watermark is #2724.
5. **A deselected Partition's row is removed at deselection and at the end of every import**, sharing one statement: deselection covers the failed-run window, the sweep covers any path that changes `Selected` without the server.
6. **`RecordedAt` is rewritten whenever a run records a Partition's watermark, even an unchanged value**, so it reads as "last read", which is what decision 3 shows.
7. **The Activity stores the per-Partition values as JSON columns**, not a child table (reasons under Activity, above).

## Follow-ups (not needed for #2715)

1. **#2723:** truthful `ConnectedSystemObject.PartitionId` for objects created by unscoped imports and by provisioning; after it, per-Partition fallback with `CompletelyReadPartitionIds` becomes possible. Today a partition-scoped Full Import never deletion-checks those objects, which is a latent missed-deletion gap in its own right.
2. **Not filed:** widening scope inside a Partition (a newly selected Container) could clear that Partition's row so its next Delta Import reads in full. The docs already ask for a Full Import (`docs/configuration/configuration-changes.md:83`).
3. **#2724:** clearing the Connector Space leaves the SQL and SCIM Connectors' watermark in place, so a Delta Import after a clear reads only changes.

## Departures from the approved design

| Approved note said | Plan does | Why |
|--------------------|-----------|-----|
| `CompletelyReadPartitionIds` generalises `CompleteRead`; deletion detection scoped to it | Keep `CompleteRead`; defer the set | `ConnectedSystemObject.PartitionId` is not truthful (Decision 1). Also, a set alone cannot say "read completely" for a Connected System without Partitions (SQL, SCIM, File), so `CompleteRead` would have to stay beside it anyway |
| Delete rows for deselected Partitions in the processor | Also at deselection | The processor alone misses deselect, a failed run, then reselect |
| Upsert the target rows | Guarded upsert (`ON CONFLICT`, joined to `ConnectedSystemPartitions`) | Parallel Schedule steps; Partitions removed mid-run |
| Capture once from the oldest W, for every source | Only the accesslog narrows from the oldest W, with an explicit second walk for quiet Partitions | USN and changelog captures never used the previous watermark; the accesslog needs the rule spelt out so a quiet Partition keeps advancing |
| Watermark fields move out of `LdapConnectorRootDse` | Yes, but `InvocationId` and `DnsHostName` stay there too | Capabilities display, `ResolveEffectiveServer`, and Scenario 025 read them |
| Source chosen as today | Explicitly kept on the start-of-run record | Later pages are handed the start-of-run record, so any other choice can diverge between pages |
| Validate the page that first carries `PartitionWatermarks` | Every page's map is checked before its objects are processed; only the first map is recorded | A bad key on a later page would otherwise be ignored silently, though it is the same fault in the Connector |
| Fail when a Connector without the capability returns watermarks | It must leave `PartitionWatermarks` null; any map, even an empty one, fails the run | One rule a Connector author can state, and no case where a non-null map from such a Connector is meaningful |
| Sweep deselected Partitions at the end of every completed run | Only at the end of a run that may record; a run that records nothing (withheld deletes, incomplete deletion detection, the close override) writes nothing at all | "Record nothing" stays literal; the deselection hook (Decision 5) already covers those runs |
| `VerifyContinuity(partition, previous, current)`, called per Partition | `VerifyContinuity(partitions, previous, current)`, called once for each distinct watermark with every target Partition that holds it (`LdapPartitionWatermark` is a record, so equal watermarks group) | One read per distinct watermark rather than per Partition, and a refusal names every Partition it affects; Phases 4 and 5 ask for exactly "each distinct `W_p`" |
| `LdapConnectorRootDse` loses `HighestCommittedUsn` | It does, from the persisted record; the USN this run's rootDSE read found is held on the in-memory record as `CurrentHighestCommittedUsn` (`[JsonIgnore]`) | The gone-backwards check (#1869) compares each Partition's USN with the directory's current one, which is a fact about the directory now, not a position, and is never persisted |
| No Connected System record: fall back like a Partition without a watermark | Also an unreadable record | It used to fail the run with a JSON error; an unreadable record says no more about the directory than a missing one |
| (not covered) A run whose Run Profile targets no Partition (Phase 2 hands an empty map) | Reads nothing, returns the rootDSE record and an empty `PartitionWatermarks`, `CompleteRead` false, and warns "This Delta Import read nothing: its Run Profile targets no selected partition", with or without a record | Before, the USN source read nothing silently, the accesslog and changelog sources read every change in the directory unconfined, and no record threw "Run a full import first". A full read here would read nothing while claiming a complete read, and an unconfined read is impossible without a Connected System-level watermark; the task-creation partition validation still blocks such a run by default |
| Refusals keep today's remedy | The USN refusals say "Run a Full Import that reads the partition X" | A Full Import scoped to another Partition no longer repairs this one, so the remedy names which one; "invocationId has changed", both ids and "Full Import" stay for Scenario 025 |
| (not covered) The fallback full read | Carries on from the first page's rootDSE read and capture instead of reading the directory and taking the watermarks a second time; its searches on the import's own connection go through the `ILdapOperationExecutor` seam | One read and one capture per run; the seam lets the tests show which Partitions a fallback read |
| Accesslog continuity: a refusal names the Partitions, today's wording and remedy otherwise | The refusal says "the last import of the partition X ended at", asks for "a Full Import that reads the partition X", and keeps the purge age longer than twice the longest gap between Delta Imports "of any one partition" | As for USN: a Full Import scoped to another Partition no longer repairs this one, and the purge age is now a per-Partition concern; "olcAccessLogPurge", "restored from a backup or snapshot", "silently miss" and "Run a Full Import" all stay |
| (not covered) Several distinct watermarks refused at once | The import checks every distinct watermark before refusing and refuses once: one refusal is rethrown as the source raised it, several become one whose message is each of theirs in turn | Stopping at the first had the administrator repair one Partition only to be refused for the next; it is the import's rule, so the USN source (and the changelog source) gain it without change |
| (not covered) A target Partition with no distinguished name | Its change read clause is the reqStart bound alone, it takes the writes no named Partition claims (of several such, the one with the oldest watermark judges them), and its capture walk is unconfined, which already answers a quiet Partition's question, so no second walk is made | Keeps what the read and capture did when no Partition could be named; unreachable for an LDAP naming context, but a run must not read nothing silently |
| (not covered) Fetching a changed object's current state | Goes through the `ILdapOperationExecutor` seam, as the import's other reads on its own connection do | Lets the import-level tests show which changes the change read kept |
