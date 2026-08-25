# Attribute Priority

When more than one Connected System feeds the same Metaverse attribute, JIM has to decide which value wins. **Attribute Priority** makes that decision deterministic: each contributing Synchronisation Rule has a priority for the attribute, and the highest-priority contributor that has an opinion wins. The result no longer depends on the order your synchronisations happen to run in.

This is an **inbound** concern. It governs how values flow from Connected Systems into the Metaverse; it does not change how the Metaverse is exported back out.

## 🥇 How resolution works

For a given Metaverse Object attribute, JIM looks at every enabled import Synchronisation Rule that maps to it and evaluates them in priority order (1 is highest):

- **A contributor with a value wins.** The first contributor, in priority order, that supplies a value sets the attribute. Lower-priority contributors are not consulted.
- **A contributor with no opinion is skipped.** If a rule does not apply to this object (it is disabled, no object from its Connected System is joined, or the joined object is out of the rule's scope), it is passed over and the next priority is considered.
- **If nobody has an opinion, the attribute has no contributor** and is left unset.

Because priority is held per Synchronisation Rule mapping, the same Connected System can rank differently for different attributes (it might be authoritative for `department` but secondary for `jobTitle`), and a single system can even contribute through several differently-scoped rules at different priorities to express "this system owns these objects, that system owns those".

A single-source attribute (only one rule maps to it) needs no configuration and is unaffected.

**The contributors are always different Synchronisation Rules; one rule cannot contribute twice.** A Synchronisation Rule can carry at most one Attribute Flow per target attribute, and JIM refuses a second, so a priority list is never an ordering of two mappings from the same rule. To fall back between two source attributes of the same system within one rule, use a single expression mapping (`Coalesce(cs["jimBadgeColour"], cs["roomNumber"])`); to give the same system two positions in the priority order, use two differently-scoped Synchronisation Rules, as described below.

## 🎯 Giving one system authority over a subset of objects

Because the priority list is a list of **Synchronisation Rules**, not of Connected Systems, a system can appear in it more than once through rules with different Scoping Criteria. That is how you express "this system is authoritative for these objects, that system is authoritative for the rest" without any extra machinery.

The usual shape is two import rules on the same Connected System:

- an **unscoped** rule that applies to everything it imports, sitting low in the priority order; and
- a **narrowly scoped** rule covering just the exception objects, sitting at the top.

An object inside the scoped rule's criteria is contributed by that rule and wins. An object outside them is not contributed by that rule at all (the rule has no opinion for it, exactly as a disabled rule has none), so the next priority decides. Authority is therefore per object, not per system, and the two rules never fight: at most one of them applies to any given object.

Two consequences are worth stating plainly:

- **Scoping Criteria are evaluated against Connected System Object attributes, so an object can move in and out of a rule's scope as its own attributes change.** Renaming a group so it matches an exceptions pattern transfers authority for it on the next synchronisation. That is the intended behaviour, and it is quiet: nothing announces it beyond the resolution decision itself.
- **The losing system is not corrected automatically.** A change made directly in a system that loses resolution never reaches the Metaverse, but it stays in that system until an export Synchronisation Rule with **Enforce State** targets it, at which point export re-evaluation stages a corrective Pending Export and puts the winning value back. Without such a rule the losing system simply remains divergent; the Metaverse and every other system are protected either way.

## ⛔ "Null is a value"

By default, if the highest-priority source has **no** value for an attribute, JIM falls through to the next source. Sometimes that is wrong: when the authoritative source clears a value, you want the clear to propagate, not to be back-filled from a stale secondary copy.

Enabling **"Null is a value"** on a contributor changes that. If that contributor is connected and in scope but supplies no value, JIM stops there and asserts "no value" for the attribute: it is cleared everywhere downstream, and lower-priority sources are not consulted. This is distinct from the contributor simply not applying to the object (a rule with no opinion is always skipped, regardless of this setting).

Typical uses:

- **A manager or department cleared at the authoritative source** must propagate as a clear, not be resurrected from a directory that still holds the old value.
- **A primary system migration** where the new system is authoritative for the people it knows about (including their blanks), while a legacy system remains the only source for people not yet migrated.

> Asserting null is powerful: a misbehaving high-priority import (an empty file, a truncated delta) becomes a mass-clearing event rather than a harmless no-op. Treat "Null is a value" as an authoritative, deliberate setting.

## 🧮 Determinism and ties

Resolution is always deterministic. Two contributors to one attribute cannot share a priority (this is prevented when you configure the order), but if it ever occurs, JIM breaks the tie consistently rather than by timing, so a sync run never produces a different winner than the one before.

When you add a new import mapping to an attribute that already has contributors, it is placed at the **lowest** priority. A newly added source therefore never silently takes over an attribute; you promote it explicitly when you want it to win.

## 🔁 When the winning source disconnects or withdraws

If the source that currently provides an attribute's value disconnects (its object is removed from that Connected System) or falls out of its Synchronisation Rule's scope, JIM does not simply blank the attribute. It re-elects the next contributor: a still-connected, in-scope lower-priority source takes over, and its value flows into the Metaverse in place of the departed one. Only when no other source contributes is the attribute cleared.

This means an authoritative source leaving hands an attribute down to the next source rather than dropping it, so downstream systems receive the fallback value instead of an unintended clear. The next contributor is resolved exactly as in normal flow, so if it has **"Null is a value"** set and supplies no value, the attribute is asserted null rather than handed further down.

Re-election covers every attribute type, including references: a manager or group membership recalled from a departing source is handed to the surviving contributor within the same synchronisation run, not left blank until that source next synchronises. It also holds when the surviving source carries the identical value; the value simply remains, now attributed to the surviving contributor.

The same hand-over applies when the winning source stays connected but simply stops supplying a value, without "Null is a value" set: for example, an expression that starts evaluating to null, or a source attribute that becomes unpopulated. The next-priority contributor takes over in the same synchronisation run, exactly as it would if the winning source had disconnected. Only when no other source contributes is the attribute cleared.

## 🔍 Seeing resolution decisions

Synchronisation Activities record notable resolution outcomes against each object, visible on the Activity detail page (with detailed outcome tracking enabled, the default):

- **MVO Null Asserted**<br /> A contributor with "Null is a value" positively asserted a blank for one or more attributes. The blank is deliberate and authoritative.
- **MVO No Contributor**<br /> An attribute value was cleared because no contributor supplied a replacement: the last contributing source withdrew its value, or disconnected with no surviving contributor to re-elect. An attribute that was already blank is never reported, so these outcomes only appear when a run actually removed something.

Together these distinguish the two kinds of blank an administrator may need to investigate: one that was asserted on purpose, and one that happened because every source fell away.

The same provenance is visible per value: retrieving a Metaverse Object through the REST API or `Get-JIMMetaverseObject` returns, for each attribute value, the Connected System and the exact Synchronisation Rule that won resolution and contributed it. An asserted null appears as a value row flagged `nullValue` with provenance but no value, so automation can distinguish a deliberate blank from an attribute that simply has no contributor; consumers should treat such a row as "no value present", never as a value.

Provenance follows the winner even when the value does not change. Two contributors often hold the same value for an attribute, so a change of winner need not be a change of value: a higher-priority source joining, a priority reorder, or the deletion of the Synchronisation Rule that contributed the value all hand the value to a different rule while the value itself stays exactly as it was. The next synchronisation of the winning contributor records the hand-over, so the contributing rule shown against a value is the rule that would win resolution for it today, not the one that happened to write it first.

## 🛠️ Configuring priority

Attribute priority is configured per (Metaverse Object Type, Metaverse attribute).

### 🖥️ In the admin portal

Open the Metaverse Object Type (**Administration → Schema → Object Types → _type_**) and select the **Attributes** tab. The **Contributors** column shows how many inbound Synchronisation Rules contribute each attribute:

- A single contributor needs no priority (nothing to resolve).
- An attribute with more than one contributor shows a **contributors** button. Click it to expand the priority list beneath the row.

In the expanded list (highest priority at the top):

- **Drag** a contributor by its handle to reorder it; JIM renumbers the whole list so it is never left inconsistent.
- Toggle **Null is a value** per contributor.
- **Disabled Synchronisation Rules** stay in the list, greyed out, holding their position but never contributing.
- Changes are held until you click **Save order**, and you can **Reset** to discard them.

### 🧭 In the Attribute Flow editor

Priority is also surfaced where an Attribute Flow is created, so you are not left to discover it afterwards. When you add or edit an inbound Attribute Flow (**Administration → Synchronisation Rules → _rule_ → Attribute Flow**) targeting a Metaverse attribute another Synchronisation Rule already contributes, the dialog shows that attribute's current priority order, read-only, with your mapping in place:

- A **new** mapping appears at the bottom of the list, marked "this mapping, once saved". That is where it will be created: a newly added inbound Attribute Flow always lands at the lowest priority, so it cannot start winning resolution the moment it is saved. Promote it deliberately on the Object Type page afterwards.
- An **existing** mapping is marked "this mapping" at its current position.
- **Changing a mapping's target attribute** moves it out of one attribute's priority list and into another's, where it again arrives at the bottom rather than keeping the position it held for its old attribute.

Reordering is not offered here: priority is managed in one place, and the dialog links through to the Object Type page for it.

### 🔀 Finding the attributes several systems contribute

Both surfaces above start from something you already suspect: an Object Type, or a mapping you are editing. To find the attributes worth reviewing in the first place, use the [Data Flow](../configuration/data-flow.md) view (**Administration → Schema → Data Flow**) with **Multiple contributors** switched on. It lists every inbound flow whose target Metaverse Attribute is fed by more than one Synchronisation Rule, across all Connected Systems at once, which is the set whose priority order decides anything.

**"Null is a value"** is set in this dialog rather than on the Object Type page when you are creating the mapping, because it belongs to that mapping rather than to the ordering. It can be changed later from either surface.

### 🔧 Via the REST API and PowerShell

The same configuration is available for automation:

- Read the ordered contributor list for an attribute.
- Replace the whole order, or move a single contributor to a position (JIM renumbers the others for you, so the list is never left in an inconsistent state).
- Set **"Null is a value"** when creating an inbound mapping: `New-JIMSyncRuleMapping -NullIsValue`. On an existing mapping it is set through the priority surface instead, with `Set-JIMMetaverseAttributePriority` or `Move-JIMMetaverseAttributePriority`, which write it in the same transaction as the ordering.

### When changes take effect

A change to priority configuration takes effect as objects are next synchronised; it does not, by itself, re-synchronise existing objects. A Delta Synchronisation applies it only to recently-changed objects, so after a significant change run a Full Synchronisation of the affected objects so the Metaverse reflects the new order everywhere.

## Related

- [Synchronisation Pipeline](synchronisation-pipeline.md): where attribute resolution sits in the inbound flow.
- [Expressions](expressions.md): an import expression that evaluates to null is treated as a positive "no value" assertion, feeding the same resolution as a direct mapping with no value.
