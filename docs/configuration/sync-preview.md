---
title: Sync Preview
description: See what synchronising one object would do with the configuration already saved, including any destructive cascade, without changing anything.
type: object
audience: administrator
---

# Sync Preview

A **Configuration Change Preview** ([Configuration changes](configuration-changes.md#previewing-a-change-before-you-make-it)) answers "what would this proposed *edit* do?" **Sync Preview** answers a narrower, more immediate question about the configuration you already have: **"what would synchronising this object do, right now?"**

Nothing you configure changes to ask it. JIM evaluates the object against the stored Synchronisation Rules exactly as a real synchronisation would, and reports what it finds. Nothing is staged, persisted, or exported.

## Where to find it

- **Connected System Object page**: a **Preview Sync** button beside **Set Password**, opening the preview inline below the object's header.
- **Metaverse Object detail page, Connections tab**: every Connected System Object joined to the Metaverse Object is listed with a per-row **Preview Sync** action, previewing that one object's synchronisation. Each row also expands to explain why the connection exists, and a **Not connected** section below says why the object has no object in other Connected Systems; see [Why it is connected, and why it is not](metaverse-objects.md#why-it-is-connected-and-why-it-is-not).
- **Metaverse Object detail page, Connections tab, below the table**: **Preview Exports**, which previews what would export from the Metaverse Object as it stands now, with no inbound chain (see [Export-only preview](#export-only-preview-from-a-metaverse-object) below).

Each panel names the Connected System whose Full Synchronisation is being previewed, so a preview reached from a Connected System Object's Connections row is never mistaken for a preview of a different system.

## How to read it

Every statement in the preview is conditional: "would project", "would join", "would be deprovisioned from Target". Nothing has happened. If the preview surfaces a blocking error, for example an Attribute Flow that could not be evaluated, the panel says so plainly: the synchronisation it describes would fail, not succeed as shown.

A predicted join through an [Object Matching Rule](object-matching-rules.md) also says what it would rest on: the value it would match on and what the rule compares, for example `Would match on "E1007", where Employee ID equals "E" + Trim(cs["employeeId"])`.

The panel offers the same **Timeline** and **Table** views documented for a recorded [Run Profile Execution Item](activity-execution-items.md) (the Lineage view is not offered, since a preview has no recorded object graph to draw). On the Table view, the current-state and would-be columns read **Current** and **Would be** rather than **Before** and **After**.

## Which exports it shows

The preview shows the exports synchronising this object would stage, which is narrower than every difference between its Metaverse Object and each target:

- **Changes this object brings in**<br /> An export is proposed only for what this object's own Attribute Flow changes on the Metaverse Object (and for a provisioning or deprovisioning its scope causes). A value someone changed directly in another Connected System is not corrected by synchronising this one, so it is not shown.
- **Drift Corrections in this Connected System**<br /> Where an export Synchronisation Rule to this object's own Connected System has [Enforce State](synchronisation-rules.md#export-outbound) on, and the object's values have drifted from what the Metaverse Object says they should be, the preview shows the correction as **Drift would be corrected**, where the synchronisation records **Drift corrected**. This applies to target systems too, where no import rule processes the object.
- **Export scope review**<br /> If a change to an export Synchronisation Rule has flagged the object's Metaverse Object for review, the preview shows the provisioning or deprovisioning the review would make. The same goes for an obsolete object in a system an export rule provisions to: the preview shows the account the synchronisation would provision again in its place.
- **Values withdrawn when the object leaves**<br /> When the object leaves the scope of its import Synchronisation Rules and its Metaverse Object lives on, the values it contributed are withdrawn or handed to another source, and the preview shows the update that sends to each target holding them.
- **Values handed to another source**<br /> When the object stops supplying a value it holds, or the Attribute Flow that contributed it has been deleted, the attribute passes to the next source by [Attribute Priority](attribute-mappings.md#attribute-priority) or is cleared where there is none. The preview shows the value the attribute would take, anything [derived](derived-attributes.md) from it, and the update that sends to each target.

- **Queued changes the target no longer needs**<br /> Where an update is already queued for a target, but the target holds the value the Metaverse Object would now have (a value changed and changed back before an export ran, for example), synchronising withdraws the queued change instead of exporting it. The preview shows this as **Changes queued for its target Connected System would be withdrawn**, with the queued values, where the synchronisation records **Pending Export changes withdrawn**. The account keeps the value it already holds.
- **A value already waiting for an Initial Password**<br /> An Active Directory account created disabled waits, with the enabled value your Attribute Flow asked for queued, until its [Initial Password](initial-password.md) is set. A change to the person that leaves your Attribute Flow asking for that same value sends nothing new, so the preview proposes no export for it, as the synchronisation stages none. See [Enabling new Active Directory accounts](../connectors/jim-ldap-connector-passwords.md#enabling-new-active-directory-accounts).

The preview evaluates the object as a synchronisation that processes it would. A Full Synchronisation processes every object, so what is shown here happens on the next one; a Delta Synchronisation processes the object only once it has changed.

## The destructive cascade

When an object falls out of scope of every import Synchronisation Rule with Scoping Criteria, a real synchronisation does not stop at disconnecting it. Sync Preview walks the same chain:

1. **Out of scope, joined**: the object would disconnect from its Metaverse Object.
2. **Deletion Rule**: the Metaverse Object's type is put to its [Deletion Rule](metaverse.md#deletion-behaviour). Depending on the rule and whether other Connected System Objects still keep it joined, the Metaverse Object would be deleted immediately, scheduled for deletion after its grace period, or kept exactly as it stands (for example, because another system still holds a connector, or the type's rule is Manual).
3. **Downstream deprovisioning**: if the Metaverse Object would be deleted, every other Connected System Object still joined to it is evaluated in turn. One with a matching export Synchronisation Rule would be deprovisioned (deleted from its target system, or merely disconnected, per that rule's Deprovisioning Action); one with **no** matching export rule at all is disconnected and left in place, because there is nothing to tell it to do otherwise.

When the rule's Out-of-Scope Action is **Remain joined (stop Attribute Flow)** instead, there is no cascade: the preview shows a single node, **Would leave scope and keep its Metaverse Object join**, naming the rule and the Metaverse Object, with nothing beneath it, exactly as the run would record it.

!!! note "A scheduled deletion stages nothing yet"

    When the Deletion Rule's outcome is a *scheduled* deletion (a grace period applies), nothing downstream is evaluated: no target account is deprovisioned until the grace period actually elapses and the deletion happens for real. The preview reflects that: you see the Metaverse Object would be scheduled for deletion, and nothing more, which is exactly what a real synchronisation would do too.

A downstream object that would only be disconnected is not deprovisioned, and is reported as a warning naming the object and the Connected System it belongs to. Where a matching export rule's Deprovisioning Action is Disconnect, it also appears in the outcome tree as a **Disconnected from target system** node under the deletion, naming that rule, exactly as the real synchronisation records it; with no matching export rule at all there is no node, only the warning. A downstream object that would be deprovisioned names the rule whose Deprovisioning Action said Delete.

The same goes for a downstream object whose provisioning was **never exported**: JIM has staged a Create for it, but no export has run, so nothing exists in the target system. Deleting the Metaverse Object cancels that provisioning outright (the unsent Create Pending Export and the Connected System Object are both removed, and nothing is exported), whatever the export rule's Deprovisioning Action. There is nothing to deprovision, so this appears in the outcome tree as its own **Provisioning cancelled** node under the deletion, naming the Connected System the cancelled provisioning targeted, rather than as a Deprovision Queued node or a warning.

!!! warning "Provisioned targets are connectors too"

    Under the **When Last Connector Disconnected** Deletion Rule, an account JIM has provisioned to a target system counts as a connector like any other. A Metaverse Object with target accounts is therefore **not** deleted just because its source system leaves scope; the target accounts keep it alive, holding their last known values. If you want a departing source to remove those target accounts, the Metaverse Object's type needs **When Authoritative Source Disconnected** with that source listed, which is the rule that actually deprovisions targets when an authoritative source disconnects. See [Deletion behaviour](metaverse.md#deletion-behaviour) for the full explanation, including the grace period and how a reconnection can cancel a scheduled deletion.

## Export-only preview from a Metaverse Object

A Metaverse Object is never synchronised itself; its Connected System Objects are. **Preview Exports** below the Connections table therefore asks a narrower question than the Connections tab's per-object preview: given the Metaverse Object **as it stands right now**, what would export to each target Connected System? It carries no inbound chain at all (no scope, no join, no Attribute Flow), so it cannot tell you whether an inbound change is about to arrive first. Use it to check what an edit you have already made to the Metaverse Object would push outward; use the per-object preview on the Connections tab for the full inbound-then-outbound answer for one Connected System Object.

There is no single preview covering every source at once: a Full Synchronisation always belongs to one Connected System, so a preview spanning several would not correspond to any run you could actually start.

## What the preview cannot know

Sync Preview evaluates the object against data already in the Metaverse; it does not run an import first, so a source-system change that has not been imported yet is invisible to it. Nothing here carries a **Pending Export** id, either: because nothing is staged, there is nothing yet to look up on the [Pending Exports](connected-systems.md#pending-exports) list. Both are true of every preview: the panel describes what synchronising **now** would do, not what synchronising **after everything currently pending elsewhere finishes** would do.

A [generated value](generated-values.md) is checked, with the [values built from it](generated-values.md#values-built-from-a-generated-value), against JIM's own records only: the preview never contacts a Connected System. It shows the candidates the run would pass over and any value it would hold back for an input, and names the Connected Systems the run would also probe for the generated value and each value built from it. A target account JIM has not imported can still make the run choose a different value.

## REST and PowerShell

Sync Preview is available over every surface:

| Object | REST | PowerShell |
|---|---|---|
| Connected System Object | `GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/connector-space/{id}/sync-preview` | [`Get-JIMConnectedSystemObjectSyncPreview`](../powershell/sync-preview.md#get-jimconnectedsystemobjectsyncpreview) |
| Metaverse Object | `GET /api/v1/metaverse/objects/{id}/sync-preview` | [`Get-JIMMetaverseObjectSyncPreview`](../powershell/sync-preview.md#get-jimmetaverseobjectsyncpreview) |

Both require the Administrator role. Full endpoint detail is in the [interactive API reference](../../api/reference/).

## Related

- [Configuration changes](configuration-changes.md#previewing-a-change-before-you-make-it): previewing a proposed configuration *edit*, rather than what today's stored configuration would do
- [Synchronisation Pipeline](../concepts/synchronisation-pipeline.md): what import, sync and export actually do when they run for real
- [Deletion behaviour](metaverse.md#deletion-behaviour): Deletion Rules, grace periods, and provisioned targets as connectors
- [Deprovisioning Action](deprovisioning.md): what happens to a downstream target object when its Metaverse Object is deleted
- [Sync Preview cmdlets](../powershell/sync-preview.md): `Get-JIMConnectedSystemObjectSyncPreview` and `Get-JIMMetaverseObjectSyncPreview`
