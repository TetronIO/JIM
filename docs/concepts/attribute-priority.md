---
title: Attribute Priority
description: How JIM decides which Connected System's value wins when several feed one Metaverse Attribute, and how to set the order.
type: concept
audience: administrator
---

# Attribute Priority

When more than one Connected System feeds the same Metaverse attribute, JIM has to decide which value wins. **Attribute Priority** makes that decision deterministic: each contributing Synchronisation Rule has a priority for the attribute, and the highest-priority contributor that has an opinion wins. The result no longer depends on the order your synchronisations happen to run in.

```jim-diagram
archetype: decision
title: How Attribute Priority picks a contributor
desc: "JIM takes the Synchronisation Rules that contribute to a Metaverse attribute in priority order, 1 first. If no contributor is left, the attribute has no contributor and is left unset. If the next contributor supplies a value, its value wins and lower priorities are not consulted. If it supplies none, but it applies to the object (enabled, joined and in scope) and has Null is a value set, it asserts no value: the attribute is cleared downstream, and lower priorities are not consulted. Otherwise, including a rule that does not apply to the object at all, it is skipped and JIM considers the next contributor."
start: {label: Metaverse attribute}
questions:
- {id: left, label: "A contributor left?", sub: "next in priority order, 1 first"}
- {id: value, label: "Does it supply a value?"}
- {id: niv, label: "Applies, with Null is a value?", sub: "enabled, joined and in scope"}
outcomes:
- {id: unset, label: No contributor, sub: the attribute is left unset}
- {id: wins, label: Its value wins, sub: lower priorities not consulted, good: true}
- {id: blank, label: No value asserted, sub: "cleared downstream; lower priorities not consulted"}
answers:
- {from: left, to: unset, label: "no"}
- {from: left, to: value, label: "yes"}
- {from: value, to: wins, label: "yes"}
- {from: value, to: niv, label: "no"}
- {from: niv, to: blank, label: "yes"}
- {from: niv, to: left, label: "no: skip it"}
```

This is an **inbound** concern. It governs how values flow from Connected Systems into the Metaverse; it does not change how the Metaverse is exported back out.

Picking one winner is the **Priority** contribution mode, which every attribute uses unless you choose otherwise. A multi-valued attribute can instead take values from every contributor: **Accumulate** keeps every value it has ever been given (see [Accumulating values from every contributor](#accumulating-values-from-every-contributor)), and **Merge** holds the values its contributors supply now (see [Merging values from every contributor](#merging-values-from-every-contributor)).

## :material-sort-numeric-ascending: How resolution works

For a given Metaverse Object attribute, JIM looks at every enabled import Synchronisation Rule that maps to it and evaluates them in priority order (1 is highest):

- **A contributor with a value wins.** The first contributor, in priority order, that supplies a value sets the attribute. Lower-priority contributors are not consulted.
- **A contributor with no opinion is skipped.** If a rule does not apply to this object (it is disabled, no object from its Connected System is joined, or the joined object is out of the rule's scope), it is passed over and the next priority is considered.
- **If nobody has an opinion, the attribute has no contributor** and is left unset.

Because priority is held per Synchronisation Rule mapping, the same Connected System can rank differently for different attributes (it might be authoritative for `department` but secondary for `jobTitle`), and a single system can even contribute through several differently-scoped rules at different priorities to express "this system owns these objects, that system owns those".

A single-source attribute (only one rule maps to it) needs no configuration and is unaffected.

**The contributors are always different Synchronisation Rules; one rule cannot contribute twice.** A Synchronisation Rule can carry at most one Attribute Flow per target attribute, and JIM refuses a second, so a priority list is never an ordering of two mappings from the same rule. To fall back between two source attributes of the same system within one rule, use a single expression mapping (`Coalesce(cs["jimBadgeColour"], cs["roomNumber"])`); to give the same system two positions in the priority order, use two differently-scoped Synchronisation Rules, as described below.

## :material-playlist-plus: Accumulating values from every contributor

A multi-valued attribute set to the **Accumulate** contribution mode takes values from every contributor rather than one winner, and synchronisation never removes them. Use it for a record that must outlive its sources, such as an audit log of every account a person has been issued: each Connected System adds its account identifiers, and they stay after the accounts are deleted (see the [account audit log recipe](expression-recipes.md#keeping-an-account-audit-log)).

- **Every contributor adds**<br /> Each Synchronisation Rule that maps to the attribute adds the values it supplies that the attribute does not already hold. No contributor wins, so the priority order is not used and cannot be changed, and "Null is a value" has no effect.
- **Nothing is removed by synchronisation**<br /> A value stays when its source changes it, when the contributing object leaves scope, is disconnected or is deleted, and when the Synchronisation Rule, the Attribute Flow or the whole Connected System is deleted. Whichever recall choice those deletions offer, an Accumulate attribute keeps its values, and the recall impact they state leaves them out.
- **Each value is held once**<br /> Values are compared exactly, so `Jo@corp.example` and `jo@corp.example` are two values; set the Attribute Flow's case normalisation to treat them as one. A value keeps the contributor that first supplied it, which the Metaverse Object's provenance shows.
- **Exports send every kept value**<br /> An export Attribute Flow from an Accumulate attribute sends all of its values, including those from accounts that have since been deleted. The Attribute Flow editor says so when you map one.

Accumulate needs a multi-valued attribute of any data type except Reference. A reference is removed with the Metaverse Object it points to, so it cannot be kept; to keep a history of references, record each one as text in a separate attribute instead (for example `"manager=" + cs["manager"]`).

### Removing values an Accumulate attribute has kept

Synchronisation never removes an Accumulate value, so an administrator removes one that is no longer wanted, such as an entry for a test account:

- **In the portal**, open the Metaverse Object's **Inspect** view, select the attribute, and choose **Remove value** beside the value in the inspector.
- **In PowerShell**, `Remove-JIMMetaverseObjectAttributeValue -MetaverseObjectId <id> -AttributeName 'Account Audit Log' -Value '<value>'`.
- **Through the REST API**, `DELETE /api/v1/metaverse/objects/{id}/attributes/{attributeId}/values`, naming the values by `valueIds` or `values`.

JIM refuses to remove a value a Synchronisation Rule still supplies, and names the rule and its Connected System: that rule's next synchronisation would only add the value back. Change the value at its source, or take the object out of the rule's scope, first. It decides this by running each enabled import rule's Attribute Flows for the Connected System Objects joined to the Metaverse Object, as synchronisation would, so a value tagged with a deleted account is removable while another system still holding the same value is not. When a rule's scope or an expression cannot be evaluated, the removal is refused rather than guessed.

A removal is all or nothing. It is recorded on the Metaverse Object's change history as the administrator's, under an Activity of its own, and reaches the Connected Systems the attribute is exported to at their next synchronisation.

### Changing the mode

A change of mode takes effect at each Metaverse Object's next synchronisation. Before it is saved, every surface reports what it would do: how many Metaverse Objects would lose values, how many values no contributor supplies any more, how many hold several contributors' values and would keep only the highest-priority one's (moving to Priority), and how many would gain values from contributors they hold none from yet (moving from Priority). The portal shows this under the mode selector, the REST API answers `GET /api/v1/metaverse/attributes/{id}/contribution-mode-impact?mode=`, and `Set-JIMMetaverseAttribute` states it in its confirmation and under `-WhatIf`.

The report is read from what JIM holds, without running any Attribute Flow, so where values would be removed it is a floor: a value a joined contributor no longer supplies because it changed at its source is counted only once that contributor is next synchronised.

Changing an Accumulate attribute back to Priority lets that synchronisation remove every value that no contributor supplies any more, and exports then remove them from target systems, so every surface asks you to confirm it: the portal by typing the attribute's name, the REST API with `confirmLeavingAccumulate`, and PowerShell with `-Force`.

## :material-set-merge: Merging values from every contributor

A multi-valued attribute set to the **Merge** contribution mode holds every value any contributor supplies, and drops a value once no contributor supplies it any more. Use it where several systems each own part of a set, such as a person's email addresses held across a directory and a mail system: the attribute holds them all, and stays in step as each system changes its own.

- **Every contributor adds**<br /> Each Synchronisation Rule that maps to the attribute adds the values it supplies that the attribute does not already hold. As with Accumulate, no contributor wins, so the priority order is not used and cannot be changed, and "Null is a value" has no effect.
- **A contributor withdraws only its own values**<br /> When a Connected System stops supplying a value, because its source changed it or the contributing object left scope or was deleted and its values recalled (see [When the winning source disconnects or withdraws](#when-the-winning-source-disconnects-or-withdraws)), JIM removes that value unless another contributor still supplies it.
- **A value another contributor supplies stays where it is**<br /> If the directory and the mail system both supply `b@corp.example` and the directory drops it, the value is kept and credited to the mail system. The Metaverse Object's value does not change, so nothing is exported and no value change is recorded; the value's provenance names the new contributor.
- **Each value is held once**<br /> Values are compared exactly, as under Accumulate; use the Attribute Flow's case normalisation to treat `Jo@corp.example` and `jo@corp.example` as one.

Merge needs a multi-valued attribute of any data type except Reference. Changing an Accumulate attribute to Merge removes, at each Metaverse Object's next synchronisation, the values no contributor supplies any more, so it asks for the same confirmation as changing it to Priority. Changing between Priority and Merge, or from Merge to Accumulate, removes nothing at the time of the change.

## :material-account-lock-outline: Giving one system authority over a subset of objects

Because the priority list is a list of **Synchronisation Rules**, not of Connected Systems, a system can appear in it more than once through rules with different Scoping Criteria. That is how you express "this system is authoritative for these objects, that system is authoritative for the rest" without any extra machinery.

The usual shape is two import rules on the same Connected System:

- an **unscoped** rule that applies to everything it imports, sitting low in the priority order; and
- a **narrowly scoped** rule covering just the exception objects, sitting at the top.

An object inside the scoped rule's criteria is contributed by that rule and wins. An object outside them is not contributed by that rule at all (the rule has no opinion for it, exactly as a disabled rule has none), so the next priority decides. Authority is therefore per object, not per system, and the two rules never fight: at most one of them applies to any given object.

Two consequences are worth stating plainly:

- **Scoping Criteria are evaluated against Connected System Object attributes, so an object can move in and out of a rule's scope as its own attributes change.** Renaming a group so it matches an exceptions pattern transfers authority for it on the next synchronisation. That is the intended behaviour, and it is quiet: nothing announces it beyond the resolution decision itself. Authority passes back the same way when the object leaves the scoped rule, provided that rule's Out-of-Scope Action is **Disconnect from Metaverse**: the unscoped rule replaces the value, and the run records the scope exit as **Left scope, join kept**. With **Remain joined** the scoped rule's last value stays and keeps winning (see [Scoping Criteria](../configuration/scoping-criteria.md#what-happens-to-each-object-a-synchronisation-visits)).
- **The losing system is not corrected automatically.** A change made directly in a system that loses resolution never reaches the Metaverse, but it stays in that system until an export Synchronisation Rule with **Enforce State** targets it, at which point export re-evaluation stages a corrective Pending Export and puts the winning value back. Without such a rule the losing system stays divergent; the Metaverse and every other system are protected either way.

## :material-null: "Null is a value"

By default, if the highest-priority source has **no** value for an attribute, JIM falls through to the next source. Sometimes that is wrong: when the authoritative source clears a value, you want the clear to propagate, not to be back-filled from a stale secondary copy.

Enabling **"Null is a value"** on a contributor changes that. If that contributor is connected and in scope but supplies no value, JIM stops there and asserts "no value" for the attribute: it is cleared everywhere downstream, and lower-priority sources are not consulted. This is distinct from the contributor not applying to the object at all (a rule with no opinion is always skipped, regardless of this setting).

Typical uses:

- **A manager or department cleared at the authoritative source** must propagate as a clear, not be resurrected from a directory that still holds the old value.
- **A primary system migration** where the new system is authoritative for the people it knows about (including their blanks), while a legacy system remains the only source for people not yet migrated.

> Asserting null is powerful: a misbehaving high-priority import (an empty file, a truncated delta) becomes a mass-clearing event rather than a harmless no-op. Treat "Null is a value" as an authoritative, deliberate setting.

## :material-scale-balance: Determinism and ties

Resolution is always deterministic. Two contributors to one attribute cannot share a priority (this is prevented when you configure the order), but if it ever occurs, JIM breaks the tie consistently rather than by timing, so a sync run never produces a different winner than the one before.

When you add a new import mapping to an attribute that already has contributors, it is placed at the **lowest** priority. A newly added source therefore never silently takes over an attribute; you promote it explicitly when you want it to win.

## :material-link-variant-off: When the winning source disconnects or withdraws

This section describes a Priority attribute; an [Accumulate attribute](#accumulating-values-from-every-contributor) keeps its values whatever its sources do.

If the source that currently provides an attribute's value disconnects (its object is removed from that Connected System) or falls out of its Synchronisation Rule's scope, JIM does not blank the attribute. It re-elects the next contributor: a still-connected, in-scope lower-priority source takes over, and its value flows into the Metaverse in place of the departed one.

Where no other contributor supplies the attribute, one principle decides what happens to it: **JIM recalls a value only while a source remains to stand behind the object; otherwise it preserves the object's last known state or completes the deletion.** Three situations follow from it, checked in this order:

- **A deletion is pending**<br /> The object type's Deletion Rule has scheduled the Metaverse Object's deletion (see [deletion behaviour](../configuration/metaverse.md#deletion-behaviour)). The departed source's sole-contributed values are preserved for the grace window: recalling them would send clears to every target system moments before deprovisioning removes the accounts anyway, and if the source reappears within the window (the grace period's purpose) the object is exactly as it was, with nothing churned downstream.
- **Another source remains**<br /> A remaining Connected System still carries an enabled import Synchronisation Rule for the object's type, so the object is still actively managed. The departed source's sole-contributed values are recalled (cleared): nothing asserts them any more, and leaving them in place would keep stale data on a live Metaverse Object forever.
- **No source remains**<br /> Only provisioned target systems (or nothing at all) still hold the object. Its values are preserved as **last known state**: they are what the target accounts are built from (including expression-based values such as a Distinguished Name), and clearing them because a source had an outage would damage live accounts. If a source-of-record departure should instead deprovision the target accounts, that is the **When Authoritative Source Disconnected** Deletion Rule's job; JIM advises you of exactly this when you configure **When Last Connector Disconnected** for a type that provisions targets.

A deliberate removal is different from a disappearance: deleting an Attribute Flow mapping, a Synchronisation Rule or a whole Connected System asks you what should happen to the contributed values, and the choice you make is carried out in full; the last-known-state preservation above never overrides it.

This means an authoritative source leaving hands an attribute down to the next source rather than dropping it, so downstream systems receive the fallback value instead of an unintended clear. The next contributor is resolved exactly as in normal flow, so if it has **"Null is a value"** set and supplies no value, the attribute is asserted null rather than handed further down.

Re-election covers every attribute type, including references: a manager or group membership recalled from a departing source is handed to the surviving contributor within the same synchronisation run, not left blank until that source next synchronises. It also holds when the surviving source carries the identical value; the value stays, now attributed to the surviving contributor.

The same hand-over applies when the winning source stays connected but stops supplying a value, without "Null is a value" set: for example, an expression that starts evaluating to null, or a source attribute that becomes unpopulated. The next-priority contributor takes over in the same synchronisation run, exactly as it would if the winning source had disconnected. Only when no other source contributes is the attribute cleared.

Removing the flow itself behaves the same way, with one deliberate distinction between deleting and disabling. When the Attribute Flow mapping that contributed a value is **deleted**, nothing asserts that value any more, and JIM asks what should happen to the values it contributed. The default, **recall**, withdraws them: the contributing Connected System's next synchronisation, Delta included, recalls the values the mapping contributed and re-elects the next contributor in the same run, or clears the attribute (reported as a **Value cleared** outcome) when no contributor survives. Choosing to **keep** the values instead leaves them in place permanently: their provenance is severed, so nothing records that the mapping contributed them and no future synchronisation will ever recall them; a new inbound Attribute Flow, or manual removal, is then the only way to change them. The confirmation states the consequence before you commit to it.

Deleting a whole **Synchronisation Rule** offers the same choice with one mechanical difference: the deletion itself would sever the provenance the recall depends on, so the recall cannot wait for a synchronisation. Choosing recall disables the rule immediately and queues the recall as a background operation, visible on the Operations page as its own Activity with per-object outcomes; surviving contributors are re-elected, resulting changes are staged as Pending Exports (including deprovisioning for any target account the recall takes out of an export rule's scope), and deleting the rule is the operation's final step. Choosing keep deletes the rule at once and leaves the values in place with no provenance, exactly as for a kept mapping.

Deleting a whole **Connected System** extends the same choice to everything the system contributed: deprovisioning through synchronisation processes each of its objects as a normal disconnection (recall, re-election, deletion rules, downstream exports), while deleting immediately keeps the contributed data behind the same kind of stated warning. See [Removing a Connected System](../configuration/connected-systems.md).

**Disabling** a mapping, or its whole Synchronisation Rule, is a pause rather than a removal. A disabled flow contributes nothing, so a surviving contributor still takes the attribute over exactly as above; but where the disabled flow was the only contributor, its values are retained rather than cleared, ready for the flow to be re-enabled. To withdraw a sole contributor's values, delete the mapping instead.

## :material-eye-outline: Seeing resolution decisions

Synchronisation Activities record notable resolution outcomes against each object, visible on the Activity detail page (with detailed outcome tracking enabled, the default):

- **Blank asserted**<br /> A contributor with "Null is a value" positively asserted a blank for one or more attributes. The blank is deliberate and authoritative.
- **Value cleared**<br /> An attribute value was cleared because no contributor supplied a replacement: the last contributing source withdrew its value, or disconnected with no surviving contributor to re-elect. An attribute that was already blank is never reported, so these outcomes only appear when a run actually removed something.
- **Values preserved**<br /> A disconnection left the Metaverse Object with no remaining source, so the departed system's values were preserved as last known state rather than recalled (the third situation above). The outcome states how many values were kept and why, so "why does this object still have values" is answered from the run that decided it.

Together these distinguish the blanks and survivals an administrator may need to investigate: a blank asserted on purpose, a blank that happened because every source fell away, and a value that outlived its source deliberately.

The same provenance is visible per value. In the portal, a Metaverse Object's [Inspect view](../configuration/metaverse-objects.md#where-a-value-comes-from) names each value's source, and its attribute inspector lists every contributing source in priority order with the value each would supply, so you can see why one value won. Retrieving a Metaverse Object through the REST API or `Get-JIMMetaverseObject` returns, for each attribute value, the Connected System and the exact Synchronisation Rule that won resolution and contributed it. An asserted null appears as a value row flagged `nullValue` with provenance but no value, so automation can distinguish a deliberate blank from an attribute that has no contributor; consumers should treat such a row as "no value present", never as a value.

Provenance follows the winner even when the value does not change. Two contributors often hold the same value for an attribute, so a change of winner need not be a change of value: a higher-priority source joining, a priority reorder, or the deletion of the Synchronisation Rule that contributed the value all hand the value to a different rule while the value itself stays exactly as it was. The next synchronisation of the winning contributor records the hand-over, so the contributing rule shown against a value is the rule that would win resolution for it today, not the one that happened to write it first.

## :material-tune-variant: Configuring priority

Attribute Priority is configured per (Metaverse Object Type, Metaverse attribute).

### In the admin portal

Open the Metaverse Object Type (**Administration → Schema → Object Types → _type_**) and select the **Attributes** tab. The **Contributors** column shows how many inbound Synchronisation Rules contribute each attribute:

- A single contributor needs no priority (nothing to resolve).
- An attribute with more than one contributor shows a **contributors** button. Click it to expand the priority list beneath the row.

In the expanded list (highest priority at the top):

- **Drag** a contributor by its handle to reorder it; JIM renumbers the whole list so it is never left inconsistent.
- Toggle **Null is a value** per contributor.
- **Disabled Synchronisation Rules** stay in the list, greyed out, holding their position but never contributing.
- An **Accumulate** attribute lists its contributors without an order, since every one adds its values; there is nothing to drag or save.
- A **Synchronisation Rule being deleted** (its contributed values are still being recalled) drops to the bottom of the list the moment you delete it, and is gone once the recall finishes. You can reorder the surviving contributors straight away; there is no need to wait for the recall.
- Changes are held until you click **Save order**, and you can **Reset** to discard them. **Save order** asks you to confirm the change, as saving a Synchronisation Rule does, and says when it takes effect: which Connected Systems' next synchronisation processes every object of an Object Type, how many objects that is, and about how long it takes (see [When changes take effect](#when-changes-take-effect)).

### In the Attribute Flow editor

Priority is also surfaced where an Attribute Flow is created, so you are not left to discover it afterwards. When you add or edit an inbound Attribute Flow (**Administration → Synchronisation Rules → _rule_ → Attribute Flow**) targeting a Metaverse attribute another Synchronisation Rule already contributes, the dialog shows that attribute's current priority order, read-only, with your mapping in place:

- A **new** mapping appears at the bottom of the list, marked "this mapping, once saved". That is where it will be created: a newly added inbound Attribute Flow always lands at the lowest priority, so it cannot start winning resolution the moment it is saved. Promote it deliberately on the Object Type page afterwards.
- An **existing** mapping is marked "this mapping" at its current position.
- **Changing a mapping's target attribute** moves it out of one attribute's priority list and into another's, where it again arrives at the bottom rather than keeping the position it held for its old attribute.

Reordering is not offered here: priority is managed in one place, and the dialog links through to the Object Type page for it.

### Finding the attributes several systems contribute

Both surfaces above start from something you already suspect: an Object Type, or a mapping you are editing. To find the attributes worth reviewing in the first place, use the [Data Flow](../configuration/data-flow.md) view (**Administration → Schema → Data Flow**) with **Multiple contributors** switched on. It lists every inbound flow whose target Metaverse Attribute is fed by more than one Synchronisation Rule, across all Connected Systems at once, which is the set whose priority order decides anything.

**"Null is a value"** is set in this dialog rather than on the Object Type page when you are creating the mapping, because it belongs to that mapping rather than to the ordering. It can be changed later from either surface.

### Via the REST API and PowerShell

The same configuration is available for automation:

- Choose the contribution mode with the attribute: `New-JIMMetaverseAttribute -ContributionMode Accumulate` (or `Merge`) or `Set-JIMMetaverseAttribute -ContributionMode`, or `contributionMode` on the attribute endpoints. The priority order reports the attribute's mode, and a change to the order of an Accumulate or Merge attribute is refused.
- Read the ordered contributor list for an attribute.
- Replace the whole order, or move a single contributor to a position (JIM renumbers the others for you, so the list is never left in an inconsistent state).
- A replacement order must list every contributor, with one exception: a Synchronisation Rule whose deletion is still recalling its values may be left out, and stays at the bottom. If an order is refused, the error names the contributors it is missing and any listed mapping that is not a contributor.
- Set **"Null is a value"** when creating an inbound mapping: `New-JIMSyncRuleMapping -NullIsValue`. On an existing mapping it is set through the priority surface instead, with `Set-JIMMetaverseAttributePriority` or `Move-JIMMetaverseAttributePriority`, which write it in the same transaction as the ordering.

### When changes take effect

A change to priority configuration is recorded as awaiting synchronisation for the Connected System Object Type of each Synchronisation Rule whose mapping it moves. The next synchronisation of each of those Connected Systems, Delta included, processes every object of that type, so the Metaverse reflects the new order once every Connected System involved has synchronised. When they also export, use the run order in [Initialising JIM](synchronisation-pipeline.md#initialising-jim), so a higher-priority value arrives before the export that would overwrite it.

## :material-book-open-variant-outline: Related

- [Synchronisation Pipeline](synchronisation-pipeline.md): where attribute resolution sits in the inbound flow.
- [Expressions](expressions.md): an import expression that evaluates to null is treated as a positive "no value" assertion, feeding the same resolution as a direct mapping with no value.
