---
title: Predefined Searches
---

# Predefined Searches

A Predefined Search is a named, reusable search over metaverse objects. Predefined Searches drive the portal's list views and back the fast `GET /api/v1/metaverse/objects/search/{uri}` endpoint that end-user search consumers call. They are administrator-managed: end users do not author new searches, they consume the ones that already exist.

> Endpoint reference for this resource is in the [Scalar API reference](../index.md#where-to-find-what). This page covers the model and the operational behaviour.

## Key Concepts

**Stable URI slug.** Each search has a human-readable URI (e.g. `people`, `security-groups`) which is what consuming code uses to invoke the search. Treat the URI as a stable identifier; even though the underlying integer ID is the canonical key for write operations, the URI is what callers refer to.

**Built-in vs administrator-defined.** Searches that ship with JIM are marked `builtIn` and cannot be deleted; they can however be enabled or disabled. Administrator-defined searches are first-class and can be created, edited, and removed.

**Default for object type.** Each metaverse object type may have one default Predefined Search. The default is what the portal uses when navigating to that object type without a specific search selection.

**Enabled flag.** Disabling a search hides it from end users in the portal, the end-user search API, and the sidebar navigation. It remains discoverable through this admin API and the admin UI so it can be re-enabled at any time -- disable rather than delete when you want to suppress a search temporarily.

**Attributes and criteria.** A search defines which attributes appear as columns in the result list, and a tree of criteria groups (with nested groups and AND/OR logic) that filter which objects match.

## Running vs administering searches

There are two distinct surfaces:

- **Running** a search to get matching objects: `GET /api/v1/metaverse/objects/search/{uri}` (covered as part of the [Metaverse](../metaverse/index.md) endpoints in the Scalar reference). This is what the portal and end-user integrations call.
- **Administering** the list of searches (listing, retrieving, enabling, disabling): the endpoints documented for this resource in the Scalar reference.

The two surfaces never overlap. Administrative endpoints never return search results; the search-execution endpoint never returns search definitions.

## Common Workflows

**Hiding a built-in search from end users:**

1. List Predefined Searches to find the search by name and capture its ID
2. Update the search with `isEnabled = false`
3. The search disappears from the portal immediately on the next page load

**Re-enabling a previously hidden search:**

1. List Predefined Searches (this returns disabled searches too)
2. Update with `isEnabled = true`

## See also

- [Metaverse](../metaverse/index.md) -- the search-execution endpoint lives with the metaverse object endpoints
- [PowerShell: Predefined Searches](../../powershell/predefined-searches.md) -- cmdlets that wrap these endpoints
