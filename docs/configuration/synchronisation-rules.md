---
title: Synchronisation Rules
description: What a Synchronisation Rule ties together, its direction, and where to configure its scope, matching, mappings and deprovisioning.
type: object
audience: administrator
validated: v0.16.0
---

# Synchronisation Rules

A **Synchronisation Rule** defines the complete relationship between a [Connected System](connected-systems.md) and the Metaverse. It controls which objects are in scope, how objects are matched, when new Metaverse Objects are created, and how attributes flow between systems.

```jim-diagram
archetype: flow
title: What a Synchronisation Rule does to an object on its way in
desc: 'An inbound Synchronisation Rule (1, its direction) takes a Connected System Object from the Connector Space and checks its Scoping criteria (2); an object out of scope is skipped. Object Matching Rules (3) look for an existing Metaverse Object: on a match, the Attribute mappings (5) flow values to it; on no match, Projection (4) creates one first. The Metaverse Object is the result. An outbound rule runs the same steps from the Metaverse Object to the Connected System Object, with Provisioning in place of Projection.'
in: {label: Connected System Object, note: Connector Space}
out: {label: Metaverse Object, note: the Metaverse}
container: {label: Synchronisation Rule, tag: 'Direction: inbound', badge: 1}
steps:
- {id: scope, label: Scoping criteria, badge: 2, note: 'out of scope: skipped'}
- {id: match, label: Object Matching Rules, badge: 3, next: match}
- {id: map, label: Attribute mappings, badge: 5}
detours:
- from: match
  to: map
  label: no match
  step: {id: project, label: Projection, sub: creates one, badge: 4}
```

Synchronisation Rules are the central configuration mechanism for identity synchronisation in JIM. Every Connected System needs at least one Synchronisation Rule to participate in synchronisation.

## :material-link-variant: What a Synchronisation Rule ties together

1. **Direction**<br /> Whether data flows inbound (a source system into the Metaverse) or outbound (the Metaverse out to a target system).
2. **Scoping criteria**<br /> Which objects the rule applies to.
3. **Object Matching Rules**<br /> How to match a Connected System Object to an existing Metaverse Object.
4. **Projection or Provisioning**<br /> What to do when no match is found.
5. **Attribute mappings**<br /> Which attributes to synchronise and how to transform them.

Each rule also has a name and an optional **description**, a free-text note for recording what the rule is for and why it exists. The description is shown on the rule's Details tab and changes to it are tracked in the [Configuration Change History](activities.md#configuration-change-history).

A rule's Connected System Object Type must be selected on the Connected System's Schema tab for the rule to be enabled; saving an enabled rule against a deselected Object Type is refused, as is deselecting an Object Type an enabled rule is still bound to. A deselected Object Type is out of management, and its objects are obsoleted by the next Full Import (see [What deselecting means](connected-system-schema.md#what-deselecting-means)). A disabled rule can be saved against one, which is how you keep a rule while taking its type out of management.

A saved rule's Object Types cannot be changed: its Scoping Criteria, Attribute Flows and Object Matching Rules name attributes of those types, and would read nothing on another. Neither the portal, the REST API nor PowerShell offers a way to change them once the rule is saved, and JIM refuses a save that would. To manage another Object Type, create a new Synchronisation Rule.

A saved rule's Connected System, direction and Object Types are stated in a strip beneath the page's breadcrumbs, visible on every tab. It reads left to right in the direction data flows: an import rule shows the Connected System Object Type, then the Metaverse Object Type; an export rule the reverse. The form that creates a rule orders its two Object Type pickers the same way. The Connected System's name links to it. An import rule whose Object Type has changes awaiting synchronisation shows an **Awaiting synchronisation** chip there, and an export rule with Metaverse Objects flagged for review shows **Export review pending: N**; see [When a change takes effect](configuration-changes.md#when-a-change-takes-effect).

Where a tab introduces a term you might not already know (Projection, Scoping, Object Matching Rules, Attribute Flow and others across JIM's configuration pages), an info icon sits beside it. Selecting it shows a short definition and a link to the full entry in the [glossary](../reference/glossary.md).

## :material-swap-horizontal: Direction

Each rule has a direction that determines the flow of data.

### Import (inbound)

Import rules process data from a Connected System into the Metaverse. They are used with **source systems**: systems that provide authoritative identity data (HR databases, badge systems, etc.).

An import rule:

- Reads Connected System Objects from the Connected System's Connector Space
- Attempts to join each Connected System Object to an existing Metaverse Object using the rule's object matching configuration
- Projects a new Metaverse Object if no match is found and projection is enabled
- Flows attribute values from the Connected System Object to the Metaverse Object

### Export (outbound)

Export rules push data from the Metaverse to a Connected System. They are used with **target systems**: systems that receive provisioned identity data (LDAP directories, email systems, etc.).

An export rule:

- Evaluates Metaverse Objects in the Metaverse against the rule's scoping criteria
- Provisions a new Connected System Object in the target system's Connector Space if one does not exist (and provisioning is enabled)
- Flows attribute values from the Metaverse Object to the Connected System Object
- Creates Pending Exports for any changes

When **enforce state** is set on an export rule, JIM additionally detects and remediates attribute drift in the Connected System: if an exported attribute is changed externally, the next sync run pulls it back to the Metaverse-derived value. For a [generated value](generated-values.md) on an export mapping, it is pulled back to the value JIM generated for that Connected System Object.

A change made in the Connected System is not drift when it can flow back in: that is, when the same Connected System holds the winning import Attribute Flow for the Metaverse attribute and that flow **reads the changed attribute**. For example, an import flow of `mail` into Email and an export of Email to `mail` let an edit to `mail` update Email. If the import flow reads a different attribute (Display Name built from `givenName` and `sn`, but exported to `displayName`), an edit to `displayName` could never reach the Metaverse, so it is treated as drift and corrected.

## :material-filter-outline: Scoping criteria

Scoping criteria decide which objects a rule applies to: only objects that match are processed. [Scoping Criteria](scoping-criteria.md) covers where scope is checked, what happens to objects in and out of scope, how criteria are evaluated, relative dates, and previewing a scope change.

## :material-vector-link: Object Matching Rules

Object Matching Rules decide how a Connected System Object is matched to an existing Metaverse Object, by comparing a Connected System attribute, or an [expression](object-matching-rules.md#matching-on-an-expression) worked out from the Connected System Object, with a Metaverse attribute. [Object Matching Rules](object-matching-rules.md) covers the outcomes, simple and advanced matching, and previewing a matching change.

## :material-ray-start-arrow: Projection and provisioning

These determine what happens when no match is found.

**Projection** applies to import rules. If projection is enabled, JIM creates a new Metaverse Object of the specified object type and links the Connected System Object to it. This is how new identities enter the Metaverse for the first time. If projection is not enabled, the Connected System Object remains disconnected.

Where the object type is deleted [When Authoritative Source Disconnected](metaverse.md#authoritative-source-trigger-modes), a projecting rule's Connected System should normally be one of the type's authoritative sources; otherwise the objects it creates are governed by no source. Saving a rule that newly projects from a system that is not one asks you to confirm first. A rule that only joins (projection off) never does: that is the normal way to add a system that contributes attributes without governing lifecycle.

**Provisioning** applies to export rules. If provisioning is enabled, JIM creates a new Connected System Object in the target system's Connector Space (and ultimately the target system itself, when the export Run Profile flushes Pending Exports). If provisioning is not enabled, the rule only updates objects that already exist in the target.

A rule that provisions also puts back an account deleted directly in the target system. The next import from that system (Full or Delta) marks the account's Connected System Object obsolete; the next synchronisation of that system (Full or Delta) disconnects it and, where the Metaverse Object is still in the rule's scope, stages a provisioning Create in the same run, so the next export recreates the account. Nothing else about the person needs to change. The Create is recorded on that run's Activity as an **Export Review** item. It is not staged when the disconnection schedules or performs the Metaverse Object's deletion under its type's [Deletion Rules](metaverse.md#deletion-behaviour), or when the object was already due for deletion; and an export rule that does not provision leaves the person without an account in that system.

Before provisioning, the rule's Object Matching Rules look for an unjoined account already in the target, so it is joined rather than duplicated; if several match, JIM provisions nothing for that rule and reports an **Ambiguous Provisioning Match**. See [Matching outcomes](object-matching-rules.md#matching-outcomes).

If a Metaverse Object attribute changes again while its Create export is still awaiting confirmation by a subsequent import, JIM never sends a second Create; most Connected Systems reject a Create for an object they already hold. The change is queued and sent as a single Update once the Create is confirmed, carrying whatever the latest value is by then; the Create counts as confirmed as soon as an import reports the object back at all, even if one of the values it reports still differs from what was exported, and the outstanding value then retries as part of that same Update.

If a Full Import completes without reporting the object back at all, the Create itself is retried on the next export, up to the ordinary retry limit; a Delta Import never triggers this, since it only reports changes and an object missing from its payload is not evidence that it no longer exists.

## :material-account-arrow-down-outline: Deprovisioning Action

Each export rule's **Deprovisioning Action** decides what happens to its object in the Connected System when the Metaverse Object leaves the rule's scope or is deleted. [Deprovisioning](deprovisioning.md) covers the actions, when a change to an export rule takes effect, seeing what a run deprovisioned, and previewing a destructive change.

## :material-form-textbox-password: Initial password

An export rule's **Initial Password** tab sets a password on every Connected System Object the rule creates. [Initial Password](initial-password.md) covers what you configure, and what happens after provisioning.

## :material-swap-vertical: Attribute mappings

Attribute mappings define which attributes to synchronise and how to transform them: each maps a source attribute or an expression to a target attribute. [Attribute Mappings](attribute-mappings.md) covers each kind of mapping, value processing, Initial Export Only, previewing a change, and Attribute Priority; [Generated Values](generated-values.md) and [Deriving Metaverse Attributes](derived-attributes.md) cover the two kinds that build a value rather than copy one.

## :material-playlist-check: Common workflows

**Setting up an import rule:**

1. Create a Synchronisation Rule with direction `Import`, choosing the source object type in the Connected System and the target Metaverse Object Type
2. Add attribute mappings to flow values from Connected System Object attributes onto the corresponding Metaverse Object attributes
3. Configure Object Matching Rules so incoming objects join to the right Metaverse Objects
4. Decide whether to project new Metaverse Objects when no match exists, and enable that on the rule

**Setting up an export rule with scoping:**

1. Create a Synchronisation Rule with direction `Export`, with provisioning enabled if you want JIM to create objects in the target system
2. Add attribute mappings to flow values from Metaverse Object attributes onto Connected System Object attributes
3. Add scoping criteria so only the relevant Metaverse Objects are exported (for example, only `person` objects whose `department` is `IT`)
4. Configure Object Matching Rules for the export direction
5. Decide whether to enforce state, i.e. detect and correct drift in the target system

## :material-file-document-outline: Example: a complete import rule

A complete import rule for an HR system might look like:

| Component | Configuration |
|-----------|---------------|
| **Name** | HR Import - Employees |
| **Direction** | Import |
| **Connected System** | HR Database |
| **Scoping** | `cs["employeeType"] = "FTE"` |
| **Object Matching Rule 1** | Match `cs["employeeId"]` to `mv["Employee ID"]` |
| **Projection** | Create Metaverse Object of type `Person` |
| **Attribute mappings** | `cs["givenName"]` to `mv["First Name"]` (direct) |
| | `cs["sn"]` to `mv["Last Name"]` (direct) |
| | `cs["department"]` to `mv["Department"]` (direct) |
| | `cs["employeeId"]` to `mv["Employee ID"]` (direct) |
| | `Capitalise(cs["givenName"]) + " " + Capitalise(cs["sn"])` to `mv["Display Name"]` (expression) |

This rule imports full-time employees from the HR system, joins them to existing Metaverse Objects by employee ID, creates new Metaverse Objects for new starters, and flows their attributes into the Metaverse.

## :material-magnify: Finding a Synchronisation Rule

Once a deployment has more than a handful of rules, the Synchronisation Rules list carries filters above the table so you can narrow it to the rules you care about:

| Filter | Narrows to |
|--------|------------|
| **Connected System** | Rules belonging to the systems you pick. Only systems that actually have rules are offered. |
| **Direction** | Inbound (Import) or Outbound (Export) rules. |
| **Action** | **Projects** (Import rules that create Metaverse Objects), **Provisions** (Export rules that create Connected System Objects), or **Flow Only** (rules that create nothing and only flow attribute values). |
| **Status** | Enabled or Disabled rules. |

Each filter accepts several values, and the filters combine: picking two Connected Systems and the Outbound direction shows the outbound rules of either system. Leaving a filter empty means "all".

The search box in the table's toolbar narrows whatever the filters left, matching on the rule name. It filters as you type, so there is nothing to press. Clearing the search box returns the filtered list rather than the full one, so you can keep a filter in place while searching within it.

The same filters are available to automation: see `Get-JIMSyncRule`'s `-Direction`, `-ActionType` and `-Status` parameters, and the matching query parameters on the Synchronisation Rules list endpoint in the REST API.

## :material-content-save-check-outline: Confirming a change before you save it

Saving a Synchronisation Rule can be harmless or far-reaching, and the two sit side by side on the same page: renaming a rule is beside the Deprovisioning Action that decides whether leavers' accounts are deleted. JIM judges each save by the properties that actually changed:

- **Cosmetic changes** (name, description) save straight away with no prompt.
- **Changes that affect synchronisation** (scope, mappings, Object Matching Rules, direction, enabling or disabling the rule) show a confirmation listing exactly what is changing, from which value to which, and when it takes effect; see [When a change takes effect](configuration-changes.md#when-a-change-takes-effect).
- **Destructive changes** (Deprovisioning Action, Inbound Out-of-Scope Action) additionally state, in plain terms, what the change will do: which objects will be deleted rather than disconnected, or disconnected rather than left joined.

The same rules apply across every configuration surface; see [Configuration changes](configuration-changes.md) for the full picture, including when JIM stays silent.

## :material-delete-outline: Deleting a Synchronisation Rule

Deleting a rule removes all of its configuration: Attribute Flow mappings, scoping criteria and Object Matching Rules. The deletion lives on the rule's **Danger Zone** tab, and what the confirmation asks depends on whether the rule contributed Metaverse attribute values that are still in place.

A rule contributing nothing deletes immediately after a plain confirmation. A rule that did contribute values shows the impact first (how many values, across how many Metaverse Objects, per attribute) and asks what should happen to them:

- **Recall the attribute values** (the default): the rule is disabled immediately and the recall runs as a background operation. Where another Attribute Flow also contributes an attribute, its value takes over; where none does, the attribute is cleared, and resulting changes are staged as [Pending Exports](../concepts/synchronisation-pipeline.md) for mapped target systems. Where the recall takes an object out of an export Synchronisation Rule's scope, its account in that target system is deprovisioned according to the rule's [Deprovisioning Action](deprovisioning.md), exactly as a synchronisation would deprovision it. The confirmation links the recall's Activity so you can monitor progress from the Operations page; deleting the rule is the operation's final step.
- **Keep the attribute values**: the rule is deleted at once and the values remain in place with no record of where they came from. Nothing will ever recall them; a new inbound Attribute Flow, or manual removal, is the only way to change them later. The choice is recorded on the deletion's Activity.

Removing a single **Attribute Flow mapping** from the editor offers the same choice when the mapping contributed values, with one difference in timing: a recalled mapping's values are withdrawn at the contributing Connected System's next synchronisation, Delta included, rather than by a background operation (the rule survives, so the ordinary recall machinery covers it). The choice is made when you remove the mapping and takes effect when you save the rule.

The same options exist on the other surfaces: `Remove-JIMSyncRule` and `Remove-JIMSyncRuleMapping` take `-KeepContributedValues` and state the impact in their confirmations, and the REST delete endpoints take a `keepContributedValues` query parameter. A deletion that completes straight away answers `200 OK` with what it affected; a rule deletion that queues a recall answers `202 Accepted` with the recall Activity's id. Both name any [derived Attribute Flow](derived-attributes.md) the deletion leaves with a missing input. See the [PowerShell reference](../powershell/synchronisation-rules.md) and the [interactive API reference](../../api/reference/).

For the full recall semantics (re-election, Value cleared outcomes, and how disabling differs from deleting), see [Attribute Priority](../concepts/attribute-priority.md).

## :material-tune-variant: Manage Synchronisation Rules

- **JIM portal**<br /> Synchronisation Rules area of the admin UI
- **PowerShell**<br /> [Synchronisation Rules cmdlets](../powershell/synchronisation-rules.md) (`Get-JIMSyncRule`, `New-JIMSyncRule`, etc.)
- **REST API**<br /> Synchronisation Rules endpoints in the [interactive API reference](../../api/reference/)

## :material-book-open-variant-outline: See also

- [Configuration changes](configuration-changes.md): how JIM classifies and confirms configuration changes
- [Connected Systems](connected-systems.md): the systems a Synchronisation Rule connects to
- [Concepts: Synchronisation Pipeline](../concepts/synchronisation-pipeline.md): where Synchronisation Rules fit in the import/sync/export flow
- [Concepts: Attribute Priority](../concepts/attribute-priority.md): how JIM resolves which source wins when several rules feed the same attribute, and the "Null is a value" setting
- [Concepts: JML Lifecycle](../concepts/jml-lifecycle.md): how scoping and provisioning drive joiner/mover/leaver behaviour
- [Concepts: Expressions](../concepts/expressions.md): the expression language used in scoping criteria and attribute mappings
- [Concepts: Case Sensitivity](../concepts/case-sensitivity.md): the four questions JIM asks when comparing text, and which features ask which
