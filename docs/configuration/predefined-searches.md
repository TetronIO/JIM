---
title: Predefined Searches
---

# Predefined Searches

A **predefined search** is a named, reusable search over [metaverse](metaverse.md) objects. Predefined searches drive the portal's list views and back the fast named-search endpoint that end-user search consumers call.

Predefined searches are administrator-managed: end users do not author new searches, they consume the ones that already exist.

## What a predefined search defines

A predefined search ties together:

- **A target Metaverse Object Type**<br /> The object type whose objects this search returns.
- **A set of attributes**<br /> The fields surfaced as columns in the result list, in display order.
- **Criteria**<br /> Conditions on attribute values that control which objects match (see [Filtering with criteria](#filtering-with-criteria) below).
- **A stable URI slug**<br /> The human-readable identifier (e.g. `people`, `security-groups`) that consuming code uses to invoke the search.

## Stable URI slug

Each search has a URI that is what consumers refer to. Treat the URI as a stable identifier; even though the underlying integer ID is the canonical key for write operations, the URI is what end-user code, the portal, and integrations will call by name. Picking a sensible URI when you create a search matters more than picking a name.

## Built-in vs administrator-defined

Searches that ship with JIM are marked **built-in** and cannot be deleted; they can however be enabled, disabled, and customised in some cases. Administrator-defined searches are first-class and can be created, edited, and removed.

## Default for object type

Each Metaverse Object Type may have one default predefined search. The default is what the portal uses when navigating to that object type without a specific search selection.

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

## Filtering with criteria

A predefined search can filter its results with **criteria**: conditions tested against a Metaverse Object's attribute values. Criteria live in **criteria groups**; a search can have one or more groups, and each group holds one or more criteria.

Each criterion combines:

- **An attribute**<br /> Any attribute of the search's Metaverse Object Type.
- **A comparison operator**<br /> The operators offered depend on the attribute's data type (see below).
- **A value**<br /> Typed to match the attribute (a number for a Number attribute, a date for a DateTime attribute, and so on).

### Operators by attribute type

| Attribute type | Operators |
|----------------|-----------|
| Text | equals, does not equal, starts with, does not start with, ends with, does not end with, contains, does not contain |
| Number, Long Number, Date/Time | equals, does not equal, less than, less than or equal to, greater than, greater than or equal to |
| Boolean, GUID | equals, does not equal |

For **Date/Time** attributes the editor shows the operators in calendar wording: *before*, *on or before*, *after*, *on or after*, *equals*, *does not equal*. Date/Time values are stored and compared in UTC.

A Date/Time criterion can compare against either a fixed date (**Absolute**) or a date worked out **Relative** to now (a count, a unit of Hours/Days/Weeks/Months/Years, and a direction of Ago or From now). Relative criteria are re-evaluated every time the search runs, so "expiring within the next 7 days" keeps moving with time. Days and coarser units round to midnight UTC; Hours keeps exact-instant precision; month and year offsets are calendar-correct. See [relative dates in scope filters](synchronisation-rules.md#relative-dates-in-scope-filters) for the shared behaviour.

**Text comparisons are case-sensitive by default.** Switch a text criterion to case-insensitive when you want, for example, `Finance` and `finance` to match the same value.

### How criteria combine

Each group has a **logic type**:

- **All (AND)**<br /> the group matches only when every criterion (and nested child group) in it matches.
- **Any (OR)**<br /> the group matches when at least one criterion (or nested child group) in it matches.

**Top-level groups are combined with OR**: an object matches the search when it matches any one of the top-level groups. A search with no criteria returns every object of its Metaverse Object Type, and an empty group matches everything.

**Nested groups** let you express mixed logic. For example, "in Finance or Sales, and active" is a top-level **All** group containing the `IsActive = true` criterion and a child **Any** group containing `Department = Finance` and `Department = Sales`, giving `(Department = Finance OR Department = Sales) AND IsActive = true`. (Nesting is supported one level deep, which covers these mixed-logic expressions.)

### Editing criteria

On the Predefined Search detail page in the portal, the **Criteria** panel lets you add a criteria group, then add criteria to it, each with the attribute, operator and value controls above. Within a group you can also add a nested child group (with its own All / Any logic). Removing a group removes everything within it. The same operations are available through the [PowerShell cmdlets](../powershell/predefined-searches.md) and the REST API.

## Change history

Changes to a Predefined Search, including its criteria groups and criteria, are tracked as versioned [configuration change history](activities.md#configuration-change-history): who changed what, and when. A **Changes** tab on the Predefined Search detail page shows each change as an audit-style field history, and lets you compare any two versions.

Retrieve the same history with `Get-JIMConfigurationChangeHistory -Type PredefinedSearch` or the equivalent `change-history` endpoint in the [interactive API reference](../../api/reference/). Enabling or disabling a search from the admin portal offers an optional "Reason for change" prompt; automation can pass the same reason with `-ChangeReason` on `Set-JIMPredefinedSearch` or the corresponding REST request field. Adding, editing, or removing a criteria group or criterion is captured as its own version rolled up into the owning search's history, without prompting for a reason on each edit.

## Manage Predefined Searches

- **JIM portal**<br /> Predefined Searches area of the admin UI
- **PowerShell**<br /> [Predefined Searches cmdlets](../powershell/predefined-searches.md) (`Get-JIMPredefinedSearch`, `Set-JIMPredefinedSearch`, and the criteria cmdlets such as `New-JIMPredefinedSearchCriterion`)
- **REST API**<br /> Predefined Searches endpoints in the [interactive API reference](../../api/reference/), including the criteria-group and criteria endpoints

## See also

- [Metaverse](metaverse.md) -- predefined searches return Metaverse Objects; the running endpoint lives with the Metaverse Object endpoints
