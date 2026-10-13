---
title: Troubleshooting Synchronisation
description: What each synchronisation error and Activity message means, how to fix and check it, and what JIM keeps safe in the meantime.
type: troubleshooting
audience: operator
validated: v0.16.0
---

# Troubleshooting Synchronisation

Every run of a Run Profile is an Activity. When a run goes wrong, open its Activity from **Activity** in the portal, or with `Get-JIMActivity`. Its status says how the run ended:

- **Complete with warning** means some objects hit a problem JIM recognises, such as data that matches two people, or that the run stopped short of something on purpose.
- **Complete with error** means at least one object hit an error JIM did not anticipate.
- **Failed with error** means the run itself stopped, or every object in it failed.

The Activity's [execution items](../configuration/activity-execution-items.md) name each object that hit a problem, with the error recorded against it (`Get-JIMActivityExecutionItem` returns the same). The entries below are titled by that error, or by the message on the Activity. The ones that protect your data come first.

## `Deletion detection found ... no longer in the Connected System ... none were marked as deleted`

**What it means.** A Full Import found more objects missing from the Connected System than the Run Profile's deletion detection limit allows, so JIM marked none of them as deleted and the run ended **Complete with warning**. The usual causes are a change of scope or filter, a connection that returned part of the data, or a file replaced by a smaller one; occasionally it is a genuine mass deletion. Nothing has been deleted: the Connected System Objects, and the Metaverse Objects they contribute to, are as they were before the run.

**How to fix.** Find out why the objects are missing: check the Connected System's partitions and containers, the Connector's filters, and the source itself. If the absence is genuine, let one run through as [Letting a legitimate mass change through](../configuration/run-profiles.md#letting-a-legitimate-mass-change-through) describes, then run the Full Import again.

**Check.** The next Full Import ends without the warning, and its Activity counts the deletions it detected.

## `Max deletes is ..., but ... deletes were pending, so none were attempted`

The same message names creates or updates when those are what the limit holds back.

**What it means.** More changes of one kind were waiting to be exported than the Export Run Profile's limit allows, so JIM attempted none of them, and the run ended **Complete with warning**. Every one of them is still waiting, as a Pending Export, and nothing has been written to the Connected System.

**How to fix.** Look at what staged them before letting them through: the Connected System's Pending Exports list shows each one and the changes it carries. If they are what you intended, run an Export Run Profile with no limit by hand, as [Letting a legitimate mass change through](../configuration/run-profiles.md#letting-a-legitimate-mass-change-through) describes. If they are not, correct whatever staged them (usually a Synchronisation Rule's scoping or an Attribute Flow), and synchronise again.

**Check.** The export run ends without the warning, and the Connected System's Pending Exports list shows none still **Pending**.

## `Task was abandoned due to worker crash or restart.`

**What it means.** The worker stopped while it was running this Activity: it restarted, crashed, or its server went down. JIM marks the Activity **Failed with error** when the worker starts again, or when the worker has not reported progress for the [Stale task timeout](configuration.md#service-settings) Service Setting (five minutes by default). Whatever the run had already saved stays saved, and nothing is left half-written: the next run carries on from the state JIM's data is in.

**How to fix.** Find out why the worker stopped, from its log (`docker logs jim.worker`, or **Logs** in the portal), then start the Run Profile again.

**Check.** The new run's Activity ends **Complete**.

## **Ambiguous Match**: `This object matches ... Metaverse Objects on ...`

**What it means.** An Object Matching Rule found more than one Metaverse Object for one Connected System Object, so JIM neither joined nor projected it. The message names the attribute and the values, and the execution item lists the Metaverse Objects that matched. The object stays as it was; nothing is joined to the wrong person.

**How to fix.** Make the matched values identify one person: correct the duplicate data in the source or in the Metaverse, or change the Object Matching Rule to match on something unique, as [Matching outcomes](../configuration/object-matching-rules.md#matching-outcomes) describes.

**Check.** After a corrected source, import and synchronise as usual; after a changed Object Matching Rule, run a Full Synchronisation, since a Delta Synchronisation processes only the objects that changed. The object's execution item records a join or a projection.

## **Ambiguous Provisioning Match**: `This Metaverse Object matches ... Connected System Objects in '...' on ...`

**What it means.** Before provisioning, an export rule's Object Matching Rule found more than one existing Connected System Object in the target for one Metaverse Object, so JIM neither joined it to one nor provisioned another. The message names the Connected System, the rule and the values, and the execution item lists the accounts that matched. Nothing is staged for that export rule; the Metaverse Object's other export rules carry on.

**How to fix.** Make the matched values identify one account: remove or correct the duplicate account in the target, correct the identity's data, or change the Object Matching Rule to match on something unique, as [Matching outcomes](../configuration/object-matching-rules.md#matching-outcomes) describes.

**Check.** After a corrected target, import it and synchronise as usual; after a changed Object Matching Rule, run a Full Synchronisation. The Metaverse Object's execution item records the join, or the provisioning, for that export rule.

## **Could Not Join Due To Existing Join**: `Cannot join this Connected System Object to Metaverse Object (...) - it already has a connector from this Connected System.`

**What it means.** The Metaverse Object this object matched is already joined to another object from the same Connected System, and a Metaverse Object has at most one Connected System Object per Connected System. Two records in the source almost always describe the same person. Neither join is changed.

**How to fix.** Find the two records (the message names the Metaverse Object, whose page lists what is joined to it) and remove or correct the duplicate in the source. If they are genuinely two people, the Object Matching Rule is matching on something they share: change it to something unique.

**Check.** After the next import and synchronisation, the object's execution item records a join or a projection.

## **Duplicate Object**: `Duplicate external ID '...' found in the same import batch.`

**What it means.** The Connected System returned two or more objects with the same External ID, so JIM imported none of them: it cannot tell which is the real one.

**How to fix.** Make the External ID unique in the source, or choose an attribute that is unique as the External ID on the Connected System's schema. A variant of the message, `found across import pages`, means a directory returned the same object on two pages of results; check the directory's paging.

**Check.** The next import brings the object in, with no Duplicate Object execution items.

## **Reference Unresolved**: `Couldn't resolve a reference on ... to a Connected System Object`

**What it means.** An attribute refers to another object (a manager, a group member) that JIM does not hold for this Connected System, usually because it sits outside the containers JIM manages. The rest of the object is imported.

**How to fix.** [Finding out why a reference is unresolved](../configuration/unresolved-references.md#finding-out-why-a-reference-is-unresolved) explains how to see where each one points and what would resolve it.

**Check.** The Connected System's count of unresolved references falls after the next import.

## **Operation Failed**: `Export failed: ...`

**What it means.** The Connected System refused an export, and the rest of the message is its own reason: the account JIM connects as lacks the rights, the change breaks the system's own rules, or a required value is missing. JIM records any refusal it cannot classify as an error it did not anticipate, so the run ends **Complete with error** even though the cause is usually the Connected System's configuration. The change is still waiting, as a Pending Export: JIM tries it again at each Export run after waiting 2, 4, 8 and then 16 minutes, and marks it Failed after the fifth refusal.

**How to fix.** Act on the Connected System's reason: grant the rights, supply the value, or change the Attribute Flow. A **Value Already in Use** error instead means the value is held by another object; [When a target rejects a value](../configuration/generated-values-in-target-systems.md#when-a-target-rejects-a-value) covers that one. A **Password Required** error from Active Directory is explained in [Active Directory will not enable an account that has no password](../connectors/jim-ldap-connector-troubleshooting.md#active-directory-will-not-enable-an-account-that-has-no-password).

**Check.** The next Export run writes the change: its Pending Export shows **Exported**, and clears once an import confirms it (at once, for the File and SQL Connectors). A Pending Export already marked Failed is not attempted again; it clears when an import from the Connected System shows its values in place, for example once you make the change there yourself.

## **Export Pending**: `We exported a change, but did not get confirmation of it`

**What it means.** JIM exported a change, but the confirming import read back a different value, or none. The Connected System changed the value as it stored it (often its case), or the import does not return that attribute. JIM exports the change again at the next Export run, and after five unconfirmed attempts records **Export Failed**: `Export confirmation failed after maximum retries`.

**How to fix.** Compare what JIM exported with what the Connected System holds. Where the system changes the case, see [When a target changes the case of a value](../concepts/case-sensitivity.md#when-a-target-changes-the-case-of-a-value); where the import cannot see the attribute, select it on the Connected System's schema.

**Check.** The next confirming import records no Export Pending items, and the Pending Export clears.

## **Synchronisation Failed**: `Synchronisation Rule '...' targets Connected System Object Type '...' in '...', but this Metaverse Object is already represented there by a '...' object.`

**What it means.** Two export Synchronisation Rules for the same Connected System, each for a different Object Type, both have this Metaverse Object in scope. A Metaverse Object has at most one Connected System Object per Connected System, and it already has one of the other Object Type there, so JIM staged nothing for the rule the message names. The object it already has is left as it is.

**How to fix.** Narrow the Scoping Criteria of one rule, so that no Metaverse Object is in scope of both. If the two Object Types genuinely belong to the same identities, give each its own Connected System over the same target. The object's **Connections** tab names the conflicting rule too (see [Metaverse Objects](../configuration/metaverse-objects.md#why-it-is-connected-and-why-it-is-not)).

**Check.** After the next synchronisation, the object's execution item stages the export it expects, or records nothing for the rule it no longer matches.

## **Synchronisation Failed**: `The scoping criterion on '...' in the Synchronisation Rule '...' could not be evaluated, so this object's scope was left as it was.`

**What it means.** A *matches pattern* or *does not match pattern* criterion ran past its time limit on one of this object's values, so JIM could not tell whether the object is in the rule's scope. It took no action for the object: nothing was brought into scope or taken out, and any join it already had is kept. The message names the rule, the attribute and the pattern. Every other object in the run carried on.

**How to fix.** Simplify the pattern in the rule's [scoping criteria](../configuration/scoping-criteria.md#operators). Nested repetition such as `(a+)+` or `(.*)*` is the usual cause: on a long value that does not match, the expression tries an enormous number of ways before giving up. Anchoring the pattern and matching the exact shape you expect (`^[a-z.]+@example\.org$` rather than `.*@.*example.*`) keeps each match fast.

**Check.** Run a Full Synchronisation. The object's execution item records it entering or leaving scope, with no Synchronisation Failed error.

## A run ends **Failed with error** before processing anything

**What it means.** JIM could not reach the Connected System, or the Connector could not start: the message is the reason the network, the server or the Connector gave, such as a refused connection, a name that does not resolve, or a certificate JIM does not trust. Nothing was imported or exported.

**How to fix.** Check the Connected System's connection settings and that its server is reachable from JIM; for a certificate JIM does not trust, see [Certificates](../configuration/certificates.md). Then run the Run Profile again.

**Check.** The run's Activity ends **Complete**.

## Still stuck?

Collect these before asking for help:

- **The Activity's ID** and its status and message, from its page or `Get-JIMActivity -Id <id>`.
- **One failing execution item's error type and message,** with its stack trace if it has one: `Get-JIMActivityExecutionItem -Id <id>`.
- **The JIM version,** shown at the foot of every portal page and by `Connect-JIM`.
- **The services' logs from the time of the run:** **Logs** in the portal, or `docker logs jim.worker` and `docker logs jim.web` (`sudo podman logs jim-worker` on Podman).
