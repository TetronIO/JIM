---
title: Deprovisioning
description: "What an export rule's Deprovisioning Action does when an object leaves its scope, when a change takes effect, and how to preview it."
type: object
audience: administrator
---

# Deprovisioning

Provisioning's counterpart: each export rule's **Deprovisioning Action** determines what happens to the object in the Connected System when its Metaverse Object leaves the rule's scope or is deleted (for example, when a leaver's Metaverse Object is removed by a [deletion rule](../concepts/jml-lifecycle.md#deletion-rules)):

```jim-diagram
archetype: decision
title: What happens when a Metaverse Object leaves an export rule's scope
desc: 'When a Metaverse Object leaves an export rule''s scope or is deleted, it matters first whether a Create was ever sent for its Connected System Object. If not, JIM cancels the provisioning: the unsent Create Pending Export and the Connected System Object are removed, and nothing is exported. If it was, the Deprovisioning Action decides, and Delete wins when several export rules cover the object. Disconnect, the default, breaks the join and leaves the object in place in the Connected System; Delete queues a delete that removes it on the next export run.'
start: {label: Leaves scope or is deleted}
questions:
- {id: sent, label: 'Create sent?', sub: confirmed or not}
- {id: action, label: Deprovisioning Action, sub: Delete wins across export rules}
outcomes:
- {id: cancelled, label: Provisioning cancelled, sub: unsent Create and object removed}
- {id: disconnected, label: Disconnected, sub: join broken; object left in place}
- {id: deleted, label: Delete queued, sub: removed on the next export run}
answers:
- {from: sent, to: cancelled, label: 'no: Pending Provisioning'}
- {from: sent, to: action, label: 'yes'}
- {from: action, to: disconnected, label: Disconnect (default)}
- {from: action, to: deleted, label: Delete}
```

- **Disconnect** (default): JIM breaks the join and leaves the object in place in the Connected System. Nothing is exported: a change queued for the object while it was in scope, and not yet exported, is withdrawn by the next export run rather than written to an object JIM no longer manages.
- **Delete**: JIM queues a delete so the object is removed from the Connected System on the next export run.

The action applies regardless of how the object came to be joined: it makes no difference whether JIM provisioned it or matched (joined) a pre-existing object. If several export rules cover the same object with different actions, Delete wins.

**Changing the action** reaches objects that have already left the rule's scope at the next synchronisation (see [below](#when-a-change-to-an-export-rule-takes-effect)), not only future leavers. Switching from Delete to Disconnect withdraws each delete the rule queued that has not been exported yet, including one waiting to be retried after a failure, and disconnects the object instead, so its account stays. A delete already exported, or being exported at that moment, goes ahead: the account is removed as queued and the next import confirms it. Switching from Disconnect to Delete queues deletes for joined objects already outside the rule's scope.

One case sits outside the action altogether: provisioning that was **never exported**. If JIM has staged a new object for the Connected System (a Connected System Object in **Pending Provisioning** status, carrying a Create Pending Export) and the Metaverse Object leaves scope or is deleted before any export has run, there is nothing in the Connected System for either action to apply to. JIM cancels the provisioning instead: the unsent Create Pending Export and the Connected System Object are removed together, and nothing is exported. Once a Create has been sent, confirmed or not, the object may exist in the Connected System and the Deprovisioning Action applies as described above. The cancellation is reported on the Activity as a [**Provisioning cancelled**](activity-execution-items.md) outcome, so it is visible in the causality tree and Table view rather than only in logs.

Configure the action in the export section of the Synchronisation Rule editor. To review the deprovisioning behaviour of every export rule for an object type in one place, use the **Downstream Deprovisioning** panel on the Metaverse Object Type page (Admin, Schema, then the object type), where the action can also be changed inline.

## :material-clock-outline: When a change to an export rule takes effect

JIM evaluates a Metaverse Object against the export rules whenever a synchronisation changes its values. A change to the rule is different: it can move objects whose values have not changed at all into or out of the rule's scope, or change what happens to objects already outside it. So when you save an export rule in a way that can do either, JIM marks every Metaverse Object of the rule's object type for review:

- creating an enabled export rule
- enabling a disabled export rule
- switching **Provision to Connected System** on
- changing the rule's Scoping Criteria, whether widening or narrowing them
- changing the rule's Deprovisioning Action

The next synchronisation of any Connected System reviews each marked object against the rule as it now stands, whether it is a Full or a Delta Synchronisation, and even when a Delta Synchronisation has nothing new to import. An object now in scope is provisioned (when the rule provisions), and a joined object that has left scope is deprovisioned according to the Deprovisioning Action. The review appears on that run's Activity as the **Reviewing exports** step. It records an **Export Review** execution item, named after and linking the Metaverse Object, for each object it provisioned, deprovisioned, queued an export for or delivered a changed value to; an object it found already as the rule wants it gets none, so the few objects a change actually moved are not lost among the whole type. It reviews every object of the type once, so on a large population expect that run to take longer than usual.

Some changes deliberately mark nothing:

- **Disabling a rule, or switching provisioning off**<br /> The rule stops acting from then on; nothing it already created is deprovisioned.
- **Renaming a rule, or editing its Attribute Flow**<br /> Neither can move an object into or out of scope. A changed export Attribute Flow reaches existing objects at the target system's next Full Synchronisation, which corrects every object to the new configuration while the rule [enforces state](synchronisation-rules.md#export-outbound) (the default). With Enforce State off, each object picks the change up the next time one of its values changes.

A synchronisation marks objects for the same review itself when it disconnects one from a Connected System an export rule provisions to, so an account deleted directly in the target system is provisioned again by that run (see [Projection and provisioning](synchronisation-rules.md#projection-and-provisioning)).

A Metaverse Object that JIM creates or changes outside any synchronisation is marked for the same review, because no synchronisation visits it: the initial administrator created at first sign-in, a profile value refreshed at a later sign-in, and the objects [Example Data](example-data.md) generates. The next synchronisation of any Connected System, a Delta Synchronisation included, reviews each one against every export rule, so the outcome is the same whether the object or the rule came first. For an object whose values changed, the review also delivers those changes: a new or changed value is staged for the accounts the object already has, and a removed value is removed from them, exactly as if a synchronisation had made the change. A value the target system holds that JIM never managed is left alone. A change saved while a synchronisation is reviewing the object is kept for the next one. Upgrading from v0.17.0 or earlier marks every internal Metaverse Object (the initial administrator and any Example Data objects) for this review once, so the next synchronisation provisions any an export rule should already have reached. A value changed outside synchronisation on v0.17.0 or earlier reaches the accounts the object already has only when it next changes.

A change saved while a synchronisation is already running is not lost: that run carries on with the rules as they were when it started, and leaves the review for the next one.

**Changes saved on v0.16.0 or earlier.** Only changes saved on a later release are reviewed. If an export rule was created, re-enabled, switched to provisioning or re-scoped on v0.16.0 or earlier, objects it should have provisioned or deprovisioned may still be waiting for a change of their own. On a rule that provisions, switch **Provision to Connected System** off and save, then switch it on and save again: the next synchronisation reviews every object of the rule's type. On a rule that does not provision, the next change saved to its Scoping Criteria reviews them. A Deprovisioning Action switched from Delete to Disconnect on v0.16.0 or earlier leaves the deletes it queued before the switch in place; to have them withdrawn, switch it to Delete and back to Disconnect, saving each time, before the next export runs. That reaches accounts JIM still manages; a delete v0.16.0 or earlier left queued for an account it had already disconnected is still exported.

## :material-text-box-search-outline: Seeing what a run has deprovisioned

Every delete queued by a Deprovisioning Action is reported on the Activity of the run that staged it, so you can see exactly which accounts are about to be removed before the next export runs. Each queued delete appears on the deleted Metaverse Object's execution item as a **Pending Export** outcome nested beneath the **Metaverse Object deleted** outcome that caused it, naming the Connected System the account is being removed from and the export rule whose Deprovisioning Action queued it, and is counted in the Activity's Pending Exports total. Where several export rules cover the account, that is the rule whose Delete won. A leaver's execution item therefore reads as the whole chain: disconnected, Connected System Object deleted, Metaverse Object deleted, then one Pending Export per downstream account being deprovisioned. Open the outcome to see the Pending Export's detail.

This applies wherever the deletion happens: during a Synchronisation Run Profile (when the Metaverse Object Type's [deletion rule](../concepts/jml-lifecycle.md#deletion-rules) has no grace period, so the Metaverse Object is deleted inline), and in the background [Scheduled Metaverse Object Deletion](activities.md#scheduled-metaverse-object-deletion) batch that deletes Metaverse Objects once their grace period expires.

A delete queued because an object left an export rule's scope, whether its own values changed or a [change to the rule](#when-a-change-to-an-export-rule-takes-effect) moved it, is reported the same way: as a **Deprovision queued** outcome on that object's execution item, naming the Connected System the account is being removed from, and counted in the Activity's Pending Exports total.

Where the rule's Deprovisioning Action is **Disconnect**, the account stays in the Connected System and nothing is queued, so the run reports a **Disconnected from target system** outcome instead, in the same place: on the execution item of the object that left scope, or beneath the **Metaverse Object deleted** outcome when the Metaverse Object was deleted. It names the Connected System and the export rule whose action left the account in place, and links the account that JIM no longer manages. It is not counted as a Pending Export. [Sync Preview](sync-preview.md) predicts both outcomes in the same place.

## :material-eye-outline: Previewing a destructive toggle change

Two of a Synchronisation Rule's settings can turn a routine scope exit into something you cannot take back: the **Deprovisioning Action** above, and an import rule's **Out-of-Scope Action** (whether objects that leave import scope keep their Metaverse Object join or are disconnected). Both are single dropdowns, and before this preview existed the first sign of what one meant was the synchronisation run that acted on it.

The **Preview Deprovisioning Impact** button, which appears beside the editor's save button once you have edited either action, starts a [Configuration Change Preview](configuration-changes.md#previewing-a-change-before-you-make-it) of the toggles as they stand on the form, evaluated against the rule's saved configuration, changing nothing. It answers two different questions and keeps them apart:

- **What the next synchronisation would do differently.** Objects the rule already has something to act on: a joined object whose Metaverse Object is already outside an export rule's scope would be deleted from the target system rather than disconnected (or the reverse; an object whose delete has already been exported is left out, because the delete goes ahead either way), and a joined object outside import scope, or already marked obsolete, would be disconnected rather than keep its join (or the reverse). Where those disconnections would take a Metaverse Object's last connector, the preview follows the chain and reports which identities would become eligible for deletion.
- **What changes for every object the rule manages.** Flipping an export rule's action to Delete deletes nothing today, but it changes what every future scope exit means for every managed object. The preview states that exposure as its own count ("scope-exit action changes"), so "3,400 objects in this system move from Disconnect to Delete" reads at a glance without overstating what the save itself does.

Where several import rules cover the same object type, the Out-of-Scope Action that applies is taken from the first applicable rule. If that is not the rule you are editing, the preview says so by name and counts nothing, because your change would do nothing while that rule exists.

Saving with a current preview on screen states its counts on the confirmation and records the preview against the change's [Activity](activities.md); edit either toggle afterwards and the preview is marked stale and contributes nothing. Automation gets the same evaluation through [`New-JIMConfigurationChangePreview -SyncRuleId`](../powershell/previews.md) and the REST API's `POST sync-rules/{id}/destructive-toggles/preview` endpoint.
