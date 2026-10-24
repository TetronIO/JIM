---
title: Causality
description: "How the Lineage explains what caused a change and what it went on to cause, and how the Causality Explorer follows one story across objects."
type: concept
audience: operator
---

# Causality

Every execution item's Lineage reads beyond the run it records, in both directions: up the chain to why the change happened, and down it to what the change went on to cause in later runs. Together they answer the two questions an administrator investigating an account usually has, "why did this happen?" and "what did it lead to?", from whichever item they opened, without reconstructing the story from several Activities by hand.

## Why it happened

The Lineage's subdued cards are the item's causal chain: why this happened at all, why that happened, and so on back up the chain as far as JIM recorded it, each cause placed on the column of the object it happened to rather than listed separately.

Among those causes, the chain also states the Metaverse Object's own creation wherever it recorded one: that the Metaverse Object was projected, joined to an existing one, or created directly, on whichever run actually decided it, even where that run is further back in the chain than the item you are viewing. Without this the Metaverse Object column could sit empty even though the fact was known, because the run that created it was not the one you opened.

Each cause is a sentence rather than a diagram, for example "10 Users were deleted, so they were removed from Project Diamond's Static Members", with the relationship the cascade acted through picked out in colour. Objects removed for the same reason, on the same Connected System, through the same Synchronisation Rule read as one card carrying a count instead of as ten near-identical cards; expanding it in place names each of them individually. Two independent causes converging on one effect stay as two, because a hidden second cause is precisely what an administrator needs to see.

Where JIM recorded why the cause itself happened, that follows as a second sentence, led by the Connected System responsible: "Yellowstone APAC was the last authoritative source to disconnect, so the Deletion Rule deleted them".

Everything a cause says was captured at the moment it happened: the causing object's name, its object type, the attribute, the Connected System and the Synchronisation Rule. A cause is by definition older than its effect, so the objects a chain names have very often been deleted, renamed, or aged out of history by the time anyone reads it; recording the wording alongside the link is what lets the chain still read correctly when they have. A deleted Metaverse Object still gets its column, named from those snapshots.

The chain always says why it ends, because the three reasons mean entirely different things. It is stated at the top of the object it closes, above that object's oldest card: an object's cards read downwards from oldest to newest, so what lies behind the earliest of them belongs at the beginning of that order rather than the end.

- **No earlier causes recorded**: nothing caused this. It is the whole story.
- **What caused this is no longer retained**: the causing object has aged out of Activity retention. This is expected rather than exceptional on a deployment that has been live longer than one retention window, and is shown calmly rather than as an error; the cause itself is still named, from the wording recorded at the time.
- **More causes exist beyond this point**: the walk stopped at its depth bound, not at a real end. A chain that hit the bound anywhere says so above the canvas as well.

A cause that was recorded on a different execution item links to it, so a long chain can be walked one page at a time. A cause recorded on the item you are already looking at is shown without a link.

### Why an export happened

An export run is the common case of a run whose reason lies elsewhere. It holds a queue of changes to make and nothing more; the synchronisation that decided on a given change ran in a different Activity, often days earlier. The same recorded decision is what lets an export outcome state what it actually did, **Connected System Object created**, **Changes applied** or **Connected System Object deleted**, rather than a bare "Exported". An export item leads with what that synchronisation decided:

- A provisioning create reads "Mia Young was provisioned to Glitterband EMEA, so this run created the Connected System Object", with the Synchronisation Rule that made the provisioning decision beside it.
- An update reads "Mia Young's Metaverse Object changed, so this run applied the changes to the Connected System Object". Where a later synchronisation added its change to an export still waiting, the export carries both and names both: "Mia Young's Metaverse Object changed in 2 synchronisations", and each of them continues to the export in its own **After this run**.
- A deprovision reads "The Metaverse Object Tina Adams was deleted, so this run deleted the Connected System Object", and continues through the Deletion Rule decision that ordered it.

A confirming import is the same story read from the other end. When an import finds a change JIM had exported, its **Export confirmed** outcome continues into the export that made it, and from there into the synchronisation that staged it and the import behind that: a deprovisioning confirmation runs all the way back to the moment the source Connected System Object disappeared. JIM pairs the confirmation to its export by the queued change they both name, rather than by the Connected System Object, because a Connected System Object cycles through export and import repeatedly and matching on it alone could pair a confirmation with the wrong cycle.

Each hop links to the run that staged the change, whose own causes continue above it. Behind any synchronisation, the chain then follows the Connected System Object's own history to the import that fed it: "Mia Young was imported into Yellowstone APAC as a new Connected System Object" is the true root of most stories, and a deleted account's chain runs back to the moment the source Connected System Object disappeared. An export staged by Drift Correction names the Drift Correction that staged it, so an export putting a value back reads as the enforcement it is rather than as an ordinary update.

The queueing link is the one thing JIM cannot work out after the event, because a queued change is deleted the moment its export succeeds. It is recorded as the change is staged, so changes queued **before** you upgraded to a version carrying this show a shorter chain; once they have exported, every later export is covered. The import hop is the opposite: it is read from the Connected System Object's own history at viewing time, so it appears only while that history is still reachable, and a chain whose imports have aged out of Activity retention, or whose Connected System Object has since been removed from the Connector Space, ends at the synchronisation instead. A fully deleted account's chain therefore ends at the deletion decision, which carries the Deletion Rule context that ordered it.

## What it went on to cause

Below each object's events, the Lineage carries on past the run under an **After this run** rule: what later runs did to that object, oldest first. An import shows the Synchronisation that processed it; a Synchronisation shows the exports it queued and the import that confirmed them. Each card names the kind of run that recorded it and when, and links that run's own execution item, so the next step can be opened directly.

Objects the run itself never touched join the story where its consequences reached them. Deleting a Metaverse Object, for example, adds a panel for the account deprovisioned in each target Connected System and another for the Groups that referenced the deleted object. They are separate objects even when they share a Connected System, and each keeps its own story down to the import that confirmed it.

Objects that the same change reached for the same reason read as one card carrying an exact count ("Removed Tina Adams from Static Members on 4,218 Groups"). The count is exact however large the cohort is. Expanding the card lists its members 50 at a time with **Show 50 more**, and a cohort of more than 10 offers a find box to narrow the list by name.

Where a configured setting decided an outcome, its card names that setting: a deprovision card names the [Deprovisioning Action](deprovisioning.md) that deleted the account or left it in place, and the card for a deleted Metaverse Object names its Deletion Rule and what the grace period meant. Those are the settings to change for a different result next time. When the item that deleted a Metaverse Object has since aged out of Activity retention, a note beneath its events still says it was deleted after this run and links its deletion record.

### Where each branch ends

Every branch says why it stops, so an object with nothing after this run never has to be read as "nothing will happen":

- **Confirmed by the next import**: an export landed, and the next import found it in place. Folded to one line, since it changes nothing; **Show** opens it.
- **Waiting for the next Synchronisation**: the import has not been synchronised yet, so nothing has acted on the change.
- **Nothing followed**: a Synchronisation has run since the import and changed nothing as a result. This is a complete answer.
- **Waiting for the next Export**: the change is queued as a Pending Export, which the card links.
- **Waiting for a retry**: an export was not confirmed and will be retried on the next Export of its Connected System.
- **Needs attention**: a Pending Export was parked. Nothing more happens until someone acts on it, so it is not counted as waiting. A Pending Export that is failing is **Waiting for the next Export**, since the next export run attempts it again; its own page says it is failing.
- **Waiting for the next import**: the change was exported and is waiting for an import to confirm it.
- **Deletion scheduled**: the Metaverse Object's grace period is running, with the date it ends.
- **This goes further than shown**: the walk stopped at its depth bound or at the number of objects it reads. Open the last item in the branch to carry on from there.

An item whose kind causes nothing, such as an import that only confirmed an export, has no After this run section at all.

### A reading, checked on demand

Consequences can still be happening, so the Lineage shows a reading taken when the page loaded. A line beneath the canvas says whether every branch has run to its end or which ones are waiting, and when it was checked. **Check again** appears only while a branch is waiting on something that will happen without anyone acting; nothing refreshes by itself.

The summary band adds an **After this run** strip of what followed, separate from the run's own outcomes, and it shows in every view. Choosing a pill opens the Lineage, which is the one view that shows consequences; the Timeline and Table describe the run alone.

## The Causality Explorer

The Lineage answers for one execution item. The Causality Explorer answers for an object, and lets you follow a story from object to run to object without losing your place. Open it from wherever the question starts:

- **Explore causality** on a Connected System Object or a Metaverse Object opens that object's causal timeline.
- **Open in explorer** in an execution item's causality panel opens the item's Lineage in the explorer.
- **Queued by** on a Pending Export lists the synchronisation runs that queued the change, each with **Explore** beside it.

An object's timeline lists every execution item about it, newest first: when the run happened, what kind of run it was, what it did, and **Because**, the item's nearest cause in one line ("Tina Adams was deleted, so they were removed from Project Diamond's Static Members, and 2 more causes"). Where nothing earlier was recorded, the line says so, and it says differently where the cause has aged out of history, so a story cut short never reads as complete. Long timelines load 50 items at a time with **Show 50 more**. Choosing a row opens that item's Lineage, and any object or item named in a Lineage opens in turn, so the explorer re-centres on whatever you choose.

Each re-centring adds a step to the **Trail** above the page. Choosing an earlier step goes back to it, as does the browser's back button. The page's address carries the focus and its trail, so a link to it opens the same view. Where a walk stops at its depth bound on a single item, **Continue from** carries on from that item rather than leaving you to find it.

The trail and re-centring belong to the portal. The REST API and PowerShell read the same timelines and the same runs, and a script follows a story by passing each row's execution item to the causes and consequences reads instead (see [PowerShell: Execution Items](../powershell/execution-items.md)).

A deleted object stays explorable: its timeline is read through the records its deletion kept, and the focus header says when it was deleted, and **View deletion record** opens what its deletion kept. A Metaverse Object's timeline on a deployment that does not record Metaverse Object changes holds only what its joined Connected System Objects recorded, and says so above the list.

**Queued by** answers the question an export raises on its own: why is this change waiting? A Pending Export can be queued by several synchronisations before it exports, and each is listed with its own nearest cause. A Pending Export queued before your deployment recorded its queueing runs says that no run is recorded rather than showing an empty list, and runs whose records retention has removed are counted rather than listed.

## Related

- [Activities](activities.md): execution items and the causality panel's views.
- [PowerShell: Execution Items](../powershell/execution-items.md): `Get-JIMActivityExecutionItem -Causes` and `-Consequences`, and `Get-JIMCausalCohortMember`, which return the same chain one row per cohort; and `-ConnectedSystemObjectId` or `-MetaverseObjectId`, which return an object's causal timeline.
- [PowerShell: Pending Exports](../powershell/pending-exports.md): `Get-JIMPendingExport -QueuedBy`, the runs that queued a Pending Export.
- [Deprovisioning Action](deprovisioning.md) and [grace period](metaverse.md#grace-period): the settings the deletion cards name.
