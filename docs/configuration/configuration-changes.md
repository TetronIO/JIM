---
title: Configuration Changes
description: How JIM judges each configuration save, which edits it asks you to confirm, and how to preview what an edit would affect.
type: concept
audience: administrator
---

# Configuration Changes

Some configuration edits are harmless. Others decide whether accounts are deleted. The two sit side by side on the same pages, so JIM judges every save by the properties that actually changed, and reacts only where it matters.

```jim-diagram
archetype: comparison
title: The three classes of configuration change
desc: 'JIM classes each property a save changes. A Cosmetic change, such as a name, a description or an icon, goes straight through. A Sync-affecting change, such as scope, mappings or matching, asks you to confirm, listing each change from its old value to its new one. A Destructive change, such as a Deprovisioning Action or a deletion rule, asks you to confirm and also states in plain terms what the change will do. A save is judged by the properties that changed, not the page they are on.'
columns:
- {label: Cosmetic, sub: labels and display}
- {label: Sync-affecting, sub: what synchronisation does}
- {label: Destructive, sub: whether objects are removed}
rows:
- label: For example
  cells:
  - {text: 'a name, a description, an icon'}
  - {text: 'scope, mappings, matching'}
  - {text: 'Deprovisioning Actions, deletion rules'}
- label: On save
  cells:
  - {text: goes straight through}
  - {text: asks you to confirm, sub: 'each change, old value to new'}
  - {text: asks you to confirm, sub: and says what it will do}
footer: 'Judged by the properties changed: a rename saved with a Deprovisioning Action change is destructive'
```

## :material-tag-text-outline: How JIM classifies a change

Every configuration property carries one of three classes:

| Class | What it means | What JIM does when you save |
|---|---|---|
| Cosmetic | Labels and display: a name, a description, an icon, a page size. | Nothing. The save goes straight through. |
| Sync-affecting | Changes what synchronisation does: scope, mappings, matching, connector settings, enabling or disabling a rule. | Asks you to confirm, listing exactly what is changing and from which value to which. |
| Destructive | Decides whether objects are removed: Deprovisioning Actions, Object Type, partition and container selection, deletion rules. | As above, and additionally states in plain terms what the change will do. |

The class is worked out from the properties you actually changed, not from the page you changed them on. Renaming a Synchronisation Rule saves in silence even though the Deprovisioning Action sits on the same form; changing both in one save is treated as destructive.

## :material-content-save-check-outline: Confirming a change before you save it

Where a change is sync-affecting or destructive, JIM shows a confirmation before saving. It lists what is changing, leads with the most consequential change, names the object being changed, and says [when the change takes effect](#when-a-change-takes-effect): exactly, for a Synchronisation Rule; for every other surface, with a reminder that saving does not apply the change to existing objects and a Full Synchronisation does. Where a destructive property is involved it also says what that property governs and what your particular change will do; switching a Deprovisioning Action *to* Delete and switching it *back* are both confirmed, but they are described differently, because they are opposite consequences.

Cancelling the confirmation abandons the save. Nothing is written.

A change that stops a [configuration test case](configuration-tests.md#when-a-change-stops-a-case-passing) passing is not saved until you choose: keep editing (nothing is saved), accept the new result, or save anyway. Accepting or saving anyway is recorded on the change's entry, naming each case.

## :material-timer-sand: When a change takes effect

Saving writes configuration; a synchronisation carries it to the objects that already exist. For a change to an import Synchronisation Rule or an Object Matching Rule, that is the Connected System's next synchronisation, Delta or Full, and JIM shows the wait at every step:

```jim-diagram
archetype: flow
title: How a saved import rule change reaches existing objects
desc: 'Five steps carry a saved change to an import Synchronisation Rule to the objects it affects. Optionally preview it (1). Save it (2): the confirmation says when it takes effect. It then waits as awaiting synchronisation (3), shown on the Connected System page with a Run now button. The Connected System''s next synchronisation, Delta or Full, processes every object of the affected Object Type and applies it (4). The Activities record it (5): the run lists the changes it applied, and each change says which run applied it.'
steps:
- {id: preview, label: Preview, sub: optional, badge: 1}
- {id: save, label: Save, sub: says when it applies, badge: 2}
- {id: wait, label: Awaiting synchronisation, sub: Run now, badge: 3}
- {id: run, label: Synchronisation, sub: every object of the type, badge: 4}
- {id: activity, label: Activities, sub: Applied by, badge: 5}
```

1. **Preview**<br /> The rule editor's preview button reports which objects the change would move, changing nothing; see [Previewing a change](#previewing-a-change-before-you-make-it).
2. **Save**<br /> The confirmation states when the change takes effect. For an import change it reads: "At the next synchronisation of {System}, Delta or Full. That run processes every {Type} object, not only the changed ones: {count} objects, about {N} minutes at the speed of its last Full Synchronisation. Other Object Types are processed as usual. Until then the Connected System's page lists it as awaiting synchronisation, with a button to run that synchronisation straight away." The estimate is left out until the Connected System has had a Full Synchronisation. REST and PowerShell callers get the same facts: the response to saving a Synchronisation Rule, an Attribute Flow or an Object Matching Rule, or to changing an Attribute Priority order, lists the Connected System, the Object Type, the object count and the estimated duration under `AwaitingSynchronisation`, and the cmdlets that make those changes warn of the same.
3. **Awaiting synchronisation**<br /> The change shows on the Connected System, with **Run now**; see [Changes awaiting synchronisation](connected-systems.md#changes-awaiting-synchronisation).
4. **The synchronisation**<br /> The run processes every object of the affected Object Type, and only the changed objects of the others, then marks the change applied. Every Metaverse Object those objects join or project is processed too, so export evaluation runs on them as usual.
5. **The Activities**<br /> The synchronisation's Activity lists the changes behind each Object Type it processed in full, and each change's own Activity says **Applied by** that run.

What waits for which synchronisation depends on what you changed:

| You change | It reaches existing objects at | Until then |
|---|---|---|
| An import Synchronisation Rule (scope, enabling, projection, Out-of-Scope Action, Attribute Flow, Attribute Priority, matching) or an Object Matching Rule | The next synchronisation of the Connected System, Delta or Full, which processes every object of the affected Object Type | The Connected System lists it as awaiting synchronisation, and the rule shows **Awaiting synchronisation** |
| An export Synchronisation Rule's scope, provisioning or Deprovisioning Action | The next synchronisation of any Connected System, which reviews every Metaverse Object of the rule's type | The rule and the Metaverse Object Type show **Export review pending**; see [When a change to an export rule takes effect](deprovisioning.md#when-a-change-to-an-export-rule-takes-effect). The Connected System does not count it as pending a Full Synchronisation |
| An export Attribute Flow | The target Connected System's next Full Synchronisation | The Connected System shows it as pending a Full Synchronisation, as it does the part of an export rule save that the review does not carry |
| A Metaverse Attribute or Object Type, or what a Connected System imports (schema selection, partitions and containers) | A Full Synchronisation, after a Full Import where the change alters what JIM reads | The same |
| A Metaverse Object Type's deletion settings | At once: objects that already satisfy a new deletion rule become eligible for deletion on the next synchronisation or housekeeping run, with no further change to them | The confirmation says so |

The confirmation names the one that applies. For an export scope change it reads "At the next synchronisation of any Connected System, which reviews every {type} Metaverse Object against the rule as it now stands." For an export Attribute Flow it reads "Existing objects get this at the next Full Synchronisation of {System}."

JIM records an import change as awaiting synchronisation only when it can alter what an import produces:

- **Not recorded**<br /> A cosmetic change (a name, a description), a save that changed nothing, and an edit to a rule that is disabled before and after the save.
- **Simple Mode matching**<br /> A Simple Mode Object Matching Rule change is recorded for its Object Type only when an enabled import rule covers that type, since nothing else synchronises its objects.
- **Tracking off**<br /> With [configuration change tracking](service-settings.md) off, JIM cannot tell what changed, so it records every save of an import rule.
- **Recording fails**<br /> The change stays saved, and the save's Activity fails with a message telling you to run a Full Synchronisation, which applies it whatever was lost; see [Troubleshooting Synchronisation](../administration/troubleshooting-synchronisation.md).

## :material-eye-outline: Previewing a change before you make it

A confirmation tells you *what* you are changing. A **Configuration Change Preview** tells you what it would *do*: JIM evaluates the proposed change against the objects already in the Metaverse and reports which of them would be affected, changing nothing.

This is a preview of a **proposed edit**, across every affected object. For "what would synchronising *this one object* do with the configuration already saved?", see [Sync Preview](sync-preview.md) instead.

Previews are available where a surface has an evaluator for it:

- A Metaverse Object Type's [deletion settings](metaverse.md#previewing-a-deletion-settings-change), the change most worth asking about because it is the one that can make existing objects eligible for deletion the moment it is saved.
- A Connected System's [partitions and containers](partitions-and-containers.md#previewing-a-partition-or-container-change), where deselecting one container quietly takes everything beneath it out of import scope.
- A Synchronisation Rule's [destructive toggles](deprovisioning.md#previewing-a-destructive-toggle-change): the Deprovisioning Action, which decides whether a scope exit disconnects an object or deletes it from the target system, and the Inbound Out-of-Scope Action, which decides whether objects leaving import scope keep their join.
- A Synchronisation Rule's [Scoping Criteria](scoping-criteria.md#previewing-a-scope-change), which decide which objects the rule manages at all: narrowing them takes objects out of scope and hands them to whichever of the two toggles above applies, while widening them projects and provisions identities nobody has counted.
- A Synchronisation Rule's [Attribute Flow](attribute-mappings.md#previewing-an-attribute-flow-change), which decides what the objects it manages end up holding: a changed mapping rewrites an attribute on every one of them, and an Expression that malforms one case in a thousand is invisible until it has flowed.
- A Connected System's [Object Matching Rules](object-matching-rules.md#previewing-an-object-matching-change), which decide which Metaverse Object each of its unjoined objects joins to: a rule matched too loosely merges an account into the wrong identity, one matched too tightly creates a duplicate beside the right one, and neither fails at the time.
- A Synchronisation Rule's [behaviour toggles](object-matching-rules.md#previewing-a-behaviour-change), which decide whether the rule runs at all and what it may create: disabling one withdraws every value it owns, and turning provisioning on creates accounts at scale.
- A Connected System's [schema selection](connected-system-schema.md#previewing-a-schema-change), which decides what JIM reads: deselecting an Object Type takes its objects out of management on the next Full Import, disconnecting the joined ones, while deselecting an attribute has no visible effect at all and leaves the values already held flowing without ever being refreshed.
- [Deleting a Connected System](removing-a-connected-system.md#previewing-a-deletion) altogether, which hands its values to the systems that also contribute them, clears the rest, can leave people with no connector at all, and corrects every downstream system to match.

The panel opens with what saving would do, in one sentence, worst consequence first: a change that disconnects forty thousand objects and makes two eligible for deletion leads with the two deletions, because that is the part you cannot take back. The breakdown sits beneath it, and selecting a row lists the objects behind it.

A preview answers in stages, and each appears as it completes:

| Stage | What it tells you |
|---|---|
| Validation | Whether the proposal can be applied at all. A blocking finding stops the preview; nothing further is evaluated, because counting the objects a change would affect is a statement about a change that will happen. |
| Objects affected | How many objects would move through each transition. Exact counts over the whole population. |
| Summary | The counts broken down by transition, object type, and the attribute and values involved. |
| Object detail | The individual objects behind each summary row. |

The stages are shown while the preview is working, and while any of them ended in something other than success. A preview whose every stage completed says so once, in the status beside its heading.

A summary row names the old and new values where doing so makes the summary shorter: when every object in the row shares one pair of values ("Email changed from @contoso.com to @fabrikam.com"), or when a few pairs between them cover many objects, each pair gets a row of its own. Where most objects have values of their own (five people, five different Job Titles cleared), JIM shows one row for the attribute instead, and the drill-down behind it lists each object's values. Either way the row's count is exact.

Where JIM recognises what kind of edit a summary row describes, it says so beside the row: "Email or UPN domain changed" rather than only "Email changed". The patterns it names are a domain change on an address or User Principal Name, a move to a different container, a change of letter case alone, and text added to or removed from the start or end of a value.

A pattern only appears where **every** object in the row makes the same kind of edit. A row covering a mixture is left unnamed, and the objects behind it carry their own patterns in the drill-down. Nothing is shown where JIM does not recognise the change, which is the normal case for rows whose values are dates or identifiers.

In the drill-down, each object's name is a link to the object the change would act on, with a **CS** or **MV** marker saying which side of the Metaverse it leads to: the Connected System Object where the row has one (the object an export would remove, or the joined object an import would disconnect), otherwise the Metaverse Object.

Four things are worth knowing before you act on a preview:

- **A preview that failed shows nothing.** A part-way evaluation has seen an arbitrary subset of the objects, so its counts are real numbers about the wrong population. JIM withholds them rather than presenting them with a caveat beside them.
- **An object whose scope cannot be decided is counted as one the run would fail.** When a [Scoping Criterion](scoping-criteria.md) cannot be evaluated for an object (an Expression whose value cannot be compared as its Compare As type, say), a synchronisation stops that object with an error and changes nothing about it. Every preview says the same, as **Fails with an error** with the reason, and previews every other object; it never guesses the object's scope.
- **Object detail may be a sample.** Each summary row keeps a capped number of detail rows by default; the row's own count is always exact. Where the cap applied, the drill-down is labelled as a sample, and you can ask for the full set when you start the preview.
- **A preview describes the data as it stood.** An import that runs afterwards can move the answer. The panel says when the preview was evaluated so you can judge whether that matters.

In the portal, previewing leaves the change unsaved: read the result, then save (or not). If you save, the confirmation opens with the same sentence, alongside the properties changing, and the change's [Activity](activities.md) records which preview informed it. Edit the settings after previewing and the preview is marked stale and contributes nothing to the confirmation, because it now describes a different change. On a Synchronisation Rule, each preview's button appears only once you have edited what it answers for, since a preview of unedited settings can only report that nothing changes. Any preview panel you have finished with can be closed, which forgets the preview so the confirmation no longer cites it, while a preview still running finishes as an Activity.

Automation gets the same evaluation. Start a preview with [`New-JIMConfigurationChangePreview`](../powershell/previews.md), or `POST` to the surface's own preview endpoint in the [REST API](../../api/reference/), then pass the preview's Activity id back on the change itself so the audit records the link.

## :material-page-layout-header-footer: Where the confirmation appears

| Surface | Changes that can trigger it |
|---|---|
| [Synchronisation Rules](synchronisation-rules.md) | Direction, enabling, provisioning and projection, scope, Attribute Flow, Object Matching Rules; **destructive:** Deprovisioning Action, Inbound Out-of-Scope Action |
| [Connected Systems](connected-systems.md) (Details, Settings, Schema, Scope) | Connector settings, matching mode, unresolved reference handling, attribute selection, selecting a container; **destructive:** deselecting an Object Type, a partition or a container |
| [Metaverse Object Types](metaverse.md#deletion-behaviour) | Attribute bindings; **destructive:** deletion rule, grace period, deletion trigger systems. The Deprovisioning Action dropdown on this page edits Synchronisation Rules and is confirmed the same way. |
| [Metaverse Attributes](metaverse.md#attributes) | Data type, plurality, Object Type bindings |
| [Service Settings](service-settings.md) | The few settings that steer synchronisation; nearly all Service Settings are operational and save in silence |

## :material-bell-outline: When JIM stays silent

The confirmation appears only when JIM can tell what changed, which means [configuration change tracking](service-settings.md) must be switched on. With tracking off there is no recorded baseline to compare against, so JIM says nothing rather than guessing. Creating a new object is never confirmed either: there is no prior state, so nothing existing is at risk.

Changes made through the REST API and PowerShell are not prompted. An automated call names the property it is setting, so consent is already explicit; the change is classified and recorded in the Configuration Change History exactly as a portal change is.

## :material-book-open-variant-outline: Related

- [Sync Preview](sync-preview.md): previewing what synchronising one object would do with the configuration you already have, including the destructive cascade
- [Activities](activities.md): every configuration change is recorded as an Activity, with a versioned before-and-after snapshot
- [Preview cmdlets](../powershell/previews.md): starting, reading and cancelling a preview from PowerShell
- [Metaverse](metaverse.md#previewing-a-deletion-settings-change): previewing a change to deletion settings
- [Connected Systems](partitions-and-containers.md#previewing-a-partition-or-container-change): previewing a change to what JIM imports
- [Synchronisation Rules](deprovisioning.md#previewing-a-destructive-toggle-change): previewing a change to the destructive toggles
- [Object Matching](object-matching-rules.md#previewing-an-object-matching-change): previewing a change to which identity an account joins
- [Behaviour toggles](object-matching-rules.md#previewing-a-behaviour-change): previewing a change to whether a rule runs, and what it may create
- [Connected Systems](connected-systems.md#changes-awaiting-synchronisation): the changes awaiting synchronisation, and the changed-since indicator
- [Service Settings](service-settings.md): switching configuration change tracking on or off
