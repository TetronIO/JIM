# Short ID Alternatives to GUIDs

## Status: Researched (2026-03-03)

Background research into shorter identifier formats that could replace GUIDs for improved URL and UI aesthetics. No action planned — captured for future reference.

## Current State

JIM uses two primary key types:

- **`int`** (~70% of entities) — object types, attributes, connected systems, partitions, containers, run profiles, sync rules, etc.
- **`Guid`** (~30%, 28 entities) — MetaverseObject, ConnectedSystemObject, Activity, Schedule, PendingExport, ApiKey, and related entities

GUIDs also appear in 100+ foreign key / audit fields and 28+ API routes with `{id:guid}` constraints.

## PostgreSQL Sequence Behaviour

PostgreSQL sequences backing `SERIAL`/`IDENTITY` columns **never reuse values**, even after row deletion. The counter only moves forward. Reuse would require explicit manual intervention (`ALTER SEQUENCE ... RESTART` or `setval()`), which JIM never does.

This means `int` PKs in JIM are safe from reuse and do not need GUID-level uniqueness guarantees.

## Alternatives Considered

### 1. Base62-Encoded GUIDs (Display Only)

- **Format**: `2VsBMkfHuMqGGsh5W3LoAA` (22 chars vs 36)
- **Storage**: `uuid` in database (unchanged)
- **Effort**: Low — encode/decode at API route and UI boundary only
- **Risk**: Near-zero — no schema or logic changes
- **Trade-off**: Moderate aesthetic improvement, no structural benefit

### 2. ULID (Universally Unique Lexicographically Sortable Identifier)

- **Format**: `01ARZ3NDEKTSV4RRFFQ69G5FAV` (26 chars, Crockford Base32)
- **Storage**: 128-bit (same as GUID)
- **Effort**: Very high — full migration of all GUID columns, routes, DTOs, tests
- **Risk**: High
- **Trade-off**: Sortable by creation time, no hyphens, case-insensitive; same collision resistance as GUID

### 3. NanoID

- **Format**: `V1StGXR8_Z5jdHi6B-myT` (21 chars default, configurable)
- **Storage**: `text` in database (no native DB type)
- **Effort**: Very high
- **Risk**: High — string-based PK, collision probability depends on chosen length
- **Trade-off**: Very short and URL-safe, but loses native UUID indexing benefits

### 4. TSID (Time-Sorted Unique Identifiers)

- **Format**: `0ARXKWT2MG29N` (13 chars, Crockford Base32)
- **Storage**: 64-bit `bigint`
- **Effort**: Very high — migration from `uuid` to `bigint` for all GUID columns
- **Risk**: High — reduced collision space (still safe for single-instance)
- **Trade-off**: Very short, sortable, efficient storage; significant migration

### 5. Sqids (formerly Hashids)

- **Format**: `X9f2gP` (6-12 chars, variable)
- **Storage**: Encodes integers only — not applicable to existing GUID entities
- **Effort**: Low for `int`-based entities
- **Risk**: Low
- **Trade-off**: Only works with integer PKs; reversible encoding, no storage needed

## Summary

| Approach | Chars | Effort | Risk | DB Change |
|---|---|---|---|---|
| GUID (current) | 36 | None | None | None |
| Base62-encoded GUID | 22 | Low | Near-zero | None |
| ULID | 26 | Very high | High | uuid (compatible) |
| NanoID | 21 | Very high | High | text |
| TSID | 13 | Very high | High | bigint |
| Sqids (int PKs only) | 6-12 | Low | Low | None |

## Conclusion

If aesthetics is the primary concern, **Base62-encoding GUIDs at the API/UI boundary** offers the best effort-to-benefit ratio. The database stays `uuid`, all existing logic remains unchanged, and URLs shrink from 36 to 22 characters.

A deeper change (ULID, TSID) would require migrating 28 entity PKs, 100+ foreign keys, 28+ API routes, all DTOs, and all tests — a significant initiative best justified by functional requirements beyond aesthetics.
