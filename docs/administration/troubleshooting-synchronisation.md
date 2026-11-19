---
title: Troubleshooting Synchronisation
description: What each synchronisation error and Activity message means, how to fix and check it, and what JIM keeps safe in the meantime.
type: troubleshooting
audience: operator
validated: v0.19.0
---

# Troubleshooting Synchronisation

Every run of a Run Profile is an Activity. When a run goes wrong, open its Activity from **Activity** in the portal, or with `Get-JIMActivity`. Its status says how the run ended:

- **Complete with warning** means some objects hit a problem JIM recognises, such as data that matches two people, or that the run stopped short of something on purpose.
- **Complete with error** means at least one object hit an error JIM did not anticipate.
- **Failed with error** means the run itself stopped, or every object in it failed.

```jim-diagram
archetype: decision
title: Where to look, by how the run ended
desc: "Open the run's Activity and read its status. Complete with warning means some objects hit a problem JIM recognises, and the entries below that protect your data come first; each object's execution item names its error. Complete with error means at least one object hit an error JIM did not anticipate, again named on its execution item. Failed with error means the run itself stopped, or every object failed: if the message says the Worker restarted or ran short of memory, see Troubleshooting Worker Restarts and Memory; otherwise read the Activity's message, such as a Connected System JIM could not reach."
start: {label: The run's Activity}
questions:
- {id: status, label: 'How did it end?', sub: the Activity's status}
- {id: worker, label: 'Worker restart or memory?', sub: in the Activity's message}
outcomes:
- {id: warning, label: A problem JIM recognises, sub: 'see each execution item''s error'}
- {id: error, label: An unanticipated error, sub: 'at least one object; see its item'}
- {id: workerpage, label: Troubleshooting Worker Restarts and Memory}
- {id: message, label: 'Stopped, or every object failed', sub: "read the Activity's message"}
answers:
- {from: status, to: warning, label: Complete with warning}
- {from: status, to: error, label: Complete with error}
- {from: status, to: worker, label: Failed with error}
- {from: worker, to: workerpage, label: 'yes'}
- {from: worker, to: message, label: 'no'}
```

The Activity's [execution items](../configuration/activity-execution-items.md) (`Get-JIMActivityExecutionItem`) name each object that hit a problem, with its error. The entries below are titled by that error, or by the Activity's message; those that protect your data come first.

For a run that ended because the Worker restarted or ran short of memory, see [Troubleshooting Worker Restarts and Memory](troubleshooting-worker.md); for an export the Connected System refused or never confirmed, see [Troubleshooting Exports](troubleshooting-exports.md).

## :material-database-alert-outline: **Failed with error**: `Run Profile '...' cannot run: an import of Connected System '...' stopped part-way through saving what it read`

An import of the same Connected System that completes without being able to repair it says `An earlier import stopped part-way through saving what it read` as a warning instead.

**What it means.** An import of the Connected System began saving what it read and did not finish: it failed, was cancelled, or the Worker stopped or restarted part-way. A Full Import saves as it reads, in units of up to 2,000 objects, so an object it saved can refer to one it had not saved yet. A synchronisation would take each such reference as removed, take it off the Metaverse Object and export the removal, so JIM refuses Full and Delta Synchronisation of that Connected System until the import is whole again. The Connected System's page shows **An import stopped part-way**, and `Get-JIMConnectedSystem` returns when the stopped import began saving as `importIncompleteSince`. Nothing is lost while it waits: what the import saved stays saved, the Metaverse and other Connected Systems are untouched, and exports still send what earlier synchronisations decided. A Schedule records the refused step as failed and follows its failure rules. Sync Previews and Full Synchronisation previews of the Connected System still run, for diagnosis, and open with the same warning: they read the half-saved Connector Space, so a reference they show being removed may only be one the import had not yet saved.

**How to fix.** Find out from the import's Activity why it stopped, and fix that ([Troubleshooting Worker Restarts and Memory](troubleshooting-worker.md) covers a Worker that stopped). Then run a Full Import of the Connected System to completion, reading all selected partitions, and run the synchronisation again. It passes quickly over what the stopped import saved, recording nothing twice. A Delta Import that reads only what changed, or a Run Profile that targets one partition, cannot tell that what the stopped import left is whole, so it leaves the refusal in place; a Delta Import whose Connector reads every object counts as a Full Import. Detected deletions refused by the [Run Profile's limits](../configuration/run-profiles.md#full-import) do not stop a Full Import repairing it. There is no way to lift the refusal by hand; clearing the Connector Space lifts it too, along with every Connected System Object.

**Check.** `Get-JIMConnectedSystem -Id <id>` returns an empty `importIncompleteSince`, the notice has gone from the Connected System's page, and the synchronisation's Activity ends **Complete**.

## :material-delete-alert-outline: `Deletion detection found ... no longer in the Connected System ... none were marked as deleted`

A Delta Import's message begins `The Connected System reported ... as deleted` instead, and every Delta Import after it reports the same deletions until you act.

**What it means.** A Full Import, or a Delta Import that read everything, found more objects missing from the Connected System, or a Delta Import was told of more deletions, than the Run Profile's deletion detection limit allows, so JIM marked none deleted and the run ended **Complete with warning**. The usual causes are a change of scope or filter, a connection that returned part of the data, or a file replaced by a smaller one; occasionally, a genuine mass deletion. Nothing has been deleted: the Connected System Objects and their Metaverse Objects are unchanged.

**How to fix.** Find out why the objects are missing: check the Connected System's partitions and containers, the Connector's filters, and the source itself. If the absence is genuine, let one run through as [Letting a legitimate mass change through](../configuration/run-profiles.md#letting-a-legitimate-mass-change-through) describes, then run the import again.

**Check.** The next import ends without the warning, and its Activity counts the deletions it detected.

## :material-delete-clock-outline: `Max deletes is ..., but ... deletes were pending, so none were attempted`

The same message names creates or updates when those are what the limit holds back.

**What it means.** More changes of one kind were waiting to be exported than the Export Run Profile's limit allows, so JIM attempted none, and the run ended **Complete with warning**. Each still waits as a Pending Export, and nothing was written to the Connected System.

**How to fix.** Before letting them through, look at what staged them: the Connected System's Pending Exports list shows each, with its changes. If they are what you intended, run an Export Run Profile with no limit by hand, as [Letting a legitimate mass change through](../configuration/run-profiles.md#letting-a-legitimate-mass-change-through) describes. If they are not, correct whatever staged them (usually a Synchronisation Rule's scoping or an Attribute Flow), and synchronise again.

**Check.** The export run ends without the warning, and the Connected System's Pending Exports list shows none still **Pending**.

## :material-vector-link: **Ambiguous Match**: `This object matches ... Metaverse Objects on ...`

**What it means.** An Object Matching Rule found more than one Metaverse Object for one Connected System Object, so JIM neither joined nor projected it. The message names the attribute and the values, and the execution item lists the Metaverse Objects that matched. The object stays as it was; nothing is joined to the wrong person. The same error, with the message `This object's import Synchronisation Rules match ... different Metaverse Objects: ...`, means two import Synchronisation Rules for the object's type each found one Metaverse Object, but not the same one; the message names each rule and what it found.

**How to fix.** Make the matched values identify one person: correct the duplicate data in the source or in the Metaverse, or change the Object Matching Rule to match on something unique, as [Matching outcomes](../configuration/object-matching-rules.md#matching-outcomes) describes. Where two rules disagree, change one rule's Object Matching Rules, or its scope, so that every rule that covers the object finds the same person.

**Check.** After a corrected source, import and synchronise as usual; after a changed Object Matching Rule, the next synchronisation processes every object of its Object Type. The object's execution item records a join or a projection.

## :material-vector-link: **Ambiguous Provisioning Match**: `This Metaverse Object matches ... Connected System Objects in '...' on ...`

**What it means.** Before provisioning, an export rule's Object Matching Rule found more than one existing Connected System Object in the target for one Metaverse Object, so JIM neither joined it to one nor provisioned another. The message names the Connected System, the rule and the values, and the execution item lists the accounts that matched. Nothing is staged for that export rule; the Metaverse Object's other export rules carry on.

**How to fix.** Make the matched values identify one account: remove or correct the duplicate account in the target, correct the identity's data, or change the Object Matching Rule to match on something unique, as [Matching outcomes](../configuration/object-matching-rules.md#matching-outcomes) describes.

**Check.** After a corrected target, import it and synchronise as usual; after a changed Object Matching Rule, run a Full Synchronisation. The Metaverse Object's execution item records the join, or the provisioning, for that export rule.

## :material-link-variant-off: **Could Not Join Due To Existing Join**: `Cannot join this Connected System Object to Metaverse Object (...) - it already has a connector from this Connected System.`

**What it means.** The Metaverse Object this object matched is already joined to another object from the same Connected System, and a Metaverse Object has at most one Connected System Object per Connected System. Two records in the source almost always describe the same person. Neither join is changed.

**How to fix.** Find the two records (the message names the Metaverse Object, whose page lists what is joined to it) and remove or correct the duplicate in the source. If they are genuinely two people, the Object Matching Rule is matching on something they share: change it to something unique.

**Check.** After the next import and synchronisation, the object's execution item records a join or a projection.

## :material-content-copy: **Duplicate Object**: `Duplicate external ID '...' found in the same import batch.`

**What it means.** The Connected System returned two or more objects with the same External ID, so JIM imported none of them: it cannot tell which is the real one.

**How to fix.** Make the External ID unique in the source, or choose a unique attribute as the External ID on the Connected System's schema. The variant `found across import pages` means a Full Import was given the same object on two pages of results; check the directory's paging. A Delta Import never reports it: it reads changes in order, so a later page's copy of an object (a row deleted and added back, say) replaces the earlier one. An object repeated within one page is still rejected as a duplicate.

**Check.** The next import brings the object in, with no Duplicate Object execution items.

## :material-link-variant-remove: **Reference Unresolved**: `Couldn't resolve a reference on ... to a Connected System Object`

**What it means.** An attribute refers to another object (a manager, a group member) that JIM does not hold for this Connected System, usually because it sits outside the containers JIM manages. The rest of the object is imported.

**How to fix.** [Finding out why a reference is unresolved](../configuration/unresolved-references.md#finding-out-why-a-reference-is-unresolved) explains how to see where each one points and what would resolve it.

**Check.** The Connected System's count of unresolved references falls after the next import.

## :material-swap-horizontal: **Synchronisation Failed**: `Synchronisation Rule '...' targets Connected System Object Type '...' in '...', but this Metaverse Object is already represented there by a '...' object.`

**What it means.** Two export Synchronisation Rules for the same Connected System, each for a different Object Type, both have this Metaverse Object in scope. A Metaverse Object has at most one Connected System Object per Connected System, and it already has one of the other Object Type there, so JIM staged nothing for the rule the message names. The existing object is left alone.

**How to fix.** Narrow the Scoping Criteria of one rule, so that no Metaverse Object is in scope of both. If the two Object Types genuinely belong to the same identities, give each its own Connected System over the same target. The object's **Connections** tab names the conflicting rule too (see [Metaverse Objects](../configuration/metaverse-objects.md#why-it-is-connected-and-why-it-is-not)). Saving either rule while both are enabled warns you about the other.

**Check.** After the next synchronisation, the object's execution item stages the export it expects, or records nothing for the rule it no longer matches.

## :material-call-split: **Export Rules Overlap**: `This Metaverse Object is in scope of ... export Synchronisation Rules that target '...' in '...': ...`

**What it means.** Two or more enabled export Synchronisation Rules for the same Connected System and Object Type both have this Metaverse Object in scope. Only one export rule may manage an object, so JIM exported nothing to that Connected System for it and corrected no drift there; the object's other Connected Systems, and every other object, carried on. The message names the rules.

**How to fix.** Give the rules Scoping Criteria that never overlap, or combine them into one rule; see [Several rules for the same objects](../configuration/scoping-criteria.md#several-rules-for-the-same-objects). Saving either rule while both are enabled warns you about the other.

**Check.** After the next synchronisation, the object's execution item stages the export it expects and records no Export Rules Overlap item.

## :material-shape-plus-outline: **Projection Type Conflict**: `More than one import Synchronisation Rule would project this object, into different Metaverse Object Types: ...`

**What it means.** Two or more enabled import Synchronisation Rules have this Connected System Object in scope, nothing matched it, and the rules would project it into different Metaverse Object Types. JIM will not choose between them, so it projected nothing and the object stays unjoined. The message names each rule and the type it projects into.

**How to fix.** Give the rules Scoping Criteria that never cover the same object, or have them project into one Metaverse Object Type; see [Several rules for the same objects](../configuration/scoping-criteria.md#several-rules-for-the-same-objects). Saving either rule while both are enabled warns you about the other.

**Check.** After the next synchronisation, the object's execution item records a projection.

## :material-filter-remove-outline: **Synchronisation Failed**: `The scoping criterion on '...' in the Synchronisation Rule '...' could not be evaluated, so this object's scope was left as it was.`

**What it means.** JIM could not tell whether the object is in the rule's scope, so it took no action for it and kept any join; every other object carried on. The message gives the cause:

- A *matches pattern* or *does not match pattern* criterion ran past its time limit on one of this object's values.
- The object holds an attribute a criterion reads (directly or as an expression's input) only under its old data type: a schema refresh changed the type and no import has read it since.
- An expression criterion reads an attribute its Object Type no longer has, fails to evaluate, or computes a value that cannot be compared as the criterion's data type.

When an object stops contributing to a Metaverse Object and a surviving contributor's scope cannot be decided, the leaving object gets this error and the Metaverse Object is left unchanged. Deleting a Connected System or Synchronisation Rule then stops, keeping it; remove the cause and delete it again.

**How to fix.** For a pattern, simplify it in the rule's [scoping criteria](../configuration/scoping-criteria.md#operators). Nested repetition such as `(a+)+` is the usual cause; anchoring the pattern and matching the exact shape you expect (`^[a-z.]+@example\.org$` rather than `.*@.*example.*`) keeps each match fast.

For an expression, correct it, or the attribute it reads, in the rule's scoping criteria; see [Comparing a computed value](../configuration/scoping-criteria.md#comparing-a-computed-value).

For a value held under its old data type, run a Full Import of the Connected System the rule imports from; see [Moving an attribute to its new type](../configuration/connected-system-schema.md#moving-an-attribute-to-its-new-type).

**Check.** The next synchronisation, Delta included, retries the object, and after a changed pattern processes every object of the rule's type. Its execution item no longer records Synchronisation Failed.

## :material-calendar-alert: **Dates Merged**: `'...' holds ... dates, but target attribute '...' on '...' holds dates to the second, where some of them become the same date, so it is sent ... distinct values`

**What it means.** Some of the dates a multi-valued attribute exports are the same date at the step of time the Connected System keeps, its [Date resolution](../configuration/connected-system-attributes.md#date-resolution): two times within one second, for a directory that keeps whole seconds. The Connected System can hold them only as one value, so JIM sent them as one. Nothing failed: the attribute and the object's other attributes exported, and the next import confirms what was sent.

**How to fix.** Nothing, if one value per step is what the Connected System should hold. If the dates must stay apart there, keep them further apart than its resolution at the source; if the Connected System keeps them more finely than JIM assumes, change the attribute's resolution on the Schema tab.

**Check.** After the next synchronisation that changes the attribute, the object's execution item records the export with no Dates Merged item, or one naming the count you expect.

## :material-timer-sand: **Failed with error**: `The change was saved, but JIM could not record that the next synchronisation of ... must process every ... object`

**What it means.** An import Synchronisation Rule or Object Matching Rule change was saved, but JIM could not record that the next synchronisation must process its whole Object Type, so existing objects may miss it. The save's Activity is marked failed to warn you.

**How to fix.** Run a Full Synchronisation of the Connected System named: it processes every object.

**Check.** The Full Synchronisation's Activity ends **Complete**.

## :material-pause-circle-outline: **Failed with error**: `Run Profile '...' is disabled, so it cannot run.`

**What it means.** The Run Profile is [disabled](../configuration/run-profiles.md#disable-a-run-profile), so JIM refused the run and changed nothing. The message says why: an administrator disabled it, or [a hierarchy refresh removed its partition](../configuration/partitions-and-containers.md#when-a-refresh-removes-a-partition-a-run-profile-targets).

**How to fix.** Turn **Enabled** on in its edit dialog, or `Set-JIMRunProfile -Enabled $true`; if its partition was removed, choose another (`-PartitionId` or `-AllSelectedPartitions`). If it should stay disabled, take it out of the Schedule.

**Check.** `Get-JIMRunProfile` returns it with `Enabled` true, and its next run's Activity ends **Complete**.

## :material-play-circle-outline: A run ends **Failed with error** before processing anything

**What it means.** JIM could not reach the Connected System, or the Connector could not start: the message gives the reason, such as a refused connection, a name that does not resolve, or a certificate JIM does not trust. Nothing was imported or exported.

**How to fix.** Check the Connected System's connection settings and that its server is reachable from JIM; for an untrusted certificate, see [Certificates](../configuration/certificates.md). Then run the Run Profile again.

**Check.** The run's Activity ends **Complete**.

## :material-lifebuoy: Still stuck?

Collect these before asking for help:

- **The Activity's ID** and its status and message, from its page or `Get-JIMActivity -Id <id>`.
- **One failing execution item's error type and message,** with its stack trace if it has one: `Get-JIMActivityExecutionItem -Id <id>`.
- **The JIM version,** shown at the foot of every portal page and by `Connect-JIM`.
- **The services' logs from the time of the run:** **Logs** in the portal, or `docker logs jim.worker` and `docker logs jim.web` (`sudo podman logs jim-worker` on Podman).
