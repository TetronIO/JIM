# CSO Large Multi-Valued Attribute Pagination

**GitHub Issue:** #320
**Status:** Doing (Phases 1–2 complete)
**Date:** 2026-03-05

## Problem Statement

The `GetConnectedSystemObjectAsync` repository method eagerly loads ALL attribute values with deep `.Include()` chains. For a CSO with a large multi-valued attribute (e.g. a group with 10,000 members), this loads the full entity graph into memory in a single query — including every reference value and their attribute values.

This is a **data layer problem** that affects every consumer:

| Consumer | Impact |
|---|---|
| **Worker** (sync processing) | Memory pressure, change tracker overhead, potential OOM/timeouts with concurrent sync tasks processing multiple large groups |
| **Web UI** (CSO detail page) | Slow page load, all values held in Blazor Server memory |
| **API** (CSO detail endpoint) | Unbounded JSON response, slow serialisation |

### Root Cause: Deep Eager Loading

The core issue is in `ConnectedSystemRepository.GetConnectedSystemObjectAsync` (~line 1026):

```
ConnectedSystemObject
  +-- AttributeValues (10,000 for a large group)
       +-- Attribute
       +-- ReferenceValue (another ConnectedSystemObject)
            +-- Type
            +-- AttributeValues (all attributes of referenced CSO)
                 +-- Attribute
  +-- MetaverseObject
       +-- Type
       +-- AttributeValues
            +-- Attribute
```

For a group with 10K members, this loads ~10K `ConnectedSystemObjectAttributeValue` entities, each with a `ReferenceValue` navigation pointing to another `ConnectedSystemObject`, each with their own `AttributeValues`. That's potentially **100K+ tracked entities** for a single group object.

### Stability Risks

- **EF Core change tracker** scales poorly with entity count — `SaveChangesAsync` diffs every tracked entity
- **Memory pressure** — multiple groups being processed concurrently compounds the problem
- **Query timeouts** — even with `AsSplitQuery()`, the split queries for 10K reference values' attribute values are heavy
- **Blazor Server** holds the full entity graph in circuit memory for the duration of the page session

### What Already Works Well

- Inline display on the CSO detail page is capped at `MvaInlineThreshold = 10` with a "+N more" button
- `CsoMvaDialog` uses `Virtualize="true"` and has search/filter — good UX patterns, just needs server-side backing

## Proposed Solution: Fix at the Data Layer, Resolve Upward

The fix must start at the repository/data layer, with both worker and web/API consuming leaner queries appropriate to their needs.

```
+-----------------------------------------------------+
| Data Layer (Repository)                             |
|                                                     |
| GetConnectedSystemObjectAsync()         (fixed)     |
|   Loads CSO + attribute values + shallow refs       |
|   (no deep reference attribute includes)            |
|   Optional loading strategy: all values (default),  |
|   capped MVA values with counts, SVAs only          |
|                                                     |
| GetAttributeValuesPagedAsync()          (new)       |
|   Paginated attribute values for a single attr      |
|   with optional search/filter                       |
|                                                     |
| GetConnectedSystemObjectReferenceProjectionsAsync() |
|   Lightweight projection: (RefValueId,        (new) |
|   MetaverseObjectId) — just what sync needs         |
+-----------------------------------------------------+
          |                          |
          v                          v
+------------------+    +---------------------+
| Web / API        |    | Worker              |
| Server-side      |    | Lightweight ref     |
| pagination for   |    | projections instead |
| display          |    | of deep entity      |
+------------------+    | graphs              |
                        +---------------------+
```

### Fix `GetConnectedSystemObjectAsync` In Place

Rather than creating a new method, **fix the existing `GetConnectedSystemObjectAsync`** to remove deep eager loading of referenced CSOs' attribute values. This is what the method should have been doing all along. All current consumers benefit immediately.

**Loading strategy parameter (Phase 2):** Even with shallow references, loading 10K+ attribute value entities in one call is still too heavy for web/API consumers that only display a capped subset. In Phase 2, `GetConnectedSystemObjectAsync` gains an optional loading strategy parameter that controls how much attribute data is loaded (e.g. all values, capped MVA values with counts, SVAs only). This allows consumers to request only the data they need — the web detail page and API load a capped set, while the worker continues to load all values for sync processing. See section 2.1.

### Phase 1: Lightweight Worker Queries

**Goal:** Eliminate deep eager loading from sync processing. The worker doesn't need full entity graphs — it needs specific data for specific operations.

#### 1.1 Analyse Worker Usage Patterns

The worker uses CSO attribute values in these ways:

| Operation | What it needs | Current cost |
|---|---|---|
| **Scoping evaluation** (`IsCsoInScopeForImportRule`) | SVA values for the CSO being evaluated | Loads ALL values including MVA references |
| **Attribute mapping** (`SyncRuleMappingProcessor`) | CSO attribute values by attribute ID | Loads ALL values; filters in memory |
| **Reference resolution** (`ProcessReferenceAttribute`) | For each ref value: `ReferenceValueId`, `ReferenceValue.MetaverseObjectId` | Loads full `ReferenceValue` entity with all its `AttributeValues` |
| **Export confirmation** | CSO attribute values by attribute ID | Loads ALL values; filters in memory |

Key insight: `ProcessReferenceAttribute` only needs `(ReferenceValueId, MetaverseObjectId)` per reference — not the full referenced CSO entity with all its attributes. The deep includes exist only because the detail page needs display names and secondary IDs.

#### 1.2 Fix `GetConnectedSystemObjectAsync`: Remove Deep Includes ✅

Fix the existing method so that it:

- Loads the CSO with its `AttributeValues` and their `Attribute` metadata
- Loads `ReferenceValue` navigation but **only** includes `Type` and `MetaverseObjectId` — NOT the referenced CSO's own `AttributeValues`
- Loads `MetaverseObject` with `Type` and `AttributeValues` (needed for mapping)

This eliminates the most expensive part: loading every referenced CSO's full attribute set.

```
Before (current):
  CSO -> 10K AttributeValues -> 10K ReferenceValues -> 10K * N AttributeValues
  ~100K+ entities

After (Phase 1):
  CSO -> 10K AttributeValues -> 10K ReferenceValues (Id, Type, MetaverseObjectId only)
  ~20K entities
```

#### 1.3 Evaluate Whether Worker Needs Full Collection

Currently `SyncRuleMappingProcessor.ProcessReferenceAttribute` loads all reference values to compute a full diff (add/remove). Consider whether this can be chunked or streamed for very large MVAs, though this may be Phase 3 scope depending on measured improvement from 1.2.

### Phase 2: Web/API Pagination ✅

**Goal:** The web UI and API never load unbounded attribute values.

#### 2.1 Loading Strategy Parameter on `GetConnectedSystemObjectAsync`

Add an optional parameter to `GetConnectedSystemObjectAsync` that controls attribute value loading. This keeps the API surface clean — one method, configurable behaviour:

```csharp
Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(
    int connectedSystemId,
    Guid id,
    CsoAttributeLoadStrategy loadStrategy = CsoAttributeLoadStrategy.All);
```

Possible strategies:

| Strategy | Behaviour | Consumer |
|---|---|---|
| `All` (default) | Loads all attribute values with shallow refs. Current behaviour after Phase 1 fix. | Worker |
| `CappedMva` | Loads all SVA values normally. For MVA attributes, loads first N values (matching inline display threshold, currently 10) and returns total count per attribute. | Web detail page, API detail endpoint |
| `SvasOnly` | Loads only single-valued attribute values. | Future use / lightweight lookups |

When using `CappedMva`, the method returns per-attribute value counts alongside the capped values (via a companion result object or a metadata property on the entity). The web page and API use this to show "10,247 total" and offer paginated access via `GetAttributeValuesPagedAsync`.

#### 2.2 New Repository Method: Paginated Attribute Values

New method on `IConnectedSystemRepository`:

```csharp
Task<PagedResult<ConnectedSystemObjectAttributeValue>>
    GetAttributeValuesPagedAsync(
        Guid connectedSystemObjectId,
        string attributeName,
        int page,
        int pageSize,
        string? searchText);
```

Queries the database with `Skip`/`Take` and optional `WHERE` filtering. For reference attributes, includes display info (type name, display name, secondary ID) only for the current page.

#### 2.3 New API Endpoint

```
GET /api/connected-systems/{csId}/objects/{csoId}/attributes/{attributeName}/values
    ?page=1
    &pageSize=50
    &search=smith
```

Response:

```json
{
  "attributeName": "member",
  "totalCount": 10247,
  "filteredCount": 42,
  "page": 1,
  "pageSize": 50,
  "values": [...]
}
```

- Supports server-side search/filter
- Supports pagination
- Used by API consumers (e.g. the JIM PowerShell module)
- The Blazor dialog does not use this endpoint — it calls JIM.Application directly

#### 2.4 Application Layer Changes

- Pass `CsoAttributeLoadStrategy` through `ConnectedSystemServer.GetConnectedSystemObjectAsync`
- Add `GetAttributeValuesPagedAsync` to `ConnectedSystemServer`
- Web page and API controller call `GetConnectedSystemObjectAsync` with `CappedMva` strategy
- Worker continues calling with default `All` strategy (no change)

#### 2.5 DTO Changes

Add metadata to the API response:

```
ConnectedSystemObjectDetailDto
  AttributeValues: [...]          // capped at 10 per MVA attribute
  AttributeValueSummaries:        // NEW: per-attribute metadata
    - AttributeName: "member"
      TotalCount: 10247
      ReturnedCount: 10
      HasMore: true
```

#### 2.6 CsoMvaDialog: Server-Side Data

Convert `CsoMvaDialog` from client-side filtering to `MudTable` `ServerData` callback:

- Dialog receives only the attribute name, CSO ID, connected system ID, and total count
- On open (and on search/page change), calls the application layer which calls the paginated repository method
- Search and pagination happen in the database
- Memory usage is bounded to one page of results

#### 2.7 Web Page Changes

- `ConnectedSystemObjectDetail.razor` uses the detail-view method (capped values)
- Passes CSO ID + attribute name to the dialog instead of the full value list
- No change to inline display (already capped at 10)

### Phase 3: Further Worker Optimisation (If Needed)

After measuring the impact of Phase 1, consider these additional optimisations if large MVA processing is still problematic:

#### 3.1 Chunked Reference Processing

For `ProcessReferenceAttribute` with very large MVAs (e.g. 50K+ members):

- Load reference values in chunks (e.g. 1000 at a time) using `Skip`/`Take`
- Build the resolved MVO ID set incrementally
- Perform the diff (add/remove) after all chunks are processed
- This bounds peak memory to chunk size rather than full MVA size

#### 3.2 Projection-Based Reference Loading

Instead of loading full `ConnectedSystemObject` entities as reference values, use a projection:

```csharp
Task<List<CsoReferenceProjection>> GetConnectedSystemObjectReferenceProjectionsAsync(
    Guid connectedSystemObjectId,
    int attributeId);

// Returns only what ProcessReferenceAttribute needs:
record CsoReferenceProjection(
    Guid AttributeValueId,
    Guid? ReferenceValueId,
    Guid? MetaverseObjectId,
    string? UnresolvedReferenceValue);
```

This avoids EF Core entity tracking entirely for reference resolution.

#### 3.3 Materialised Display Names

Store a `DisplayName` column on `ConnectedSystemObject` maintained during import, eliminating the need to traverse reference value attribute values for display purposes. Benefits both the detail view and the worker (logging/diagnostics).

## Design Decisions Needed

| Decision | Options | Recommendation |
|---|---|---|
| Default cap for detail view | 10 / 50 / 100 | 10 — matches inline display threshold; dialog fetches its own pages |
| Default page size for paginated endpoint | 25 / 50 / 100 | 50 — standard pagination size |
| Should API always include `AttributeValueSummaries`? | Always / Only when capped | Always — API consumers need predictable structure |
| Phase 1 approach | Remove deep includes / Projection / Chunked | Remove deep includes first (simplest), measure, then consider projection |
| Phase 3 trigger | Always implement / Only if Phase 1 insufficient | Measure after Phase 1 — may not be needed for typical deployments |

## Implementation Order

```
Phase 1 (Worker Safety)
+-- 1.1 Analyse and document worker attribute usage patterns
+-- 1.2 Fix GetConnectedSystemObjectAsync (remove deep ref includes) ✅
+-- 1.3 Verify all consumers work with the fixed method
+-- Tests for all of the above ✅

Phase 2 (Web/API Pagination)
+-- 2.1 Add CsoAttributeLoadStrategy parameter to GetConnectedSystemObjectAsync ✅
+-- 2.2 New paginated attribute values repository method ✅
+-- 2.3 New paginated API endpoint ✅
+-- 2.4 Application layer methods ✅
+-- 2.5 DTO changes with summaries ✅
+-- 2.6 Convert CsoMvaDialog to ServerData ✅
+-- 2.7 Update web page to use detail-view method ✅
+-- Tests for all of the above ✅

Phase 3 (Further Optimisation — If Needed)
+-- Measure performance after Phase 1+2
+-- 3.1 Chunked reference processing (if memory still an issue)
+-- 3.2 Projection-based reference loading (if change tracker overhead still an issue)
+-- 3.3 Materialised display names (if reference display info is a bottleneck)
```

## Non-Goals

- Pagination of the CSO object list page (separate concern)
- Pagination of change history (separate concern, already bounded by time)
- Changing sync semantics — the worker must still process all values, just loaded more efficiently

## Acceptance Criteria

- [ ] Worker loads CSOs without deep-including all referenced CSOs' attribute values
- [ ] Worker remains stable when processing groups with 10K+ members
- [x] CSO detail page loads in reasonable time for a group with 10K+ members
- [x] Web UI shows value count and provides access to all values via paginated dialog
- [x] API endpoint returns capped values with metadata indicating total count
- [x] New paginated attribute values API endpoint supports search and pagination
- [x] Unit tests cover new repository methods, application layer, and API endpoints
