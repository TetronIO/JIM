# Connector Space Search Keys

- **Status:** Planned
- **Related Plans:** none
- **Last Updated:** 2026-10-24

## Overview

Searching the Connector Space list scans every attribute value the Connected System holds. At 790,007 Connected System Objects (18 million attribute values, 4.9 GB) one search takes about 5 seconds, and every further scroll window of the results takes another 2.3 seconds, because the window query repeats the search predicate. This plan gives each Connected System Object a precomputed search key, held in a narrow table of its own with a trigram index, so the search becomes an index lookup. A prototype on the same 790k database brought the search from about 5 s to 31 ms.

The loading indicator and query cancellation shipped separately (the delayed indicator in `VirtualisedDataGrid`, and the cancellation token on `GetConnectedSystemObjectHeadersRangeAsync`). They make a slow search honest; this plan makes it fast.

## Business Value

- **The problem:** an administrator searching a large Connector Space (the scale JIM is built for: 1M objects) types a name and waits 5 seconds, then waits again on every scroll. At customer scale this is the page administrators reach for first when investigating an object.
- **Who it affects:** every customer with a Connected System above roughly 100k objects. The cost grows linearly with attribute value count, so directory systems with wide schemas are hit first.
- **The benefit:** search at interactive speed regardless of Connected System size, with no change to what a search matches.

## Measured Baseline and Prototype

Measured on the development database (Connected System 1, Yellowstone APAC, 790,007 objects, 18,091,488 attribute values).

| Query | Today | Prototype (search key + trigram index) |
|---|---|---|
| Count for "smith" (37,500 matches) | 2,596 ms | 31 ms |
| First window for "smith" (60 rows, created desc) | 2,348 ms | 2 ms |
| Rare term (no matches) | ~5 s | 0.4 ms |
| Two-character term "jo" (index cannot help) | ~5 s | 169 ms |
| Term every object matches ("ou=") | ~5 s | 175 ms |

- The prototype table was 157 MB with a 117 MB trigram index; average key length 137 characters. Building both for 790k objects took 13 s.
- The worst cases are no longer worst-case scans of the 4.9 GB attribute value table: they are sequential scans of a 157 MB table. Even they finish inside the 300 ms loading indicator threshold.
- Today's plan shape: one parallel sequential scan of `ConnectedSystemObjectAttributeValues` per OR branch (external ID, secondary external ID), plus an index scan for the name branch, hashed into a filter over every Connected System Object.

## Technical Architecture

### Current state

`ConnectedSystemRepository.QueryConnectedSystemObjectHeadersByRangeAsync` matches the search with three correlated `EXISTS` subqueries over `ConnectedSystemObjectAttributeValues`, each `ILIKE '%term%'`:

1. Name-tier attributes (`displayName`, `cn`, `name`, resolved per query by `GetNameAttributeIdTiersAsync`).
2. The external ID, rendered by `ExternalIdValueText.FromAttributeValue` from whichever typed column holds it (#1286).
3. The secondary external ID, rendered the same way.

No index can serve a leading-wildcard `ILIKE`, and the attribute value table is the largest in the schema.

### Proposed solution

A new table, one row per Connected System Object:

| Column | Type | Notes |
|---|---|---|
| `ConnectedSystemObjectId` | `uuid` | Primary key; foreign key to `ConnectedSystemObjects`, `ON DELETE CASCADE` |
| `ConnectedSystemId` | `int` | Denormalised so the search filters without joining |
| `SearchKey` | `text` | Name-tier values, rendered external ID, rendered secondary external ID, joined with a unit separator (`chr(31)`) so a term cannot match across two fields |

Indexes: a GIN trigram index on `SearchKey` (`gin_trgm_ops`), and `ConnectedSystemId`.

The search predicate becomes one `EXISTS` (or join) against this table with `SearchKey ILIKE '%term%'`. Everything else in the query (filters, sorts, projection) is unchanged.

**The key is computed in one place, in SQL.** A repository method `RecomputeConnectedSystemObjectSearchKeysAsync(IReadOnlyCollection<Guid> ids)` runs a single set-based `INSERT ... SELECT ... WHERE cso."Id" = ANY(@ids) ON CONFLICT ("ConnectedSystemObjectId") DO UPDATE SET "SearchKey" = EXCLUDED."SearchKey"` that derives the key from the attribute values already written. A sibling, `RecomputeConnectedSystemObjectSearchKeysForConnectedSystemAsync(int connectedSystemId)`, does the whole Connected System. No writer computes the key in C#, so the write paths only need to call the recompute; they never need to know the key's rules.

### Why a separate table rather than a column on `ConnectedSystemObjects`

| | Separate table (recommended) | Column on `ConnectedSystemObjects` |
|---|---|---|
| Hot write path | Unchanged. The CSO bulk COPY and bulk UPDATE keep their column lists | The key joins `CsoBulkColumns`; every import update rewrites a row 137 bytes wider, adding WAL on the hottest table |
| Guard tests | One new `RequiresPostgres` round-trip test | `BulkInsertColumnCompletenessTests`, `ConnectedSystemObjectWriterCompletenessTests` and the update/exclusion lists all need the column placed |
| Search scan when the index cannot help | Narrow table (157 MB at 790k) | The whole CSO row |
| Deletes | Cascade from the CSO; nothing to add | Free |
| Extra join in the search | Yes, on the primary key | No |

The extra join costs nothing measurable (the prototype's window query used the created-date index and filtered on the key in 0.9 ms).

### Alternatives considered and rejected

- **Trigram index directly on `ConnectedSystemObjectAttributeValues.StringValue`.** No schema change, but the index covers all 18 million values (every attribute, every distinguished name), adds GIN maintenance to every bulk COPY on the import hot path, and the search still joins through the EAV table. It also cannot index the rendered external ID, which comes from typed columns.
- **Database triggers maintaining the key.** Correct by construction, but a row-level trigger on the attribute value table fires once per value on bulk COPY (millions per import), and a statement-level trigger with transition tables puts sync-path logic where nobody reading the repository will find it.
- **Prefix search (`term%`) on the existing lower-case index.** Fast, but changes what a search matches. Distinguished names begin `uid=` or `CN=`, so a prefix search for a surname finds nothing.

### Where the key must be recomputed

From an inventory of every path that writes `ConnectedSystemObjectAttributeValues` (file references as of 2026-10-24):

| Path | Method | Recompute |
|---|---|---|
| Import creates (both the parallel and single-connection paths) | `SyncRepository.CreateConnectedSystemObjectsAsync` (`SyncRepository.CsOperations.cs`) | For the created ids, after the values phase |
| Import updates (full, delta, confirming import) | `ConnectedSystemRepository.UpdateConnectedSystemObjectsAsync` | For the updated ids, after additions and removals |
| Optimistic export apply | `SyncRepository.ApplyExportedAttributeValuesAsync` | For `affectedCsoIds` |
| External ID written back after export | `ConnectedSystemRepository.UpdateConnectedSystemObjectsWithNewAttributeValuesAsync` | For the batch's ids |
| Single-object EF writes (provisioning CSO, secondary external ID on a new CSO) | `CreateConnectedSystemObjectAsync`, `UpdateConnectedSystemObjectAsync`, `UpdateConnectedSystemObjectWithNewAttributeValuesAsync` | For that id |
| Schema Refresh Removal | `DeleteConnectedSystemAttributeValuesByAttributeIdsAsync` | Whole Connected System (the delete has no CSO id list) |
| CSO delete, Connector Space clear, Connected System delete | Various | None: the row cascades |
| Reference fix-ups (`FixupCrossBatchReferenceIdsAsync`, `ClearReferencesToConnectedSystemObjectsAsync`) | | None: they change only `ReferenceValueId`, which the key never reads |

Two things that would invalidate keys do not happen today, and the plan relies on that: `ExternalIdAttributeId`, `SecondaryExternalIdAttributeId` and `TypeId` are set only at creation and never changed; and changing which attribute is the external ID flips the attribute's flags without touching existing objects (the current search reads the object's columns, not the flags, so this is already the behaviour).

Name tiers are matched by attribute name, case-insensitively. Schema refresh's only rename is case-only, so tier membership cannot change under an existing key. A newly discovered `displayName` attribute has no values until the next import, which recomputes.

## Implementation Phases

### Phase 1: Schema and backfill

- Migration: `CREATE EXTENSION IF NOT EXISTS pg_trgm`, the table, its indexes, and a backfill of every existing Connected System Object using the same SQL the recompute uses.
- `DeletePathForeignKeyCoverageTests`: the cascade from `ConnectedSystemObjects` covers the Connected System delete and factory reset surfaces; add the table to their expectations.

### Phase 2: The recompute and its parity guard

- `RecomputeConnectedSystemObjectSearchKeysAsync` and the per-Connected System sibling, raw SQL per the worker hot-path rule.
- **Parity test** (`RequiresPostgres`): seed objects whose external IDs sit in every typed column (text, GUID, integer, long, decimal, date-time, boolean) and whose names sit in each tier. Assert that the key-based search returns exactly what the current EAV predicate returns, for a set of terms including partial GUIDs and dates. This pins the raw SQL rendering to `ExternalIdValueText`'s EF translation, the one place they can drift (Npgsql's text casts for date-time and decimal).

### Phase 3: Wire every write path

- Call the recompute at each chokepoint in the table above, inside the same transaction or connection phase as the write it follows.
- One `RequiresPostgres` test per chokepoint: perform the write through the public method, then assert the key reflects it. Pattern: `CsoPartitionIdPersistenceDatabaseTests`.
- **Convention guard:** a source sweep in the style of `ConnectedSystemObjectWriterCompletenessTests` that finds every raw write to `ConnectedSystemObjectAttributeValues` and fails unless its method is listed as either recomputing or exempt (with the reason), so the next write path cannot be added without a decision.

### Phase 4: Switch the search

- Replace the three `EXISTS` branches with the search key predicate.
- Re-run the existing `ConnectedSystemObjectHeaderRangeDatabaseTests` search cases unchanged; they define what a search matches.
- Re-measure on the 790k database with `EXPLAIN (ANALYZE, BUFFERS)` and record the numbers in the PR.

### Phase 5: Documentation and changelog

- `docs/getting-started/prerequisites.md` and `docs/administration/deployment.md`: an external PostgreSQL server needs the `pg_trgm` extension available (it ships in the standard contrib package and in the bundled image; it is a trusted extension, so the database owner can create it without superuser).
- Changelog: a performance entry for Connector Space search at scale.

## Success Criteria

- A search over the 790k Connected System returns its first window in under 100 ms for any term of three or more characters, and under 300 ms for shorter terms.
- The parity test proves the key-based search returns the same objects as today's predicate for every external ID storage type.
- Every write path in the inventory has a test proving the key is current after it.
- Import throughput at Scale500k is within 5% of the current baseline (the recompute is one set-based statement per batch).

## Benefits

- **User experience:** search the Connector Space at interactive speed at any size; scrolling a searched list stops costing 2.3 s per window.
- **Performance:** removes the largest table from the search path entirely. The worst case becomes a scan of a narrow table, not the attribute value table.
- **Architecture:** one SQL definition of what is searchable, enforced by a parity test and a convention sweep, instead of a predicate restated per branch.

## Dependencies

- PostgreSQL `pg_trgm` contrib extension. Already present in the bundled PostgreSQL image; no new NuGet package.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| A write path is missed and keys go stale, so objects silently drop out of search | Convention sweep fails the build for an unclassified writer; per-chokepoint tests; the backfill SQL doubles as a repair that can be re-run |
| Raw SQL rendering of a typed external ID differs from EF's | Parity test across every typed column |
| An external PostgreSQL server lacks `pg_trgm` | Documented prerequisite; the migration fails fast with PostgreSQL's own error naming the extension |
| Recompute adds latency to import batches | One set-based statement per batch; measured against the Scale500k baseline before merge |
| Backfill time on very large existing deployments | 13 s at 790k in the prototype; pre-release, so no production deployments to migrate yet |

## Out of Scope (follow-ups)

- **Metaverse Object list search** has the same shape and a broader one: `MetaverseRepository.QueryMetaverseObjectHeadersByRangeAsync` matches `ILIKE` against every string value of every attribute. It deserves the same treatment, as a separate issue.
- **Searching pending values** (`PendingDisplayName`, `PendingExternalId`, `PendingSecondaryExternalId`). Not searched today; including them would make the Pending Export writers chokepoints too.
- **Display name sort and projection.** The same table could carry the tier-coalesced display name, replacing the three correlated subqueries the list uses to sort and render it.
- **Cancellation on the other range reads.** Only the Connected System Object header range read takes a cancellation token so far; the other virtualised range reads (`GetMetaverseObjectHeadersRangeAsync`, `GetActivitiesRangeAsync` and friends) should follow.
