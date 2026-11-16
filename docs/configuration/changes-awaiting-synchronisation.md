---
title: Changes Awaiting Synchronisation
description: "How a Connected System shows configuration changes the next synchronisation applies, and changes pending a Full Synchronisation."
type: object
audience: administrator
---

# Changes Awaiting Synchronisation

A saved configuration change reaches existing objects only when a synchronisation next runs over them, so for a while the portal and the configuration are ahead of your data. A Connected System shows that wait in two ways, because they ask different things of you: a change the next synchronisation applies, and a change that is pending a Full Synchronisation. [When a change takes effect](configuration-changes.md#when-a-change-takes-effect) says which change is which.

```jim-diagram
archetype: decision
title: Where a saved configuration change shows on a Connected System
desc: A saved change the Connected System's next synchronisation applies, such as an import Synchronisation Rule or Object Matching Rule change, shows as awaiting synchronisation. Any other change shows as pending a Full Synchronisation only while the configuration still differs, in something a Full Synchronisation applies, from the configuration the last one started with. A change that was undone, or that no synchronisation acts on, such as a rename, a deletion setting or an Initial Export Only Attribute Flow, is not shown.
start: { label: Saved change }
questions:
  - { id: next, label: "Applied by the next synchronisation?", sub: "import rule, Object Matching Rule" }
  - { id: full, label: "Still differs from the last Full Synchronisation?", sub: in something it applies }
outcomes:
  - { id: awaiting, label: Awaiting synchronisation, sub: "Delta or Full applies it" }
  - { id: pending, label: Pending a Full Synchronisation, sub: counted on the indicator }
  - { id: none, label: Not shown, sub: "undone, or nothing to apply", good: true }
answers:
  - { from: next, to: awaiting, label: "yes" }
  - { from: next, to: full, label: "no" }
  - { from: full, to: pending, label: "yes" }
  - { from: full, to: none, label: "no" }
```

## :material-sync: Changes the next synchronisation applies

Saving an import Synchronisation Rule, an Object Matching Rule or an Attribute Priority order records the change as awaiting synchronisation for the Connected System Object Type it affects. The Connected System's page shows one notice per Object Type, **Changes awaiting synchronisation: {Type}**, listing one row per changed rule; each change links to its Activity. **Run now** opens the queue dialog with the Delta Synchronisation Run Profile chosen, or the Full Synchronisation one where the system has no Delta; you can pick another. The Connected Systems list marks the system with a chip counting the changes waiting, and the rule shows an **Awaiting synchronisation** chip.

The Connected System's next synchronisation, Delta or Full, processes every object of that Object Type and only the changed objects of the others, then marks the changes applied:

- **Several changes, one run**<br /> Every change saved before a run starts is applied by that run.
- **A change saved during a run waits for the next one**<br /> The running synchronisation carries on with the rules it started with.
- **A failed or cancelled run applies nothing**<br /> The next run processes the Object Type again, and the notice stays until a run completes.
- **The cost is the whole Object Type**<br /> On a large Object Type, the next Delta Synchronisation takes as long as processing every object of that type. The notice gives the object count: run it in a quiet period, or choose **Run now** to start it straight away.
- **Export follows on its own**<br /> Every Metaverse Object the Object Type joins or projects is processed too, so export evaluation runs on them as usual.

The synchronisation's Activity has a section, **Processed every {Type} object**, listing the changes behind it and whether it applied them. Each change's own Activity says **Applied by** that run, or **Awaiting the next synchronisation** of the system.

## :material-alert-circle-outline: Changes pending a Full Synchronisation

Some changes no ordinary synchronisation applies by itself. A Connected System with such a change since its last Full Synchronisation shows an indicator in the Connected Systems list, and a notice on its page saying how many changes are waiting and when the last Full Synchronisation ran.

**Only consequential changes count.** Renaming a Connected System or editing a description changes nothing about what synchronisation does, so it never raises the indicator. Changes are classified as they are recorded, and only the two classes that alter outcomes are counted:

| Class | Examples | How it shows |
|-------|----------|--------------|
| Sync-affecting | An export Attribute Flow, a Metaverse Attribute, schema selection | Amber, with the number of changes |
| Destructive | Deselecting an Object Type or partition, deleting a Synchronisation Rule | Red, because applying it can cascade deletions or mass deprovisioning |

A change to an import Synchronisation Rule or an Object Matching Rule is not counted: the next synchronisation applies it. Nor is a change to an export rule's scope or Deprovisioning Action: the next synchronisation of any Connected System reviews every Metaverse Object against the rule as it now stands. Only what no synchronisation applies by itself counts, so a save that changes an export rule's scope and its Attribute Flow raises the indicator for the Attribute Flow edit alone.

Nor does a change a Full Synchronisation would not act on:

- **A Metaverse Object Type's deletion settings**<br /> The Deletion Rule, its trigger Connected Systems and the grace period are applied when an object is disconnected, and by housekeeping when the grace period ends. A Full Synchronisation does not revisit objects that are already disconnected.
- **An Initial Export Only Attribute Flow**<br /> Its value is written only when an object is provisioned, so adding, editing or removing one changes nothing for objects already provisioned. Switching a flow from Initial Export Only to managed does count, because the flow then manages every object.

**The net change counts, not each edit.** For each Synchronisation Rule, Connected System, Metaverse Object Type or Metaverse Attribute with a counted change, JIM compares its configuration now with its configuration when the last Full Synchronisation started. If you change a setting and then change it back, the indicator clears: there is nothing for a Full Synchronisation to apply. Deleting a Synchronisation Rule always counts, because nothing is left to compare with.

**Attribution is precise.** Editing a Metaverse Attribute raises the indicator only on the Connected Systems whose Synchronisation Rules actually reference that attribute, not on every system. Deleting a Synchronisation Rule raises it on the system that rule belonged to.

**Two states mean "JIM cannot tell", not "up to date":**

- **Never synchronised.** The Connected System has never completed a Full Synchronisation, so no configuration has
  ever been applied in full and there is nothing to compare against.
- **Unknown.** Configuration change tracking is switched off (see
  [Configuration Change History](activities.md#configuration-change-history)), so JIM holds no record of what changed.

Both are shown distinctly rather than as a clean result, because reporting a settled configuration JIM cannot vouch
for would be worse than reporting nothing. Changes awaiting synchronisation are reported in both states.

!!! note "The reference point is when the run started"
    A change made while a long Full Synchronisation was still running may not have been picked up by it, so it is
    counted as still pending. You may occasionally be prompted for a re-run you did not strictly need; the alternative
    would be hiding a change that really was missed.

Read both from automation with `(Get-JIMConnectedSystem -Id <id>).ConfigurationDrift`, or the `configurationDrift` object on the REST Connected System response. `AwaitingSynchronisation` lists the Object Types waiting, each with its changes and object count. Check `IsDeterminable` before treating `HasPendingChanges` as `false`.
