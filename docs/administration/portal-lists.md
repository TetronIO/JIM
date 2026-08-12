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

The same behaviour applies to the tables that live inside a page, not just the lists you navigate to. The values of a multi-valued attribute on a Metaverse Object or a Connected System Object, the queued changes behind a Pending Export's multi-valued attribute, and the attribute changes shown for a causality event all scroll continuously, carry the same search box and count, and have no page controls.

This matters most for group membership: a group with half a million members is read by scrolling it, and narrowed by typing into its search box, rather than by working through pages ten values at a time.

Where a multi-valued attribute appears as one row of a scrolling table (a Connected System Object's attributes, a Pending Export's Attribute Changes), the row shows the attribute's first value and a **+n more** beside it. Every row of a scrolling table is one line tall, which is what lets the table place a row without having drawn the rows above it; **+n more** opens the whole set in a dialog, which scrolls in exactly the same way, so no value is out of reach however many there are.

## Automation

Continuous scrolling is a portal behaviour only. The REST API and the PowerShell module are paged, and are unchanged: see [Rate Limiting](../api/rate-limiting.md) for the API's retrieval limits, and use `-All` on the paginated `Get-JIM*` cmdlets to page through a full result set.
