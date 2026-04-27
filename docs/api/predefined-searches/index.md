---
title: Predefined Searches
---

# Predefined Searches

The Predefined Searches API allows administrators to list the named, reusable searches that drive portal list views and the fast `GET /api/v1/metaverse/objects/search/{uri}` search endpoint, and to toggle whether each one is visible to end users.

Disabled searches are hidden from the portal, the end-user search API, and the sidebar navigation. They remain discoverable through this API and the admin UI so they can be re-enabled at any time.

!!! info "End-user search vs admin management"
    To *run* a predefined search and return matching metaverse objects, use [`GET /api/v1/metaverse/objects/search/{predefinedSearchUri}`](../metaverse/objects.md#search-objects). The endpoints on this page are for *administering* the list of searches.

## The Predefined Search Header Object

Returned by [List Predefined Searches](#list-predefined-searches).

```json
{
  "id": 3,
  "name": "People",
  "uri": "people",
  "isEnabled": true,
  "builtIn": true,
  "isDefaultForMetaverseObjectType": true,
  "metaverseObjectTypeName": "Person",
  "metaverseAttributeCount": 6,
  "created": "2026-01-10T09:00:00Z"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | integer | Unique identifier |
| `name` | string | Human-readable display name |
| `uri` | string | Stable slug used in URLs and as a search identifier |
| `isEnabled` | boolean | Whether the search is currently visible to end users |
| `builtIn` | boolean | Whether the search ships with JIM (as opposed to being administrator-defined) |
| `isDefaultForMetaverseObjectType` | boolean | Whether this is the default search for its object type |
| `metaverseObjectTypeName` | string | Name of the Metaverse Object Type the search targets |
| `metaverseAttributeCount` | integer | Number of attributes surfaced in the search results |
| `created` | datetime | UTC creation timestamp |

## The Predefined Search Object

Returned by [Get a Predefined Search by ID](#get-a-predefined-search-by-id) and [Get a Predefined Search by URI](#get-a-predefined-search-by-uri). Includes the full search graph: header fields plus the displayed attributes and the criteria-group tree.

| Field | Type | Description |
|-------|------|-------------|
| `id` | integer | Unique identifier |
| `name` | string | Human-readable display name |
| `uri` | string | Stable slug used in URLs and as a search identifier |
| `isEnabled` | boolean | Whether the search is currently visible to end users |
| `builtIn` | boolean | Whether the search ships with JIM (as opposed to being administrator-defined) |
| `isDefaultForMetaverseObjectType` | boolean | Whether this is the default search for its object type |
| `metaverseObjectType` | object | The Metaverse Object Type the search targets |
| `attributes` | array | Attributes surfaced in the search results, ordered by `position` |
| `criteriaGroups` | array | Criteria groups that filter which objects match the search |
| `created` | datetime | UTC creation timestamp |

---

## List Predefined Searches

Returns all Predefined Searches as lightweight headers, including any that are currently disabled, so administrators can discover their IDs and current state.

```
GET /api/v1/predefined-searches
```

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/predefined-searches \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMPredefinedSearch
    ```

### Response

Returns `200 OK` with an array of [header objects](#the-predefined-search-header-object).

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |

---

## Get a Predefined Search by ID

Returns the full graph (attributes and criteria groups) for the search with the given integer ID. The ID is the canonical identifier; for lookup by URI slug see [Get a Predefined Search by URI](#get-a-predefined-search-by-uri).

```
GET /api/v1/predefined-searches/{id}
```

### Request

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | integer (route) | The unique identifier of the Predefined Search |

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/predefined-searches/3 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Get-JIMPredefinedSearch -Id 3
    ```

### Response

Returns `200 OK` with a [Predefined Search Object](#the-predefined-search-object).

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | No Predefined Search exists with the supplied ID |

---

## Get a Predefined Search by URI

Convenience lookup by the search's stable, human-readable slug (for example `people` or `security-groups`). Returns the same full graph as the by-ID endpoint.

```
GET /api/v1/predefined-searches/by-uri/{uri}
```

### Request

| Parameter | Type | Description |
|-----------|------|-------------|
| `uri` | string (route) | The URI slug of the Predefined Search |

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/predefined-searches/by-uri/people \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Get-JIMPredefinedSearch -Uri people
    ```

### Response

Returns `200 OK` with a [Predefined Search Object](#the-predefined-search-object).

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `400` | `BAD_REQUEST` | URI was empty or whitespace |
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | No Predefined Search exists with the supplied URI |

### Notes

- The integer ID returned in the response is the canonical identifier. Subsequent write operations (PATCH) must be keyed by ID, not URI.

---

## Update a Predefined Search

Applies a partial update to the search identified by `id`. Only fields present in the request body are applied; omitted fields are left unchanged.

```
PATCH /api/v1/predefined-searches/{id}
```

### Request

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | integer (route) | The unique identifier of the Predefined Search |

Body fields (all optional):

| Field | Type | Description |
|-------|------|-------------|
| `isEnabled` | boolean | When present, sets whether the search is visible to end users. Pass `true` to enable, `false` to disable. |

### Examples

=== "curl"

    ```bash
    curl -X PATCH https://jim.example.com/api/v1/predefined-searches/3 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{ "isEnabled": false }'
    ```

=== "PowerShell"

    ```powershell
    Set-JIMPredefinedSearch -Id 3 -IsEnabled $false
    ```

### Response

Returns `204 No Content` on success.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | No Predefined Search exists with the supplied ID |

### Notes

- The endpoint is deliberately keyed by `id` rather than `uri`. Because `uri` may itself become mutable in future, write operations always use the immutable ID. Use [List Predefined Searches](#list-predefined-searches) to resolve a URI to an ID.
- Disabling a search takes effect immediately for new portal navigations and API calls. Admin surfaces (this API, the admin UI, and the PowerShell module) continue to show disabled searches so they can be re-enabled.

---

## See also

- [Search Metaverse Objects](../metaverse/objects.md#search-objects): run a Predefined Search to return matching objects
- [PowerShell: Predefined Searches](../../powershell/predefined-searches.md): cmdlets that wrap these endpoints
