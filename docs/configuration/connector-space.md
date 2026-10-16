---
title: Connector Space
description: "What a Connected System's Connector Space holds, how to open it, and what clearing it does."
type: object
audience: administrator
---

# Connector Space

The Connector Space is a critical concept. It is a staging area between the external system and the Metaverse: when JIM imports data from a Connected System, it does not write directly to the Metaverse. Instead, it creates or updates **Connected System Objects** in the Connector Space; the Metaverse is only updated during the explicit synchronisation phase.

--8<-- "assets/diagrams/sync-pipeline.svg"

<p class="jim-diagram-caption">Every Connected System has its own Connector Space, named for it here; imported data is staged there as Connected System Objects, the Metaverse is only touched during the synchronisation phase, and exports stage the same way in reverse. The systems shown are illustrative.<span class="jimdg-caption-motion"> Moving dots trace data through the pipeline.</span></p>

This two-stage approach gives you:

- **Isolation**<br /> Problems during import do not corrupt the Metaverse.
- **Visibility**<br /> Administrators can inspect imported data before it affects identities.
- **Comparison**<br /> JIM can detect what has changed between imports.
- **Rollback potential**<br /> The Metaverse is only updated in the sync phase.

## Opening the Connector Space

A Connected System's page carries two buttons above its tabs, each showing how much is there: **Connector Space** opens the Connected System Objects staged for this system, and **Pending Exports** opens the changes waiting to be written back to it. Both sit above the tabs rather than on one of them, so they are reachable from wherever you are on the page; the Pending Exports count is highlighted whenever changes are waiting.

The list's **State** column says where each object stands right now: **In sync**, **Update pending**, **Pending Export**, **Awaiting confirmation**, **Export not confirmed**, **Export failed**, **Delete pending** or **Obsolete**. It combines the object's status with any queued Pending Export, so an account a run could not write reads as Export failed on the row itself rather than having to be pieced together from the status and the Pending Exports page. It is the same state a Metaverse Object's [Connections tab](sync-preview.md) shows for the same object, and it is carried by `GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/connector-space` and `Get-JIMConnectedSystemObject` too.

## Connected System Objects

A **Connected System Object** is JIM's local representation of an object in an external system. Each Connected System Object holds:

- **Distinguished name or anchor**<br /> A unique identifier that maps to the external object.
- **Attributes**<br /> The attribute values as imported from the external system.
- **Link to Metaverse**<br /> If the Connected System Object has been joined or projected, it links to a Metaverse Object (Metaverse Object).
- **Pending Exports**<br /> Changes queued to be sent back to the external system.

Connected System Objects have a lifecycle:

1. **Created** during import when a new object is discovered in the external system
2. **Updated** during subsequent imports when attribute values change
3. **Joined** or **projected** during synchronisation, to link with an Metaverse Object
4. **Obsoleted** when the object no longer exists in the external system

## Objects awaiting deprovisioning

An obsoleted Connected System Object is not deleted straight away. It stays obsolete until a Synchronisation Run Profile runs on the same Connected System, which is what disconnects it from its Metaverse Object and deletes it, applying grace periods and [Metaverse Deletion Rules](metaverse.md#deletion-behaviour) as configured. On a system that is imported and synchronised on a normal schedule this is a state objects pass through in minutes.

A system that is imported but rarely synchronised is the case worth watching, because obsolete objects accumulate there with nothing acting on them. The Connected System's page states the count whenever it is above zero, with **Review** opening the Connector Space narrowed to exactly those objects; you can also reach them from the Connector Space's own **Obsolete** status filter, over the REST API with `GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/connector-space?status=Obsolete`, or with `Get-JIMConnectedSystemObject -ConnectedSystemId <id> -Status Obsolete`.

Only the first import to find an object missing records a deletion against it. Later imports that find it still missing change nothing and record nothing, because nothing has happened: the object was already obsolete when they started.

## Clearing the Connector Space

Clearing a Connected System's Connector Space removes every Connected System Object (and, unless you choose to keep it, their change history) without deleting the Connected System itself; its configuration, schema, and Synchronisation Rules survive. Clearing itself is a hard delete: no Metaverse Object Deletion Rule or grace period runs at the moment of the clear, so the removal of the Connected System Objects is immediate. The portal's Connector Space page, `POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/clear`, and [`Clear-JIMConnectedSystem`](../powershell/connected-systems.md#clear-jimconnectedsystem) all queue the same tracked background task.

Because a clear skips obsoletion, any Metaverse attribute value the cleared system contributed keeps its provenance and keeps exporting downstream even though nothing joins it to the system any more, and the identities themselves receive no Deletion Rule decision at all. For the common case, a Full Import that brings every object straight back, this is exactly what you want: the join re-forms and nothing changes. It only matters when an object never returns, for example a source population that shrank during the clear, or a system you are decommissioning without deleting: without something to notice the gap, those values and identities would be stranded indefinitely.

JIM closes that gap itself. Clearing records, inside the same transaction, which Metaverse Objects were joined at that moment, and arms the **stranded-value sweep**, but it only runs once you have genuinely rebuilt the Connector Space: the sweep executes at the first Full Synchronisation of this Connected System after a Full Import of it has completed successfully, later than the clear. A Full Synchronisation run before that leaves the sweep armed and does nothing beyond ordinary synchronisation, stating so on its Activity, because treating an empty or half-rebuilt Connector Space as a genuine departure would recall values that have not been re-imported yet.

Once the gate is open, the sweep first checks how many of the recorded objects have not rejoined. If that share is above the **Post-clear reconciliation: maximum missing share** Service Setting (`Sync.PostClearReconciliation.MaxMissingPercent`, default 10%), the sweep refuses outright: nothing is recalled, marked, or deleted, the arming stays in place, and the Activity states the counts and the remedy (re-import the rest, or raise the setting). This is what protects you from a broken re-import, such as a filter or base DN change that quietly returns a fraction of the population, being mistaken for a mass departure.

Below the threshold, the sweep proceeds in two steps. First, it finds Metaverse attribute values this system contributed with no Connected System Object of this system still joined, and recalls them exactly as an obsoletion would have: a value with a surviving contributor is re-elected to it, and a value with none is cleared. Where a Metaverse Object's only remaining connections carry no import source for its type (for example, only a provisioned target account remains), its values are preserved as last known state rather than recalled, matching the [deletion behaviour](metaverse.md#deletion-behaviour) preservation rule. Second, every recorded object that still has not rejoined is evaluated against its type's [Deletion Rule](metaverse.md#deletion-behaviour), exactly as if the cleared system had disconnected it during an ordinary synchronisation: a Manual rule does nothing, a rule with a grace period marks the object (and cancels the marking if it rejoins within the window), and a no-grace rule deletes it immediately along with its exports. Nothing here changes the rule itself; only the evaluation timing does. The sweep's outcome, whatever it is, is always stated on the Full Synchronisation run's Activity: it ran and found nothing, it ran and recalled values or applied Deletion Rules, it refused on a shortfall, or it stayed armed because the gate was still closed.

Independently of any one clear, every armed sweep (and Connected System deletion) also looks across the whole Metaverse for objects that hold no connector at all and whose type's Deletion Rule does not depend on which system left; historical strays from a clear that predates this feature, or from several disconnections over time, are given their rule too. The Pending Deletions page attributes these as "No connector remains" rather than naming a system, since none triggered the decision.

!!! tip "Watch the Connected System page while a sweep is armed"
    A notice on the Connected System page tells you whether a sweep is waiting for a Full Import, or ready for the next Full Synchronisation once one has completed. A Full Import that completes with object-level errors does not count towards readiness: an object that failed to import was never staged, and the sweep must not treat it as departed.
