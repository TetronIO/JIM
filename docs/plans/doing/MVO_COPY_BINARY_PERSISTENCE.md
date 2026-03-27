# MVO COPY Binary Persistence (Phase 6 of #338)

- **Status:** Doing
- **Milestone**: v0.9-STABILISATION
- **GitHub Issue**: [#338](https://github.com/TetronIO/JIM/issues/338) (Phase 6)
- **Related**: [`docs/plans/done/WORKER_DATABASE_PERFORMANCE_OPTIMISATION.md`](../done/WORKER_DATABASE_PERFORMANCE_OPTIMISATION.md)
- **Created**: 2026-03-27

## Overview

Convert MVO (Metaverse Object) persistence from EF Core `AddRange`/`SaveChangesAsync` to COPY binary and raw SQL, mirroring the proven pattern used for CSO persistence in Phase 3/#338.

MVO persistence is the **last major hot path still using EF Core's per-row SQL generation** in the sync flush pipeline. CSO creates, RPEI inserts, and sync outcome inserts already use COPY binary via `ParallelBatchWriter`. MVO creates and updates remain on EF Core, generating N individual INSERT/UPDATE statements per entity.

## Business Value

- **Eliminates the last single-connection bottleneck** in the sync flush — MVO persistence currently dominates flush time because EF's per-row SQL is orders of magnitude slower than COPY binary
- **Consistent architecture** — all bulk write hot paths use the same COPY binary pattern
- **Expected 5–20x improvement** in MVO create throughput (based on CSO COPY binary results)

## Scope

### In Scope — MVO Creates

Convert `CreateMetaverseObjectsAsync` to use COPY binary, following the exact pattern from `SyncRepository.CsOperations.cs`:

1. Pre-generate IDs for MVOs and their attribute values
2. Route selection: small batches → single-connection parameterised INSERT; large batches → parallel COPY binary via `ParallelBatchWriter`
3. COPY binary for `MetaverseObjects` table (10 columns, excluding `xmin` which PostgreSQL assigns)
4. COPY binary for `MetaverseObjectAttributeValues` table (13 columns)

**MetaverseObjects columns** (from migration snapshot):

| Column | Type | Nullable | Notes |
|--------|------|----------|-------|
| Id | uuid | No | Pre-generated `Guid.NewGuid()` |
| Created | timestamp with time zone | No | |
| LastUpdated | timestamp with time zone | Yes | |
| TypeId | integer | No | Shadow FK to MetaverseObjectType |
| Status | integer | No | Enum `MetaverseObjectStatus` |
| Origin | integer | No | Enum `MetaverseObjectOrigin` |
| LastConnectorDisconnectedDate | timestamp with time zone | Yes | |
| DeletionInitiatedByType | integer | No | Enum `ActivityInitiatorType` |
| DeletionInitiatedById | uuid | Yes | |
| DeletionInitiatedByName | text | Yes | |

Note: `xmin` is a PostgreSQL system column (concurrency token) — it is assigned automatically by PostgreSQL on INSERT and must NOT be included in COPY statements.

**MetaverseObjectAttributeValues columns** (from migration snapshot):

| Column | Type | Nullable | Notes |
|--------|------|----------|-------|
| Id | uuid | No | Pre-generated `Guid.NewGuid()` |
| MetaverseObjectId | uuid | No | Shadow FK to MetaverseObject (parent) |
| AttributeId | integer | No | FK to MetaverseAttribute |
| StringValue | text | Yes | |
| DateTimeValue | timestamp with time zone | Yes | |
| IntValue | integer | Yes | |
| LongValue | bigint | Yes | |
| ByteValue | bytea | Yes | |
| GuidValue | uuid | Yes | |
| BoolValue | boolean | Yes | |
| ReferenceValueId | uuid | Yes | FK to MetaverseObject (reference) |
| UnresolvedReferenceValueId | uuid | Yes | FK to ConnectedSystemObject |
| ContributedBySystemId | integer | Yes | FK to ConnectedSystem |

### Out of Scope — MVO Updates (deferred)

`UpdateMetaverseObjectsAsync` is significantly more complex than creates:

- Current implementation uses careful per-entity `Entry().State` management to handle added/modified/deleted attribute values
- This was the subject of 11 debugging attempts documented in `docs/notes/done/CROSS_PAGE_REFERENCE_IDENTITY_CONFLICT.md`
- Raw SQL would need to replicate: INSERT new AVs, UPDATE modified AVs, DELETE removed AVs — all without EF change tracking
- The risk/reward ratio is unfavourable for the initial implementation

MVO updates should be tackled as a follow-up if creates deliver the expected throughput improvement.

## FK Fixup Consideration

The caller at `SyncTaskProcessorBase:1316-1323` relies on EF relationship fixup after `CreateMetaverseObjectsAsync` to populate `cso.MetaverseObjectId` from `cso.MetaverseObject.Id`:

```csharp
// After CreateMetaverseObjectsAsync, EF has assigned IDs and
// relationship fixup has set MetaverseObjectId on the CSOs.
foreach (var cso in _pendingCsoJoinUpdates)
{
    if (cso.MetaverseObjectId == null && cso.MetaverseObject != null)
        cso.MetaverseObjectId = cso.MetaverseObject.Id;
}
```

With pre-generated IDs this is actually **simpler** — `mvo.Id` is set before persistence, so the existing code at line 1322 (`cso.MetaverseObject.Id`) already returns the correct value. No caller changes needed.

## Implementation

### File: `src/JIM.PostgresData/Repositories/SyncRepository.MvoOperations.cs` (new)

New partial class file following the established pattern from `SyncRepository.CsOperations.cs`:

- `CreateMetaverseObjectsAsync(List<MetaverseObject>)` — entry point with ID pre-generation, route selection
- `CreateMvosOnSingleConnectionAsync(List<MetaverseObject>)` — single-connection fallback
- `BulkInsertMvosOnConnectionAsync(NpgsqlConnection, NpgsqlTransaction, IReadOnlyList<MetaverseObject>)` — COPY binary for parent rows
- `BulkInsertMvoAttributeValuesOnConnectionAsync(NpgsqlConnection, NpgsqlTransaction, List<(Guid, MetaverseObjectAttributeValue)>)` — COPY binary for child rows
- `BulkInsertMvosViaEfAsync(List<MetaverseObject>)` — parameterised multi-row INSERT fallback
- `BulkInsertMvoAttributeValuesViaEfAsync(List<(Guid, MetaverseObjectAttributeValue)>)` — parameterised multi-row INSERT fallback

### File: `src/JIM.PostgresData/Repositories/SyncRepository.cs`

Change delegation from `_repo.Metaverse.CreateMetaverseObjectsAsync` to the new owned implementation.

### Test approach

Unit tests verify behaviour via the in-memory `ISyncRepository` (which already has `CreateMetaverseObjectsAsync`). Integration tests validate the COPY binary path against a real PostgreSQL instance.

## Success Criteria

- `CreateMetaverseObjectsAsync` uses COPY binary for large batches, parameterised INSERT for small batches
- Pre-generated IDs ensure CSO FK fixup works without EF relationship fixup
- All existing unit tests pass without modification
- Integration tests pass (Scenarios 1, 2, 8)
- No regressions in sync integrity

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Shadow FK columns (`TypeId`, `MetaverseObjectId`) have wrong names | Verified against migration snapshot — names confirmed |
| `xmin` concurrency token written in COPY causes error | Excluded from COPY column list — PostgreSQL assigns automatically |
| Caller code expects EF relationship fixup for MVO IDs | Pre-generated IDs mean `mvo.Id` is set before persistence — verified caller code is compatible |
| Unit tests use in-memory provider without raw SQL | Established try/catch fallback pattern; delegation change only affects the Postgres path |
