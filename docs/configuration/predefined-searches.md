---
title: Predefined Searches
---

# Predefined Searches

A **predefined search** is a named, reusable search over [metaverse](metaverse.md) objects. Predefined searches drive the portal's list views and back the fast named-search endpoint that end-user search consumers call.

Predefined searches are administrator-managed: end users do not author new searches, they consume the ones that already exist.

## What a predefined search defines

A predefined search ties together:

- **A target metaverse object type**<br /> The object type whose objects this search returns.
- **A set of attributes**<br /> The fields surfaced as columns in the result list, in display order.
- **A criteria tree**<br /> Groups of criteria with AND/OR logic and nested groups, controlling which objects match.
- **A stable URI slug**<br /> The human-readable identifier (e.g. `people`, `security-groups`) that consuming code uses to invoke the search.

## Stable URI slug

Each search has a URI that is what consumers refer to. Treat the URI as a stable identifier; even though the underlying integer ID is the canonical key for write operations, the URI is what end-user code, the portal, and integrations will call by name. Picking a sensible URI when you create a search matters more than picking a name.

## Built-in vs administrator-defined

Searches that ship with JIM are marked **built-in** and cannot be deleted; they can however be enabled, disabled, and customised in some cases. Administrator-defined searches are first-class and can be created, edited, and removed.

## Default for object type

Each metaverse object type may have one default predefined search. The default is what the portal uses when navigating to that object type without a specific search selection.

## Enabled flag

Disabling a search hides it from end users in the portal, the end-user search API, and the sidebar navigation. It remains discoverable through the admin surfaces (this configuration view, the admin UI, and PowerShell) so it can be re-enabled at any time.

**Disable rather than delete** when you want to suppress a search temporarily, or when you want to keep its definition around for future re-enablement.

## Running vs administering

There are two distinct surfaces:

- **Running** a search to get matching objects (the portal does this every time an end user opens a list view, and integrations can call it directly to retrieve a curated set of identities)
- **Administering** the catalogue of searches: listing, retrieving, enabling, disabling, creating, and editing definitions

The administrative surface is what this page describes. The running surface is part of the [metaverse](metaverse.md) endpoints.

## Common workflows

**Hiding a built-in search from end users:**

1. Find the search by name in the catalogue and capture its ID
2. Update the search with the enabled flag set to false
3. The search disappears from the portal immediately on the next page load

**Re-enabling a previously hidden search:**

1. Browse the catalogue (which still includes disabled searches)
2. Update the relevant search with the enabled flag set to true

## Manage Predefined Searches

- **JIM portal**<br /> Predefined Searches area of the admin UI
- **PowerShell**<br /> [Predefined Searches cmdlets](../powershell/predefined-searches.md) (`Get-JIMPredefinedSearch`, `Set-JIMPredefinedSearch`, etc.)
- **REST API**<br /> Predefined Searches endpoints in the [interactive API reference](../api/index.md)

## See also

- [Metaverse](metaverse.md) -- predefined searches return metaverse objects; the running endpoint lives with the metaverse object endpoints
