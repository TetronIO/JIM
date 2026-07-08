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

### 🔧 Via the REST API and PowerShell

The same configuration is available for automation:

- Read the ordered contributor list for an attribute.
- Replace the whole order, or move a single contributor to a position (JIM renumbers the others for you, so the list is never left in an inconsistent state).

### When changes take effect

A change to priority configuration takes effect as objects are next synchronised; it does not, by itself, re-synchronise existing objects. A Delta Synchronisation applies it only to recently-changed objects, so after a significant change run a Full Synchronisation of the affected objects so the Metaverse reflects the new order everywhere.

## Related

- [Synchronisation Pipeline](synchronisation-pipeline.md): where attribute resolution sits in the inbound flow.
- [Expressions](expressions.md): an import expression that evaluates to null is treated as a positive "no value" assertion, feeding the same resolution as a direct mapping with no value.
