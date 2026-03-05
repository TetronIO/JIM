# CSO Large Multi-Valued Attribute Pagination

**GitHub Issue:** #320
**Status:** Planning
**Date:** 2026-03-05

## Problem Statement

The CSO detail page (`ConnectedSystemObjectDetail.razor`) and the API endpoint (`GET /api/connected-systems/{id}/objects/{id}`) have performance and UX issues when a CSO has multi-valued attributes with large numbers of values (e.g. a group with 10,000 members).

### Root Causes

1. **Database query loads all values eagerly** — `GetConnectedSystemObjectAsync` in `ConnectedSystemRepository` uses deep `.Include()` chains to load ALL attribute values, their reference values, and those reference values' own attribute values. For a 10K-member group, this produces a massive query even with `AsSplitQuery()`.

2. **All values held in Blazor Server memory** — The `CsoMvaDialog` receives the full `List<ConnectedSystemObjectAttributeValue>` in-memory. While it uses `Virtualize="true"` for rendering, the entire dataset is still loaded server-side.

3. **API returns unbounded attribute values** — `ConnectedSystemObjectDetailDto.FromEntity()` maps ALL attribute values into the response JSON with no cap or pagination.

### Affected Code

| File | Concern |
|---|---|
| `src/JIM.PostgresData/Repositories/ConnectedSystemRepository.cs` (~line 1026) | Eager loading all values with deep includes |
| `src/JIM.Web/Pages/Admin/ConnectedSystemObjectDetail.razor` | Page consumes full CSO with all values |
| `src/JIM.Web/Shared/CsoMvaDialog.razor` | Dialog receives all values in-memory |
| `src/JIM.Web/Controllers/Api/SynchronisationController.cs` (~line 377) | API endpoint returns all values |
| `src/JIM.Web/Models/Api/ConnectedSystemDto.cs` (~line 164) | DTO maps all values without cap |
| `src/JIM.Application/Servers/ConnectedSystemServer.cs` (~line 2512) | Pass-through, but shares method with worker |

### What Already Works Well

- Inline display is capped at `MvaInlineThreshold = 10` with a "+N more" button — the page itself won't render 10K components.
- `CsoMvaDialog` uses `Virtualize="true"` and has search/filter — good UX patterns, just needs server-side backing.

## Proposed Solution: Hybrid Approach

Combine a quick repository-level cap with a new paginated attribute endpoint for full access.

### Phase 1: Cap Values in Detail View (Quick Win)

**Goal:** Prevent the main CSO detail load from fetching unbounded values.

#### 1.1 New Repository Method for Detail View

Create a new method `GetConnectedSystemObjectDetailAsync` (or add an overload) that:

- Loads all single-valued attributes normally (lightweight)
- For multi-valued attributes, loads only the first N values (e.g. 100)
- Returns a total value count per attribute so the UI/API can show "100 of 10,247"

```
+----------------------------------------------+
| GetConnectedSystemObjectAsync (existing)     |
| - Used by worker/sync processing             |
| - Loads ALL values (needed for sync logic)   |
+----------------------------------------------+

+----------------------------------------------+
| GetConnectedSystemObjectForDetailAsync (new) |
| - Used by web UI and API detail endpoints    |
| - Caps MVA values at N per attribute         |
| - Returns total count per attribute          |
+----------------------------------------------+
```

**Key consideration:** The existing `GetConnectedSystemObjectAsync` is also used by `SyncTaskProcessorBase` which needs all values. We must NOT change its behaviour. Instead, add a new method specifically for the detail/display use case.

#### 1.2 Application Layer Changes

- Add `GetConnectedSystemObjectForDetailAsync` to `ConnectedSystemServer`
- Returns a DTO or wrapper that includes per-attribute total counts
- Web page and API controller switch to this method

#### 1.3 DTO Changes

Add metadata to the API response:

```
ConnectedSystemObjectDetailDto
  AttributeValues: [...]          // capped at N
  AttributeValueSummaries:        // NEW: per-attribute metadata
    - AttributeName: "member"
      TotalCount: 10247
      ReturnedCount: 100
      HasMore: true
```

### Phase 2: Paginated Attribute Values Endpoint

**Goal:** Allow the dialog and API consumers to page through large MVAs server-side.

#### 2.1 New API Endpoint

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
- Used by both API consumers and the Blazor dialog

#### 2.2 Repository Method

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

This queries the database directly with `Skip`/`Take` and optional `WHERE` filtering, avoiding loading all values into memory.

#### 2.3 CsoMvaDialog: Server-Side Data

Convert `CsoMvaDialog` from client-side filtering to `MudTable` `ServerData` callback:

- Dialog receives only the attribute name, CSO ID, and total count
- On open (and on search/page change), calls the application layer which calls the paginated repository method
- Search and pagination happen in the database
- Memory usage is bounded to one page of results

#### 2.4 Web Page Changes

- `ConnectedSystemObjectDetail.razor` passes CSO ID + attribute name to the dialog instead of the full value list
- No change to inline display (already capped at 10)

### Phase 3: Optimise Reference Value Loading (Optional)

The current query loads reference values' own attribute values to display secondary external IDs. For 10K reference members, this means loading 10K CSOs' attribute values.

Options:
- **Materialise display names** — Store a `DisplayName` column on `ConnectedSystemObject` that's maintained during import, avoiding the need to traverse reference value attributes
- **Lazy-load reference details** — Load only reference value IDs and type in the main query; load display names on demand (per page) in the paginated endpoint
- **Database view/projection** — Create a lightweight projection that returns just what the UI needs (type name, display name, secondary ID) without full entity loading

This is the biggest performance win for reference-heavy attributes but also the largest change.

## Design Decisions Needed

| Decision | Options | Recommendation |
|---|---|---|
| Default cap for detail view | 50 / 100 / 200 | 100 — balances completeness with performance |
| Default page size for paginated endpoint | 25 / 50 / 100 | 50 — standard pagination size |
| Should API always include `AttributeValueSummaries`? | Always / Only when capped | Always — API consumers need predictable structure |
| Split repository method or parameterise? | New method / Optional parameter | New method — keeps sync path untouched, clear separation of concerns |
| Phase 3 approach | Materialised display name / Lazy load / Projection | Defer decision until Phase 1+2 results are measured |

## Implementation Order

```
Phase 1 (Quick Win)
+-- 1.1 New repository method with value cap
+-- 1.2 Application layer method
+-- 1.3 DTO changes with summaries
+-- 1.4 Update web page + API controller to use new method
+-- Tests for all of the above

Phase 2 (Full Pagination)
+-- 2.1 New paginated repository method
+-- 2.2 New API endpoint
+-- 2.3 Application layer method
+-- 2.4 Convert CsoMvaDialog to ServerData
+-- 2.5 Update web page to pass IDs instead of values
+-- Tests for all of the above

Phase 3 (Optional Optimisation)
+-- Measure performance after Phase 1+2
+-- Decide on reference value loading strategy
+-- Implement if needed
```

## Non-Goals

- Changing how sync processing loads CSOs (worker needs full data)
- Pagination of the CSO object list page (separate concern)
- Pagination of change history (separate concern, already bounded by time)

## Acceptance Criteria

- [ ] CSO detail page loads in reasonable time for a group with 10K+ members
- [ ] Web UI shows value count and provides access to all values via paginated dialog
- [ ] API endpoint returns capped values with metadata indicating total count
- [ ] New paginated attribute values API endpoint supports search and pagination
- [ ] Existing sync/worker functionality is unaffected
- [ ] Unit tests cover new repository methods, application layer, and API endpoints
