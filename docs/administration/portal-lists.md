---
title: Working with Lists
description: How the portal's lists behave: continuous scrolling, object counts, search, sorting, and shareable links into a position in a list.
---

# Working with Lists

Most of what you look at in the JIM portal is a list: Metaverse Objects, the Connector Space, Pending Exports, Activities, Synchronisation Rules, Connected Systems, and the rest. They all behave the same way, so what you learn on one applies to every other.

## Scrolling, not paging

Lists have no page size and no page controls. Rows load as you scroll towards them, so a list of eight objects and a list of eight hundred thousand are read the same way: scroll until you find what you want, or narrow the list first.

Only the rows you can see (and a screen or so beyond them) are ever fetched, so opening a very large list costs no more than opening a small one.

A list also takes only the space it needs. A short list collapses to its rows and reads as an ordinary table; a long one fills the page down to the footer.

## How many objects am I looking at

Every list states its size beside its search box:

- **`3,868 Metaverse Objects`**<br /> Nothing is narrowing the list; that is everything JIM holds for that view.
- **`12 of 3,868 Metaverse Objects`**<br /> A search or filter is active, and it is hiding the rest. Clear the search to get back to the full list.

The count is the true total for the whole list, not the number of rows currently rendered.

## Narrowing a list

Type in the search box at the top right of any list to narrow it. Search runs on the server against the whole list, not against the rows on screen, and the count updates to tell you how much your search matched.

Some lists carry filters of their own above the table (Activities can be filtered by status, type and initiator, for example). Those combine with the search box.

Click a column heading to sort by it, and click it again to reverse the direction. Sorting, like searching, is applied across the whole list rather than to the rows on screen, so the first row really is the first row.

The compact-rows toggle at the top left of the table switches between comfortable and dense row heights. Your choice is remembered and applied to every list.

## Sharing where you are

A list keeps its search, its sort and your position in the address bar. Copy the URL and send it to a colleague, or bookmark it, and it opens where you left it rather than at the top of an unsorted list.

Note that the browser's back button does not step backwards through searches and sorts within a single list; those are ways of looking at one page, not separate pages.

## Empty lists

A list with nothing in it says which of the two reasons applies. If your search matched nothing, it says so and offers to clear the search. If the list is genuinely empty, it says what would put objects in it and, where there is one obvious next step (creating your first Connected System, for instance), offers it.

## Tables inside a page

The same behaviour applies to the tables that live inside a page, not just the lists you navigate to. Nothing in the portal pages any more:

- An Activity's Run Profile execution items, and its child Activities
- An API Key's usage history
- A Connected System Object's attributes, and the Pending Exports queued against it
- A Pending Export's Attribute Changes
- The values of a multi-valued attribute on a Metaverse Object or a Connected System Object
- The changes behind a Change History entry, and a causality event's attribute changes
- A Connected System's schema, Run Profiles, Object Matching Rules and Attribute Flows
- A Metaverse Object Type's attributes and its downstream deprovisioning
- Service Settings, Example Data Templates, and all three Operations tabs

This matters most for group membership: a group with half a million members is read by scrolling it, and narrowed by typing into its search box, rather than by working through pages ten values at a time. The same goes for an Activity that recorded a million execution items.

Two things moved rather than converted, because a scrolling table could not hold them. A Metaverse Attribute's contributors reorder by dragging, which needs a row that grows, so they now open in a dialog from the contributor count. A Schedule's recent executions likewise open from its **History** action, where they show the Schedule's whole history rather than its last five.

## One line per row

Every row of a scrolling table is one line tall, which is what lets the table place a row without having drawn the rows above it. Three things follow from that, and none of them loses you anything.

**A cell holding a list shows its first item and a +n more beside it.** This covers a multi-valued attribute's values (a Connected System Object's attributes, a Pending Export's Attribute Changes), an API Key's roles, the Object Types a Metaverse Attribute is bound to, the sources of an Attribute Flow, and an Example Data Template Attribute's generation rules. **+n more** opens the whole set in a dialog, drawn exactly as it is in the row, so no item is out of reach however many there are.

**Long text is clipped with an ellipsis rather than wrapped.** Descriptions, service log messages and imported schema descriptions are all as long as whatever wrote them chose to make them. Hover the text to read it in full; where there is a detail page or panel behind the row, as there is for a log entry, that holds the complete, selectable version.

**Secondary text reads after the value rather than under it.** A setting's description, an attribute's data type and plurality, and the pattern behind a previewed change now follow what they qualify on the same line, low-lighted. Hovering shows both in full.

## The Operations queue

The queue is a table per block rather than one long table: each Schedule Execution's steps scroll within their own, underneath the header that names the Schedule, draws its rail and carries **View execution** and the option to cancel it. Everything running outside a Schedule scrolls in a table of its own at the end. Each table states how many Worker Tasks it holds, and is bounded in height, so a Schedule with twenty steps in it cannot push the rest of the queue off the screen; click a Schedule's header to put its steps away entirely.

There is no search box on these, because a search over one Schedule's handful of steps says nothing a glance does not.

## Automation

Continuous scrolling is a portal behaviour only. The REST API and the PowerShell module are paged, and are unchanged: see [Rate Limiting](../api/rate-limiting.md) for the API's retrieval limits, and use `-All` on the paginated `Get-JIM*` cmdlets to page through a full result set.
