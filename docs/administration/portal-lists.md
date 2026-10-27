---
title: Working with Lists
description: "How the portal's lists behave: continuous scrolling, object counts, search, sorting, and shareable links into a position in a list."
type: concept
audience: administrator
---

# Working with Lists

Most of what you look at in the JIM portal is a list: Metaverse Objects, the Connector Space, Pending Exports, Activities, Synchronisation Rules, Connected Systems, and the rest. They all behave the same way, so what you learn on one applies to every other.

```jim-diagram
archetype: anatomy
title: What every portal list is made of
desc: "Every portal list has a search box, which matches against the whole list on the server; a count, which reads 12 of 3,868 when a search or filter is narrowing the list; column headings, which sort the whole list and reverse on a second click; rows, which load as you scroll, with no pages; a compact-rows toggle, dense or comfortable, which is remembered; and an address that keeps the search, the sort and your position, so a copied link opens where you left it. An empty list says why it is empty. The REST API and the PowerShell module are paged instead."
subject:
  kind: object
  label: A portal list
  parts:
  - {label: Search box, detail: 'searches the whole list, on the server'}
  - {label: Count, detail: '`12 of 3,868`: a search or filter is on'}
  - {label: Column headings, detail: 'sort the whole list; again to reverse'}
  - {label: Rows, detail: 'load as you scroll; no pages'}
  - {label: Compact-rows toggle, detail: 'dense or comfortable; remembered'}
  - {label: Address, detail: 'keeps search, sort and position'}
properties:
- {name: Empty list, value: says why}
- {name: REST and PowerShell, value: paged}
```

## :material-arrow-expand-vertical: Scrolling, not paging

Lists have no page size and no page controls. Rows load as you scroll towards them, so a list of eight objects and a list of eight hundred thousand are read the same way: scroll until you find what you want, or narrow the list first.

Only the rows you can see (and a screen or so beyond them) are ever fetched, so opening a very large list costs no more than opening a small one.

A list also takes only the space it needs. A short list collapses to its rows and reads as an ordinary table; a long one fills the page down to the footer.

## :material-counter: How many objects am I looking at

Every list states its size beside its search box:

- **`3,868 Metaverse Objects`**<br /> Nothing is narrowing the list; that is everything JIM holds for that view.
- **`12 of 3,868 Metaverse Objects`**<br /> A search or filter is active, and it is hiding the rest. Clear the search to get back to the full list.

The count is the true total for the whole list, not the number of rows currently rendered.

## :material-filter-outline: Narrowing a list

Type in the search box at the top right of any list to narrow it. Search runs on the server against the whole list, not against the rows on screen, and the count updates to tell you how much your search matched. Every character you type is matched as itself, including `%`, `_` and `\`, so searching for `svc_` does not also find `svcX`.

Some lists carry filters of their own above the table (Activities can be filtered by status, type and initiator, for example). Those combine with the search box.

Click a column heading to sort by it, and click it again to reverse the direction. Sorting, like searching, is applied across the whole list rather than to the rows on screen, so the first row really is the first row.

Lists open with dense rows, so more of them fit on screen. The compact-rows toggle at the top left of the table switches to comfortable row heights and back; your choice is remembered and applied to every list.

The Connector Space list has a **Columns** menu beside that toggle, for showing or hiding its two identifier columns, External ID and Secondary External ID. Which one you read depends on the Connector (for an LDAP directory it is usually the distinguished name held as the Secondary External ID), so the choice is remembered separately for each Connected System. Hiding a column does not change what the search box matches: it still searches both identifiers. Like row density, the choice is kept in your browser, so another browser or computer starts with both columns shown.

## :material-link-variant: Sharing where you are

A list keeps its search, its sort and your position in the address bar. Copy the URL and send it to a colleague, or bookmark it, and it opens where you left it rather than at the top of an unsorted list.

Note that the browser's back button does not step backwards through searches and sorts within a single list; those are ways of looking at one page, not separate pages.

## :material-tray-remove: Empty lists

A list with nothing in it says which of the two reasons applies. If your search matched nothing, it says so and offers to clear the search. If the list is genuinely empty, it says what would put objects in it and, where there is one obvious next step (creating your first Connected System, for instance), offers it.

## :material-table-large: Tables inside a page

The same behaviour applies to the tables that live inside a page, not just the lists you navigate to. Nothing in the portal pages any more:

- An Activity's Run Profile execution items, and its child Activities
- An API Key's usage history
- The Pending Exports queued against a Connected System Object
- The values of a large multi-valued attribute on a Metaverse Object, a Connected System Object or a Pending Export (see [Attribute tables](#attribute-tables))
- The changes behind a Change History entry, and a causality event's attribute changes
- A Connected System's schema, Run Profiles, Object Matching Rules and Attribute Flows
- A Metaverse Object Type's attributes and its downstream deprovisioning
- Service Settings, Example Data Templates, and all three Operations tabs

This matters most for group membership: a group with half a million members is read by scrolling it, and narrowed by typing into its search box, rather than by working through pages ten values at a time. The same goes for an Activity that recorded a million execution items.

Two things moved rather than converted, because a scrolling table could not hold them. A Metaverse Attribute's contributors reorder by dragging, which needs a row that grows, so they now open in a dialog from the contributor count. A Schedule's recent executions likewise open from its **History** action, where they show the Schedule's whole history rather than its last five.

## :material-format-list-bulleted: One line per row

Every row of a scrolling table is one line tall, which is what lets the table place a row without having drawn the rows above it. Three things follow from that, and none of them loses you anything.

**A cell holding a list shows its first item and a +n more beside it.** This covers an API Key's roles, the Object Types a Metaverse Attribute is bound to, the sources of an Attribute Flow, and an Example Data Template Attribute's generation rules. **+n more** opens the whole set in a dialog, drawn exactly as it is in the row, so no item is out of reach however many there are.

**Long text is clipped with an ellipsis rather than wrapped.** Descriptions, service log messages and imported schema descriptions are all as long as whatever wrote them chose to make them. Hover the text to read it in full; where there is a detail page or panel behind the row, as there is for a log entry, that holds the complete, selectable version.

**Secondary text reads after the value rather than under it.** A setting's description, an attribute's data type and plurality, and the pattern behind a previewed change now follow what they qualify on the same line, low-lighted. Hovering shows both in full.

## :material-table-eye: Attribute tables

The table of one object's attributes is the exception to one line per row: a Connected System Object's **Attributes** tab, a Pending Export's **Attribute Changes**, and a Metaverse Object's **Table** view. An object has only as many attributes as its schema gives it, so these tables draw every row, and a row grows to hold a multi-valued attribute's values:

- **Up to 10 values** are listed in the row, one per line. On a Pending Export, each line also shows that value's change type and status, since adding one member and removing another are different changes.
- **More than 10 values** (a large group's members, say) are shown in a small table inside the row, which scrolls and has its own search box, so a group with half a million members is still read and searched in place.

All three tables have a search box, which matches an attribute's name or any of its values shown on the page, and column headers you can click to sort. The search and sort are kept in the page address, so a refresh or a shared link shows the same view.

## :material-tray-full: The Operations Activity tab

Of the Activity tab's three sections, only **Completed** is a scrolling list: it holds everything the Worker has ever finished. **Queued** and **In Progress** are ordinary tables, because they only ever hold work in flight. Queued shows as many rows as fit the window while In Progress stays in view, keeping the rows nearest In Progress (the work about to run), and never fewer than three; **Show more** reveals the rest. A Schedule Execution's tasks sit under a header row that names the Schedule, draws its rail and carries **View execution** and the option to cancel it.

There is no search box on Queued or In Progress, because the filters above the tab already narrow them, and a search over a handful of rows says nothing a glance does not.

## :material-cog-play-outline: Automation

Continuous scrolling is a portal behaviour only. The REST API and the PowerShell module are paged, and are unchanged: see [Rate Limiting](../api/rate-limiting.md) for the API's retrieval limits, and use `-All` on the paginated `Get-JIM*` cmdlets to page through a full result set.
