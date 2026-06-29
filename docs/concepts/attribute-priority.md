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

## 🛠️ Configuring priority

Attribute priority is configured per (Metaverse Object Type, Metaverse attribute). The priority order, and the "Null is a value" flag, are managed through the REST API and PowerShell module:

- Read the ordered contributor list for an attribute.
- Replace the whole order, or move a single contributor to a position (JIM renumbers the others for you, so the list is never left in an inconsistent state).

A change to priority configuration takes effect as objects are next synchronised; it does not, by itself, re-synchronise existing objects. After a significant change, run a full synchronisation of the affected objects so the Metaverse reflects the new order everywhere.

## Related

- [Synchronisation Pipeline](synchronisation-pipeline.md): where attribute resolution sits in the inbound flow.
- [Expressions](expressions.md): an import expression that evaluates to null is treated as a positive "no value" assertion, feeding the same resolution as a direct mapping with no value.
