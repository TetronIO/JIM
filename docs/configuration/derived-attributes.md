---
title: Deriving Metaverse Attributes
description: "Derive a Metaverse Attribute from other Metaverse values with mv[...] in an import Attribute Flow, so every target sees one value."
type: object
audience: administrator
---

# Deriving Metaverse Attributes

An import Attribute Flow's expression can read the Metaverse Object it flows to with `mv["..."]`, alongside the Connected System Object with `cs["..."]`. The result is held in the Metaverse like any other contributed value, so you define an Email or a User Principal Name once, and every target sees the same value:

```jim-diagram
archetype: flow
title: Derived Attribute Flows, evaluated in steps
desc: 'JIM evaluates an object''s import Attribute Flows in steps. Step 1 is the ordinary Attribute Flow, which settles Account Name from the Connected System Object; step 2 evaluates Email, which reads mv["Account Name"]; step 3 evaluates User Principal Name, which reads mv["Email"]. Each result is held on the Metaverse Object like any other contributed value, so every target sees the same value.'
in: {label: Connected System Object, note: 'read with `cs["..."]`'}
out: {label: Metaverse Object, note: every target sees one value}
container: {label: Evaluation steps, tag: 'later steps read `mv["..."]`'}
steps:
- {id: s1, label: Ordinary Attribute Flow, badge: 1, sub: settles Account Name}
- {id: s2, label: Email, badge: 2, sub: reads Account Name}
- {id: s3, label: User Principal Name, badge: 3, sub: reads Email}
```

| Source (import expression) | Target |
|---|---|
| `Lower(mv["Account Name"]) + "@corp.local"` | `Email` |
| `mv["Email"]` | `User Principal Name` |

JIM recognises such a flow from its expression; there is nothing to declare. It is a **Derived Attribute Flow**. The REST API marks one with `derived` on the mapping, and `Get-JIMSyncRuleMapping` with a `Derived` property, each giving the Metaverse attributes it reads and its step.

**Evaluation order: steps.** Email reads Account Name and User Principal Name reads Email, so they must be evaluated in that order. JIM works the order out across every import Synchronisation Rule of the Metaverse Object Type, and evaluates in **steps**: step 1 is the object's ordinary Attribute Flow, and each later step evaluates the derived flows whose inputs are all settled by the step before. Above, Account Name is settled at step 1, Email at step 2 and User Principal Name at step 3, so the type has three steps. Every derived contributor of an attribute is evaluated at that attribute's step, in [Attribute Priority](attribute-mappings.md#attribute-priority) order. The order of mappings inside a Synchronisation Rule does not matter, and two runs over the same data produce the same values. Disabled flows count when steps are numbered, so enabling one never renumbers the others.

**A derived value is an ordinary contribution.** It wins or loses under Attribute Priority, "Null is a value" applies to it, [Missing Input Behaviour](attribute-mappings.md#missing-input-behaviour) applies to an absent `mv["..."]` input exactly as to an absent `cs["..."]` one, and it is recalled with its rule. It reaches export evaluation, drift detection and change history in the same synchronisation as the inputs it was built from. A [generated value](generated-values.md) can be an input, and a generated value's base expression can read `mv["..."]`: the generated value is resolved at its step before the next step reads it.

**Loops are refused.** If Display Name reads Mail Nickname and Mail Nickname reads Display Name, neither can be evaluated first. JIM refuses the save, from the portal, the REST API (`400`) and PowerShell alike, naming every attribute and Synchronisation Rule in the loop, whichever of the rules you are saving:

```text
Saving would create a dependency cycle: Mail Nickname (Synchronisation Rule 'AD Import') reads Display Name, which (Synchronisation Rule 'HR Import') reads Mail Nickname.
```

An expression that reads its own target (`mv["Email"]` in the flow to Email) is a loop of one. Disabled flows count, so enabling one later can never complete a loop; disabling one of a loop's flows is how you break a loop that already exists. JIM also refuses an `mv["..."]` name that is not an attribute of the Metaverse Object Type and a Reference attribute as an input or as the target, because references are resolved later in synchronisation than derived flows are evaluated.

**Non-repeatable functions warn.** A derived expression that calls `Now()`, `Today()`, `RandomPassword()`, `RandomPassphrase()`, `DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today` or `Guid.NewGuid()` produces a different value on every synchronisation, which changes the Metaverse value and can export it every time. JIM saves it and warns: a `warnings` entry on the REST response, and a warning from `New-JIMSyncRuleMapping`, `Set-JIMSyncRuleMapping` and `Set-JIMSyncRule`.

**Which synchronisation evaluates a derived flow.** Like every Attribute Flow on a rule, a derived flow is evaluated only in the synchronisation of its own Connected System (the hosting system), for objects joined to that system and in scope for the rule. When it reads an attribute another Connected System contributes, that system's synchronisation changes the input but not the derived value. So:

- **Synchronise sources before the systems that derive from them**<br /> If Email is derived on the HR rule from a Region the directory contributes, synchronise the directory before HR, and Email is right in the same cycle. [Initialising JIM](../concepts/synchronisation-pipeline.md#initialising-jim) and [Schedules](schedules.md#common-workflows) say the same.
- **The wrong order costs one cycle, never more**<br /> When a synchronisation of any system, or a change made outside synchronisation (a Synchronisation Rule deletion's recall, Synchronised Deprovisioning, the stranded value sweep, a direct change to a Metaverse Object), changes an attribute that a derived flow on another system reads, JIM marks that object's Connected System Object in the hosting system. The hosting system's next synchronisation, delta included, processes the object even though nothing about it changed there, and re-derives the value. Nothing is lost either way: until then the object keeps its previous derived value.

**Removing or disabling an input's last contributor.** Deleting or disabling an Attribute Flow, or a whole Synchronisation Rule, can leave a derived flow with nothing to read: Email reads Account Name, and you delete the only flow that contributes Account Name. JIM lets the change go ahead and names each derived flow it leaves with a missing input, directly or through other derived attributes (User Principal Name reads Email, which reads Account Name). That flow's Missing Input Behaviour then decides what it contributes, so set one that suits it. Only an attribute losing its **last** enabled contributor counts; while another contributor remains, nothing is reported.

- **REST API**<br /> `dependentDerivedFlows` on the responses to deleting a mapping, updating a mapping, updating a Synchronisation Rule and deleting a Synchronisation Rule, in a schema refresh preview's `dependents`, and on the import-schema response when it disables or removes dependents. Each entry names the derived flow's mapping, target attribute, Synchronisation Rule and Connected System, and each input it lost (`missingInputs`), with the derived attributes in between (`via`) when it reaches that input indirectly.
- **PowerShell**<br /> `Remove-JIMSyncRuleMapping`, `Set-JIMSyncRuleMapping`, `Set-JIMSyncRule`, `Remove-JIMSyncRule` and `Import-JIMConnectedSystemSchema` write a warning: a summary line, then one line per derived flow. They never prompt and never stop.
- **Previews**<br /> An [Attribute Flow change preview](attribute-mappings.md#previewing-an-attribute-flow-change) lists the same derived flows as warnings before you save, and names derived flows on other Connected Systems' rules that read what the change alters. [Sync Preview](sync-preview.md) shows derived values as ordinary Attribute Flow changes.
- **Portal**<br /> Before it removes an Attribute Flow, applies an edit that disables or retargets one or changes its expression, or saves a Synchronisation Rule switched off or deletes one, the portal asks first, listing each derived flow that would be left without an input, its Synchronisation Rule and what it reads (`Email (via Account Name)` for one reached through another derived attribute). With nothing affected, you see the usual confirmation.

**In the portal.** The Attribute Flow tab marks each derived flow with a **Derived · step N** chip beside its target; hover over it to see what it reads and when it runs. In the table view, the **Mapping Type** filter lists derived flows under **Derived**, apart from ordinary Expressions: pick it alone to see only derived flows, or with **Generated Mapping** to see the generated values and the flows derived from them together. In the Attribute Flow dialog:

- **Insert attribute**<br /> Under the Expression (and a generated value's base expression), this menu puts `mv["..."]` or `cs["..."]` at the cursor. Type in its filter to narrow the list. Metaverse attributes that cannot be read are listed but greyed out, saying why: a Reference attribute (not supported yet) and the flow's own target. On an export Synchronisation Rule the menu offers every Metaverse attribute, whether or not Metaverse-Derived Attribute Flows are enabled, and no Connected System attributes, because an export expression reads the Metaverse Object alone.
- **The Derived Attribute Flow panel**<br /> While the expression reads the Metaverse, a panel states what it reads, its step and the chain of steps leading to it, updating as you type.
- **Checked as you type**<br /> A loop is shown as soon as you type it, listing each attribute and the attribute it is worked out from, and **Update Attribute Flow** stays disabled until you break it; the same check refuses the save. A non-repeatable function shows its warning, and you can still save.
