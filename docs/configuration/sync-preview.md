---
title: Sync Preview
---

# Sync Preview

A **Configuration Change Preview** ([Configuration changes](configuration-changes.md#previewing-a-change-before-you-make-it)) answers "what would this proposed *edit* do?" **Sync Preview** answers a narrower, more immediate question about the configuration you already have: **"what would synchronising this object do, right now?"**

Nothing you configure changes to ask it. JIM evaluates the object against the stored Synchronisation Rules exactly as a real synchronisation would, and reports what it finds. Nothing is staged, persisted, or exported.

## Where to find it

- **Connected System Object page**: a **Preview Sync** button beside **Set Password**, opening the preview inline below the object's header.
- **Metaverse Object detail page, Connections tab**: every Connected System Object joined to the Metaverse Object is listed with a per-row **Preview Sync** action, previewing that one object's synchronisation.
- **Metaverse Object detail page, Connections tab, below the table**: **Preview Outbound Synchronisation**, which previews what would export from the Metaverse Object as it stands now, with no inbound chain (see [Outbound-only preview](#outbound-only-preview-from-a-metaverse-object) below).

Each panel names the Connected System whose Full Synchronisation is being previewed, so a preview reached from a Connected System Object's Connections row is never mistaken for a preview of a different system.

## How to read it

Every statement in the preview is conditional: "would project", "would join", "would be deprovisioned from Target". Nothing has happened. If the preview surfaces a blocking error, for example an Attribute Flow that could not be evaluated, the panel says so plainly: the synchronisation it describes would fail, not succeed as shown.

## The destructive cascade

When an object falls out of scope of every import Synchronisation Rule with Scoping Criteria, a real synchronisation does not stop at disconnecting it. Sync Preview walks the same chain:

1. **Out of scope, joined**: the object would disconnect from its Metaverse Object.
2. **Deletion Rule**: the Metaverse Object's type is put to its [Deletion Rule](metaverse.md#deletion-behaviour). Depending on the rule and whether other Connected System Objects still keep it joined, the Metaverse Object would be deleted immediately, scheduled for deletion after its grace period, or kept exactly as it stands (for example, because another system still holds a connector, or the type's rule is Manual).
3. **Downstream deprovisioning**: if the Metaverse Object would be deleted, every other Connected System Object still joined to it is evaluated in turn. One with a matching export Synchronisation Rule would be deprovisioned (deleted from its target system, or merely disconnected, per that rule's Deprovisioning Action); one with **no** matching export rule at all is disconnected and left in place, because there is nothing to tell it to do otherwise.

!!! note "A scheduled deletion stages nothing yet"

    When the Deletion Rule's outcome is a *scheduled* deletion (a grace period applies), nothing downstream is evaluated: no target account is deprovisioned until the grace period actually elapses and the deletion happens for real. The preview reflects that: you see the Metaverse Object would be scheduled for deletion, and nothing more, which is exactly what a real synchronisation would do too.

A downstream object that would only be disconnected (no matching export rule, or a matching rule whose Deprovisioning Action is Disconnect) is not deprovisioned, so it does not appear as its own node in the outcome tree; it is reported as a warning instead, naming the object and the Connected System it belongs to.

!!! warning "Provisioned targets are connectors too"

    Under the **When Last Connector Disconnected** Deletion Rule, an account JIM has provisioned to a target system counts as a connector like any other. A Metaverse Object with target accounts is therefore **not** deleted just because its source system leaves scope; the target accounts keep it alive, holding their last known values. If you want a departing source to remove those target accounts, the Metaverse Object's type needs **When Authoritative Source Disconnected** with that source listed, which is the rule that actually deprovisions targets when an authoritative source disconnects. See [Deletion behaviour](metaverse.md#deletion-behaviour) for the full explanation, including the grace period and how a reconnection can cancel a scheduled deletion.

## Outbound-only preview from a Metaverse Object

A Metaverse Object is never synchronised itself; its Connected System Objects are. **Preview Outbound Synchronisation** below the Connections table therefore asks a narrower question than the Connections tab's per-object preview: given the Metaverse Object **as it stands right now**, what would export to each target Connected System? It carries no inbound chain at all (no scope, no join, no Attribute Flow), so it cannot tell you whether an inbound change is about to arrive first. Use it to check what an edit you have already made to the Metaverse Object would push outward; use the per-object preview on the Connections tab for the full inbound-then-outbound answer for one Connected System Object.

There is no single preview covering every source at once: a Full Synchronisation always belongs to one Connected System, so a preview spanning several would not correspond to any run you could actually start.

## What the preview cannot know

Sync Preview evaluates the object against data already in the metaverse; it does not run an import first, so a source-system change that has not been imported yet is invisible to it. Nothing here carries a **Pending Export** id, either: because nothing is staged, there is nothing yet to look up on the [Pending Exports](connected-systems.md#pending-exports) list. Both are true of every preview: the panel describes what synchronising **now** would do, not what synchronising **after everything currently pending elsewhere finishes** would do.

## REST and PowerShell

Sync Preview is available over every surface:

| Object | REST | PowerShell |
|---|---|---|
| Connected System Object | `GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/connector-space/{id}/sync-preview` | [`Get-JIMConnectedSystemObjectSyncPreview`](../powershell/previews.md#get-jimconnectedsystemobjectsyncpreview) |
| Metaverse Object | `GET /api/v1/metaverse/objects/{id}/sync-preview` | [`Get-JIMMetaverseObjectSyncPreview`](../powershell/previews.md#get-jimmetaverseobjectsyncpreview) |

Both require the Administrator role. Full endpoint detail is in the [interactive API reference](../../api/reference/).

## See also

- [Configuration changes](configuration-changes.md#previewing-a-change-before-you-make-it) -- previewing a proposed configuration *edit*, rather than what today's stored configuration would do
- [Sync Pipeline](../concepts/synchronisation-pipeline.md) -- what import, sync and export actually do when they run for real
- [Deletion behaviour](metaverse.md#deletion-behaviour) -- Deletion Rules, grace periods, and provisioned targets as connectors
- [Deprovisioning Action](synchronisation-rules.md#deprovisioning-action) -- what happens to a downstream target object when its Metaverse Object is deleted
- [Preview cmdlets](../powershell/previews.md) -- `Get-JIMConnectedSystemObjectSyncPreview` and `Get-JIMMetaverseObjectSyncPreview`
