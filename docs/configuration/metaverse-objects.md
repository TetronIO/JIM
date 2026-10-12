---
title: Metaverse Objects
description: Where each of a Metaverse Object's values comes from, and why it is connected to a Connected System, or not, from its own page.
type: object
audience: administrator
---

# Metaverse Objects

A Metaverse Object is one identity in the [Metaverse](metaverse.md): a single `User`, `Group`, or an object of a type you defined. Its page answers the questions asked of it most: where does this value come from, and why does this person have an account in that system, or not have one in this one? Both answers are read-only, and are available through the REST API and PowerShell too.

## Where a value comes from

A Metaverse Object's **Details** tab offers three views: **Form**, **Tabs** and **Inspect**. Form and Tabs show the values. **Inspect** shows where each value comes from, and is the view to use when a value looks wrong. You can also open it directly with `?view=inspect` on the object's address.

- **Where this {Object Type} gets its values**<br /> A bar at the top shows each source's share of the object's attributes, with a key underneath. Select a source in the key to filter the table to it; select it again to clear the filter. When every attribute comes from one source, the bar gives way to a single sentence naming it.
- **Source**<br /> Each attribute's row names its source, with a dot in the same colour as that source's share of the bar: the Connected System and the Synchronisation Rule that contributed the value, or **Source not recorded** for values set before JIM recorded their source. A rule deleted since it contributed the value reads "rule deleted", and the Connected System is still named. A value an Attribute Flow generated (see [Generated values](generated-values.md)) ends in **Generated Value**, for example "HR · HR Import Users · Generated Value", and has its own share of the bar. A multi-valued attribute whose values came from more than one source reads **Several sources**. Hover over a source to read it in full; the inspector links to the Connected System and the Synchronisation Rule.
- **Group by**<br /> Group the attributes by **Source**, to see everything one Synchronisation Rule contributes to this object (a header names each source, in place of the Source column), or by **Category**. JIM remembers your choice.

Select an attribute to open its inspector beside the table. The inspector answers "why is this value what it is":

- **Current value**<br /> The value, its source (the Connected System and Synchronisation Rule it came from), the Connected System Object that supplied it, and the Activity that last set it. For a generated value, a **Source type** of **Generated Value** replaces the Connected System Object, since the Attribute Flow produced the value rather than reading it. A source that positively asserts there is no value (see [Null is a value](../concepts/attribute-priority.md#null-is-a-value)) is shown as asserting no value.
- **Every source**<br /> Every Synchronisation Rule that can contribute the attribute, in [priority order](../concepts/attribute-priority.md), with the value each would supply for this object today (for a Generated Value Attribute Flow, the value it generated). The one supplying the current value is marked **In use**; a source with a value that lost on priority is marked **Outranked**; a joined source with nothing to supply is marked **No value**. A source whose object is not joined, whose Synchronisation Rule or Attribute Flow is disabled, or whose value cannot be worked out outside a synchronisation run says so. **Change priority** opens the Metaverse Object Type, where the order is set.
- **History**<br /> The attribute's most recent changes (up to 50), newest first, with the Activity and Synchronisation Rule behind each. **Open in Timeline** takes you to the object's **Changes** tab for its full change history.

Nothing in the Inspect view changes data: working out what each source would supply reads the joined Connected System Objects and evaluates each Attribute Flow without writing anything.

The same information is available through the REST API (`GET /api/v1/metaverse/objects/{id}/provenance`, and `.../attributes/{attributeId}/provenance` for one attribute; see the [interactive API reference](../../api/reference/)) and PowerShell ([`Get-JIMMetaverseObjectProvenance`](../powershell/metaverse.md#get-jimmetaverseobjectprovenance)).

A value's source is not only in the Inspect view: the object's **Changes** tab names each attribute change's source alongside the change itself, and a [Pending Export's detail page](connected-systems.md#pending-exports) shows both where the outbound value came from and which Attribute Flow staged it. Both link back into Inspect so you can jump straight from a change or a queued export to the full picture.

## Why it is connected, and why it is not

A Metaverse Object's **Connections** tab lists every Connected System Object joined to it, with its role, its state and a per-row **Preview Sync** (see [Sync Preview](sync-preview.md)). It also answers the two questions a helpdesk asks most: why does this person have an account in that system, and why don't they have one in this one? The tab is available to Administrators.

**Why it is connected.** Expand a connection's row to see:

- **How it joined**<br /> One sentence naming how the Connected System Object came to be joined and which Synchronisation Rule was responsible: projected by an import rule, provisioned by an export rule, joined by matching under an import rule, joined by matching on the Connected System's own Object Matching Rules (no rule is responsible), or joined by an export rule that found an existing object instead of provisioning a new one. The date it joined follows, with links to the Activity that made the join and to the rule. JIM records this at the moment of joining. For objects joined before JIM recorded it, the rule is taken from Activity history while that history is still kept, marked **Taken from Activity history**; where neither source holds it, the sentence says it was not recorded rather than guessing. A rule deleted since is still named, followed by "(since deleted)".
- **Scoping**<br /> Each enabled Synchronisation Rule relevant to the connection, evaluated against current values: import rules against the Connected System Object's values for a source, export rules against the Metaverse Object's values for a target. It is labelled with the time it was evaluated, because it answers whether the connection is in scope now, not why it joined. A connection that is now out of scope of a rule (an import rule set to keep the join, or an export rule whose deprovisioning is under way) shows that rule as **Not in scope**, with the criteria it fails.
- **Object Type conflicts**<br /> Another enabled export rule that targets the same Connected System for a different object type cannot connect this Metaverse Object, because a Metaverse Object has one Connected System Object per Connected System and this connection already holds it. The row says so, naming the rule.

**Why it is not connected.** Below the table, the **Not connected** section lists every enabled export Synchronisation Rule for the Metaverse Object's type whose Connected System holds no object joined to it. It is collapsed until you open it; its heading counts the entries. When there are none it says the object is connected everywhere an enabled export rule targets. Rules on a Connected System being deleted are left out; rules on a disabled Connected System are listed, and their hint says the Connected System is disabled. A connection still being provisioned (**Pending Export** or **Awaiting confirmation**) is already a row in the table above, so it never appears here.

Each entry shows the Connected System, the rule, one reason and a one-line hint naming what it fails on, without values:

| Reason | What it means | What its expansion shows |
|---|---|---|
| **Not in scope** | The object fails the rule's scoping criteria. | **To come into scope**: each failing condition in words, with the current value, then the rule's criteria evaluated. |
| **Rule doesn't provision** | The object is in scope, but the rule's **Provision to Connected System** is off and no object exists to join. | What would connect it: switch provisioning on, or create an object in the Connected System that joins. |
| **Rule misconfigured** | A criterion uses a comparison its attribute's type cannot use, so the rule's scoping cannot be evaluated. | The criterion, and that it needs correcting or removing in the rule. |
| **Not yet provisioned** | The object is in scope and the rule provisions, but nothing has been staged. | When the object is marked for an export scope review (as every object of the type is after a change to the rule that can bring objects into scope), the next synchronisation of any Connected System stages its provisioning, and the hint says so. Otherwise provisioning is staged the next time the object's attribute values change, or when the rule is next saved with such a change; see [When a change to an export rule takes effect](deprovisioning.md#when-a-change-to-an-export-rule-takes-effect). |

**Copy summary** on an entry puts a plain-text explanation on the clipboard, ready for a ticket or a message: the object's name, the Connected System, the reason, the rule, the bullets and the time it was evaluated in UTC, so it reads the same wherever it is pasted. You do not need to expand the entry first. Where your browser does not allow JIM to use the clipboard (over plain HTTP, for example), a dialog shows the summary already selected, ready to copy.

**Reading the criteria.** The criteria are drawn as the rule defines them: each group says whether all or any of its conditions must be met, and whether it was. Each condition shows what the object held and its outcome: **Met**, **Not met**, **No value** (an attribute with no value fails every comparison except one that requires no value), a condition that references no attribute (for example, after its attribute was deleted), or one that cannot be evaluated. A rule with several top-level groups is in scope when any one of them is met, and the explanation presents them as alternatives. Relative dates resolve to the instant shown. Where an attribute holds several values, the condition shows the value it compared and says how many more there were. Values of credential attributes are never shown.

Nothing on the Connections tab changes data: the explanations are evaluated in memory against values already stored.

The same explanations, in the same words, are available through the REST API (`GET /api/v1/metaverse/objects/{id}/connections`, adding `?includeNotConnected=true` for the Not connected entries; see the [interactive API reference](../../api/reference/)) and PowerShell ([`Get-JIMMetaverseObjectConnection`](../powershell/metaverse.md#get-jimmetaverseobjectconnection)).

## Related

- [Metaverse](metaverse.md): Object Types, attributes and pending deletions.
- [Attribute Priority](../concepts/attribute-priority.md): which source's value wins.
- [Sync Preview](sync-preview.md): what synchronising one object would do, changing nothing.
- [Scoping Criteria](scoping-criteria.md): which objects a Synchronisation Rule applies to.
