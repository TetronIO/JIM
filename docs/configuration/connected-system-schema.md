---
title: Connected System Schema
description: "What the Schema tab's selections decide, how to refresh a schema safely, and what happens when an object changes Object Type."
type: object
audience: administrator
---

# Connected System Schema

A Connected System's schema is the Object Types and attributes JIM discovered in it, and your selection among them decides what JIM reads. This page covers what a selection change does, previewing it, refreshing the schema, and an object that changes Object Type.

## What deselecting means

The Schema tab's ticks decide what JIM **reads**, and the two kinds of tick behave very differently when you clear them.

| What you deselect | What actually happens |
|---|---|
| An **Object Type** | JIM takes it out of management. The Connector stops returning its objects, so the next **Full Import** finds every Connected System Object already imported from it missing and marks it obsolete (unless an object also matches another selected Object Type, in which case it [changes Object Type](#when-an-object-changes-object-type) instead of disappearing), and the following synchronisation disconnects the joined ones from their Metaverse Objects. Their contributed values are withdrawn (see below), and a Metaverse Object left with no connectors may become eligible for deletion under its type's deletion rule. Nothing happens on save, and a Delta Import does not act on it; the Full Import does. |
| An **attribute** | JIM stops fetching it. The values already held for it stay on the Connected System Objects, and any Attribute Flow reading it goes on flowing them, without them ever being refreshed again. |

Deselecting an Object Type is the same as deselecting a partition or a container: all three take objects out of an import that still runs, so the next Full Import does not find them and marks them obsolete. They go through the same deletion detection as any other missing object, so a Full Import Run Profile's [deletion limits](run-profiles.md#safeguards) hold them back too, which is worth setting before taking a large Object Type out of management.

**Disable the Synchronisation Rules first.** An Object Type cannot be deselected while an enabled Synchronisation Rule is still bound to it, and a Synchronisation Rule cannot be enabled against an Object Type that is not selected. Saving either is refused with a message naming the rules. The two refusals keep a rule from ever running against a type JIM no longer imports; an outbound rule would otherwise go on provisioning into it, and would provision the obsoleted objects all over again. So taking a type out of management is two deliberate steps: disable (or delete) its Synchronisation Rules, then deselect it. If a Full Import ever finds a deselected Object Type with an enabled Synchronisation Rule still bound to it, it leaves that type's objects as they are and completes with a warning naming the rules to disable.

**An attribute a Scoping Criterion reads cannot be deselected.** A [Scoping Criterion](scoping-criteria.md) comparing it, or reading it in an Expression, would go on deciding which objects its Synchronisation Rule manages from values that are never refreshed, and objects imported afterwards would have no value at all. Saving is refused with a message naming each criterion; change them first. An Attribute Flow or Object Matching Rule Expression reading the attribute does not block the save, but its Activity completes with a warning naming each one. An attribute deselected before this check existed does not block an unrelated save.

Attribute selection is an **import-side** idea only. Synchronisation and export do not consult it: an export Attribute Flow whose target attribute is deselected still writes it.

### Obsoletion and contributed values

Each Object Type carries **Remove Contributed Attributes On Obsoletion**, which decides what happens to the Metaverse values one of its objects contributed when that object is obsoleted:

- **On** (the default) withdraws them. Where another Connected System still contributes the attribute it is handed over to that source; where none does, the value is cleared. A [deletion grace period](metaverse.md#deletion-behaviour) preserves a value with no surviving contributor until the grace window resolves.
- **Off** leaves them on the Metaverse Object. They stop tracking anything from that point, and nothing reports them as stale.

### Previewing a schema change

The Schema tab offers a **Preview Changes** button beside **Save Changes**, which answers what your edited selection would do without saving it.

The preview reports:

| Transition | What it means |
|---|---|
| Disconnects from its Metaverse Object | Joined objects of an Object Type you are deselecting: the next Full Import obsoletes them and the following synchronisation disconnects them. |
| Leaves import scope | Unjoined objects of an Object Type you are deselecting, which the next Full Import obsoletes with nothing to disconnect. |
| Becomes eligible for deletion | Metaverse Objects those disconnections would leave eligible for deletion under their type's deletion rule. |
| Stops being imported, stays joined | Objects holding a value for an attribute you are deselecting: that attribute freezes, and nothing else changes. |
| Imported again | Objects, or attribute values, that would start tracking the Connected System again. |
| Contributed values withdrawn | Metaverse Objects that would have this system's contributed values withdrawn when their obsolete objects are next synchronised. |
| Contributed values kept | The inverse: values that would be left in place instead. |

Two things the preview is deliberately careful about:

- **Only the objects that hold a value are counted for an attribute.** An object with nothing stored for a deselected attribute has nothing to freeze, so counting it would inflate the answer with objects the change does not touch.
- **The obsoletion toggle is counted against the objects already obsolete and still joined**, which are the only ones whose fate it changes now. Objects obsoleted in future are governed by the setting too, but there is no population to count yet.

Validation names what stands in the way and what would go on running over frozen data. Deselecting an Object Type that an enabled Synchronisation Rule still manages is a blocking finding naming the rules, because saving it is refused; the preview counts nothing until those rules are disabled. Attribute Flow mappings still reading an attribute you are deselecting are named as a warning. Deselecting an External ID is refused outright. Selecting or deselecting an Object Type that overlaps another selected one (a class that inherits from it, or a structural class beside an auxiliary one) is named as a warning, because the objects carrying both [change Object Type](#when-an-object-changes-object-type); for a deselection, the obsoletion counts above overstate what happens to those objects, since they are kept rather than obsoleted. The warning relies on the class kinds and inheritance a schema refresh records, so it appears only for the LDAP Connector, and only once the schema has been refreshed since JIM began recording them.

Save after previewing and the confirmation opens with the preview's own sentence, and the change's [Activity](activities.md) records which preview informed it. Edit the selection after previewing and the preview is marked stale and contributes nothing, because it now describes a different change.

The same evaluation is available to automation: [`New-JIMConfigurationChangePreview -ConnectedSystemId -SchemaObjectType`](../powershell/previews.md) in PowerShell, or `POST connected-systems/{id}/schema-selection/preview` in the [REST API](../../api/reference/). Every omission means "leave this as it stands", so a request changing one flag cannot accidentally propose deselecting a whole Object Type.

See [Configuration changes](configuration-changes.md#previewing-a-change-before-you-make-it) for how previews work generally.

## When an object changes Object Type

A Connected System Object belongs to one Object Type, and JIM recognises it by that Object Type's External ID. An object that starts arriving under a different Object Type therefore does not look like an existing object changing: it looks like a new object of the new type, while the old one seems to have disappeared. Taken at face value, the next Full Import would retire the old one, which can delete its Metaverse Object and deprovision it from every other Connected System before anything joins the new one.

JIM does not let that happen unannounced. For a Connector whose External IDs are unique across the whole Connected System, such as the [LDAP Connector](../connectors/jim-ldap-connector.md) (an entry's objectGUID, entryUUID or nsUniqueId never repeats, whatever its class), the import recognises the object and **refuses the change**:

- The object's Run Profile execution item records an **Object Type Changed** error naming the Object Type JIM holds it as, the Object Type it now arrives as, and its External ID. The [Activity](activities.md) completes with a warning, even when the refused objects are all it recorded, so a refusal never stops the Schedule the run belongs to.
- Nothing is lost. Nothing is created, and the existing Connected System Object is kept exactly as it is: it stays joined, its values stay in place, and deletion detection leaves it alone, so nothing is disconnected or deprovisioned. The one exception is an object an earlier import already marked obsolete (because it had left scope, say): the next synchronisation still disconnects it, as it would have anyway, and the error says so.
- The refusal repeats on every import until you either undo the cause or carry the change out deliberately.

A Connector whose External IDs are only unique within an Object Type (the SQL, File and SCIM 2.0 Client Connectors) cannot tell a changed object from a different object that shares the value, so there an object under a new Object Type is imported as a new object. Their Object Types do not overlap the way directory classes do, so the causes below do not arise for them in ordinary running.

### What changes an object's Object Type

- **Selecting an overlapping Object Type**<br /> An entry carrying two selected classes imports as the more specific one, so selecting `computer` while `user` is selected moves every computer account, which also carries `user`, to `computer`. The [schema preview](#previewing-a-schema-change) warns about this.
- **Deselecting an overlapping Object Type**<br /> The objects fall back to the other selected class: deselecting `computer` while `user` stays selected brings every computer back as a `user`, rather than taking them out of management. The preview warns about this too. To stop managing objects, scope them out by [partition or container](partitions-and-containers.md) instead.
- **Refreshing the schema**<br /> When a class changes between structural and auxiliary, or what it inherits from changes, the Object Type its entries resolve to can change. The [refresh preview](#refreshing-the-schema) lists these classification changes.
- **Editing an entry in the directory**<br /> Changing an entry's structural class where the directory allows it, or adding or removing an auxiliary class that is itself a selected Object Type.

Deleting an object and creating it again is not a type change. The new object has a new External ID, and JIM treats it as a leaver and a joiner.

### Carrying out an intended change

1. Build and enable the new Object Type's Synchronisation Rules first, with Object Matching Rules that join the new objects to the same Metaverse Objects.
2. Protect the Metaverse Objects while the old objects are retired: set a [deletion grace period](metaverse.md#deletion-behaviour) on their Metaverse Object Type, and [deletion limits](run-profiles.md#full-import) on the Full Import Run Profile.
3. Run a Full Import Run Profile with [Allow Object Type Changes](run-profiles.md#allow-object-type-changes-full-import) enabled, by hand. It imports each object as a new Connected System Object of its new type and marks the old one obsolete. Deletion limits hold back genuinely missing objects, not these retirements, which the change itself asks for. Where an old object cannot be retired (it is in a partition this Run Profile does not import, or its Object Type is deselected with an enabled Synchronisation Rule still bound to it), JIM does not create its replacement either, and reports the object as an Object Type Changed error saying why.
4. [Preview the Full Synchronisation](connected-systems.md#previewing-a-full-synchronisation), then run a Full Synchronisation, not a Delta Synchronisation. The old objects are disconnected under their type's Deletion Rules, and the new ones join or project through the new type's Synchronisation Rules.
5. Check the Activities: each changed object should have an obsoleted old object and a new one joined to the same Metaverse Object, and there should be no new Metaverse Objects you did not expect.

Keep Allow Object Type Changes off on the Run Profiles your Schedules run, so a change nobody planned is always refused and reported rather than carried out.

### Undoing an unintended change

Undo its cause: put the Object Type selection back, or revert the directory edit. Nothing needs repairing, because the refused objects were never changed; the next import matches them under their original Object Type again and the errors stop.

A schema refresh is undone differently, because JIM resolves entries from the classification it has stored, not from the directory's live schema. Before applying, **Cancel** on the refresh preview keeps the current classification. After applying, either change the directory's schema back and refresh again, change which of the overlapping Object Types are selected so the entries resolve as before, or carry the change out as described above.

Once a Full Import that allows Object Type changes has run, the old objects are obsolete, and JIM does not restore an obsolete object. Going back is then a second Object Type change: run the Full Synchronisation first so the change completes, then make the reverse change the same way.

## Refreshing the schema

The Schema tab's **Refresh Schema** button retrieves the latest object types and attributes from the Connected System. The first retrieval records what it finds; a refresh with a schema already in place shows you a **preview** of what changed before anything is applied, so a source system that has drifted never rewrites JIM's configuration behind your back.

The preview reports, per object type:

| Change | What applying it does |
|---|---|
| Object types or attributes **added** | Recorded and available for selection. Additions cannot affect anything that already works. |
| Object types or attributes **renamed** (the Connected System now reports a name differing only in case, such as `Mail` for `mail`) | The same Object Type or attribute, because [configuration names ignore case](../concepts/case-sensitivity.md#configuration-names-ignore-case). It keeps every Synchronisation Rule mapping and Object Matching Rule that uses it, and takes the new name. Listed with the safe changes. |
| Object types or attributes **no longer reported** | **Retained** in JIM; nothing is deleted by a refresh. Their values stop refreshing from that point, and any Synchronisation Rule or Attribute Flow reading them works from stale data, so the preview flags them for your attention. |
| Object Type **classification changed** (class kind, or which selected Object Type a class inherits from) | The new classification is recorded, which can change the Object Type that existing entries import as. Each object that would change is refused at the next import and kept as it is until you decide; see [When an object changes Object Type](#when-an-object-changes-object-type). A refresh with only these changes offers **Apply Schema Changes**, and its Activity completes with a warning listing them. A classification difference that cannot change resolution (an unselected Object Type, inheritance naming no other selected Object Type, or a class kind that neither becomes nor stops being auxiliary) is listed as **Object Type Classification Recorded** among the safe changes instead, without a warning; apply it so the [schema preview](#previewing-a-schema-change) can warn about overlapping Object Types later. |
| Attribute **definitions changed** (data type or plurality) | The new definition is recorded. A mapping validated against the old definition may no longer behave as intended, so these are flagged too. A data type you [overrode yourself](connected-system-attributes.md#overriding-an-inferred-type) is never overwritten, and never appears here. A Boolean the Connected System declares multi-valued is recorded single-valued, and appears here as a plurality change the first time JIM records it so. |

Where the diff carries destructive changes, the review also names the configuration that depends on them: Synchronisation Rules bound to a removed object type, Attribute Flow mappings reading a removed or redefined attribute (including attributes consumed inside an expression), Object Matching Rules that match on a removed attribute, and [Scoping Criteria](scoping-criteria.md) or Expressions that read a removed attribute, each deep-linking to its page so you can inspect a dependent without losing the review.

A destructive diff pauses on a three-way decision (an additions-only diff keeps the plain **Apply Schema Changes** action, since additions cannot affect anything):

- **Cancel** applies nothing, with the honest warning: cancelling does not preserve the status quo. The next Full Import finds no objects of a removed object type and obsoletes them regardless, and the source now sends redefined attributes in their new form. Cancelling an additions-only diff needs no confirmation, because the next refresh finds them again.
- **Apply &amp; Disable Dependents** records the refresh and then disables everything it invalidated: each named Synchronisation Rule and [mapping](attribute-mappings.md#disabling-a-single-mapping) is disabled with the refresh recorded as the reason, under child Activities of the refresh, so nothing runs against entries the source no longer reports while you rework the configuration. No objects or values are touched, and re-enabling is a manual choice per rule or mapping. Object Matching Rules have no disabled state, so the review lists any needing your attention separately. Where a disabled or removed mapping was the last contributor of a Metaverse attribute that a [derived Attribute Flow](derived-attributes.md) reads, the REST API and PowerShell name those derived flows too (`dependentDerivedFlows`); they keep running, and their Missing Input Behaviour decides what they contribute.
- **Apply &amp; Remove** is the full commitment, for a genuinely decommissioned object type or attribute. Its plan dialog leads with the consequence, because this deletes configuration and identity data: the named Synchronisation Rules and mappings are **deleted** (under child Activities of the refresh), and the data goes with them through a background worker task you can follow from the Operations tab. Connected System Objects of a removed object type are marked Obsolete and deprovision through the standard pipeline on the next synchronisation run, with disconnection, attribute recall, grace periods and [Metaverse Deletion Rules](metaverse.md#deletion-behaviour) all applying as configured; stored values of a removed attribute are deleted. The dialog counts both before you confirm, per object type and per attribute, and the task's Activity records every object it obsoleted. **Apply &amp; Remove** is unavailable while a Scoping Criterion compares a removed attribute, or a Scoping Criterion, Attribute Flow or Object Matching Rule Expression reads one, on a Synchronisation Rule that survives the refresh: deleting the attribute would leave the criterion comparing nothing, which takes objects out of scope and deprovisions them, and JIM does not delete those for you. The review names each one; change them first, or use **Apply &amp; Disable Dependents**, which keeps the attributes. The REST API and PowerShell refuse it the same way, before anything is applied.

Watch the preview's **discovery warnings** before applying: a Connected System identity without permission to read the full schema produces a partial read, which can make object types or attributes appear removed when they are not. Other warnings appear here too, about what the account can do beyond reading the schema; on Active Directory, for example, that the account is not allowed to list the domain's Deleted Objects container, or that JIM could not confirm it can, in which case a Delta Import would refuse to run or would complete with a warning (see the [LDAP Connector](../connectors/jim-ldap-connector-permissions.md)). Re-running the preview after granting the access confirms the fix.

The same flow is available to automation: `Import-JIMConnectedSystemSchema -Preview` in [PowerShell](../powershell/connected-system-schema.md#import-jimconnectedsystemschema) returns the preview result (its `HasRemovalsOrDefinitionChanges` property flags the changes that matter, its `Dependents` property names the invalidated configuration and its `RemovalImpact` property counts what a removal would take), and committing with `-DisableDependents` or `-RemoveDependents` takes the matching posture. The REST API offers `POST connected-systems/{id}/import-schema/preview` beside the committing `import-schema` endpoint, whose body accepts `disableDependents` or `removeDependents`; see the [REST API reference](../../api/reference/).

### Moving an attribute to its new type

When a refresh changes an attribute's data type, each Connected System Object keeps the values it holds under the old type until a Full Import reads the attribute again. Until then, a mapping that reads the attribute would flow those old values as the wrong type, so disable those mappings before anything synchronises.

1. Apply the refresh with **Apply &amp; Disable Dependents**. Each mapping that reads the attribute, directly or in an Expression, is disabled.
2. Run a Full Import. It reads the attribute on every object and replaces each old-type value with the value it reads as the new type. A Delta Import replaces values only on the objects it returns.
3. Point each disabled mapping at a Metaverse Attribute of the new type, or rework its Expression, and re-enable it.

**Check it worked:** open a Connected System Object that holds the attribute; its value shows the new type, and the object's change history lists the old value as removed.
