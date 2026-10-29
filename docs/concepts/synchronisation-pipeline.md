---
title: Synchronisation Pipeline
description: How JIM moves identity data through three phases, Import, Synchronisation and Export, and what each one does and protects.
type: concept
audience: implementer
validated: v0.17.0
---

# Synchronisation Pipeline

JIM processes identity data in a three-phase pipeline: **Import**, **Sync**, and **Export**. Each phase has a distinct responsibility, and data passes through them in order, so it is validated, transformed and reconciled at each stage before it reaches its destination.

--8<-- "assets/diagrams/sync-pipeline.svg"

<p class="jim-diagram-caption">Data is staged either side of the Metaverse, in the Connector Space belonging to each Connected System: imports land there as Connected System Objects, synchronisation reconciles them into Metaverse Objects, and outbound changes are staged again before export. The systems shown are illustrative.<span class="jimdg-caption-motion"> Moving dots trace data through the pipeline.</span></p>

## :material-import: Phase 1: Import

Import pulls data from a Connected System into its **Connector Space**. The result is a set of Connected System Objects that represent the current state of the external system.

### Full Import

A **Full Import** reads every object from the Connected System and compares it against the existing Connector Space. JIM determines which objects are new, which have changed, and which have been deleted (obsoleted).

Full Imports are used for:

- **Initial setup**<br /> Bringing in all data from a new Connected System for the first time.
- **Periodic reconciliation**<br /> Checking that the Connector Space still matches the source system.
- **Recovery**<br /> Reloading all data to restore consistency after errors or data issues.

A Full Import Run Profile can limit how many objects one run may newly mark as deleted, so a broken filter or base DN cannot turn a Full Import into a mass deprovisioning; [Run Profiles > Safeguards](../configuration/run-profiles.md#full-import) covers the limits and what a run that exceeds one does.

### Delta Import

A **Delta Import** reads only the objects that have changed since the last import. It is much faster than a Full Import for a large directory, and needs the Connected System to track its changes: the [LDAP Connector](../connectors/jim-ldap-connector.md) reads Active Directory's update sequence numbers, OpenLDAP's accesslog overlay, or a changelog such as 389 Directory Server's. A Delta Import Run Profile can take [deletion limits](../configuration/run-profiles.md#delta-import) too.

### What Happens During Import

1. JIM connects to the external system through its Connector.
2. Objects are read: all of them for a Full Import, the changed ones for a Delta Import.
3. For each object read, JIM either:
   - **Creates**<br /> A new Connected System Object, if the object is not already in the Connector Space.
   - **Updates**<br /> The existing Connected System Object, if at least one of its attribute values has changed, or if JIM provisioned the object and the import confirms it exists in the Connected System.
   - **Leaves it unchanged**<br /> The existing Connected System Object, if the object matches what JIM holds. JIM finds this from the object's [content hash](#how-full-import-detects-unchanged-objects), or by comparing it attribute by attribute.
   - **Obsoletes**<br /> The existing Connected System Object, if the Connector reports the object deleted. The Activity message counts an obsoleted object as **deleted**.
4. A Full Import also obsoletes each Connected System Object it no longer found in the source.
5. The run's Activity message counts what happened, for example:

    > Import complete: 1,000 objects (3 created, 12 updated, 983 unchanged, 2 errors) in 42 sec (avg 24 obj/s); 5 no longer found and marked as deleted

The figures in brackets describe the objects the run read, and count each one once:

| Figure | Counts an object read that |
|---|---|
| **created** | JIM did not hold, so it created a Connected System Object for it. An object that changed Object Type, where the Run Profile [allows it](../configuration/run-profiles.md#allow-object-type-changes-full-import), counts here once, and JIM retires the Connected System Object it replaces. |
| **updated** | JIM held, and at least one of its values changed. A provisioned object that its first import confirms exists counts here too, because confirming it changes its state. |
| **unchanged** | JIM held, and matches what JIM holds. An object whose export the import confirms counts here when the Connected System holds the exported values as JIM sent them, because JIM applied those values when the export succeeded; if the Connected System stored something different, the object counts as updated. A delete for an object JIM no longer holds, or already holds as obsolete, also counts here. |
| **deleted** | the Connector reported deleted, so JIM obsoleted it. |
| **deletions withheld** | the Connector reported deleted, but the Delta Import's deletion limits held back; JIM left it as it was. |

Unchanged, deleted, deletions withheld and errors appear only when there are any.

**Errors** counts the problems the run recorded, one per [execution item](../configuration/activity-execution-items.md). An object JIM could not import at all appears only among the errors. An object with a value JIM could not import is imported without that value, and JIM keeps any value it already held for the attribute, so the object counts in the brackets as well as among the errors.

Objects a Full Import no longer found were not read, so they follow the brackets as **no longer found and marked as deleted**, and appear only when there are any.

An obsoleted Connected System Object stays that way until a Synchronisation Run Profile on the same Connected System processes it, so every Full Import in between finds the object missing all over again. Only the first one reports a deletion; the runs that follow record nothing for it, because it was already obsolete when they started. An object that sits obsolete for a long time means the Connected System is being imported but not synchronised.

Import does **not** modify the Metaverse. The Connector Space acts as a staging area, isolating the Metaverse from any issues during import.

### How Full Import Detects Unchanged Objects

At scale, most objects in a Full Import have not changed since the previous run. Comparing every attribute of every object against the Connector Space is unnecessary work when the underlying source data is identical. JIM speeds this up with a stored **content hash**: after a Full Import updates or creates a Connected System Object, it stamps a compact hash of the imported content onto that object.

On the next Full Import, JIM computes the same hash for each incoming object *before* loading its existing attribute values. If the freshly computed hash matches the stored one (and the object's schema has not changed since it was last stamped), JIM skips loading and comparing that object's attributes entirely; only genuinely new or changed objects pay the full comparison cost. The hash is always computed from the incoming data, never from what is already stored, so any doubt falls back to the full comparison: a mismatch never causes a change to be missed, only an unnecessary (but harmless) re-comparison.

This optimisation needs no configuration. To validate it, or to investigate a suspected discrepancy, turn on [Verification Mode](../configuration/run-profiles.md#verification-mode-full-import) on a Full Import Run Profile, which compares every object and reports any disagreement with its stored hash.

## :material-sync: Phase 2: Sync (Synchronisation)

Sync is the core phase, where JIM reconciles each Connector Space with the Metaverse. It applies **Synchronisation Rules** to decide how Connected System Objects relate to Metaverse Objects, and how attributes flow between them.

### Inbound Sync (Source to Metaverse)

Inbound sync processes Connected System Objects from source systems and updates the Metaverse. For each one in scope:

1. **Scoping**<br /> JIM evaluates the Synchronisation Rule's scoping to decide whether the object should be processed. An object that falls out of scope is disconnected from its Metaverse Object, unless the rule's [Out-of-Scope Action](../configuration/scoping-criteria.md) keeps the join.

2. **Matching**<br /> JIM looks for the Metaverse Object the object belongs to, using the [Object Matching Rules](../configuration/object-matching-rules.md), which correlate it with an existing Metaverse Object on, say, an employee ID, an email address, or a combination of attributes.

3. **Projection**<br /> If nothing matches and the Synchronisation Rule projects, JIM creates a new Metaverse Object and joins the Connected System Object to it.

4. **Attribute Flow**<br /> Once joined or projected, JIM applies the rule's Attribute Flows, copying values from the Connected System Object to the Metaverse Object and optionally transforming them with [expressions](expressions.md). For text attributes, per-mapping [value processing](../configuration/attribute-mappings.md#value-processing-inbound) (trim, collapse, case normalisation, and the default treat-whitespace-as-no-value) runs at this step, normalising the value before priority resolves which rule's value wins. A multi-valued attribute is written as a set: every value is carried across, order never matters, and only the values gained and lost change (see [Multi-valued attributes](../configuration/attribute-mappings.md#multi-valued-attributes)).

5. **Attribute resolution**<br /> When more than one Connected System contributes the same Metaverse Attribute, the winning value is chosen deterministically by [Attribute Priority](attribute-priority.md), not by the order synchronisations happen to run in. This is also where a "Null is a value" contributor can assert a clear and where a departing source hands an attribute down to the next-priority contributor.

### Outbound Sync (Metaverse to Target)

Outbound sync evaluates Metaverse Objects against export Synchronisation Rules, and works out what needs exporting to each target system. For each Metaverse Object in scope:

1. **Scoping**<br /> JIM evaluates whether the Metaverse Object is in scope for the export rule.

2. **Provisioning**<br /> If it is in scope but has no Connected System Object in the target Connected System, and the rule provisions, JIM creates one.

3. **Attribute Flow**<br /> The rule's Attribute Flows copy values from the Metaverse Object to the Connected System Object, optionally transforming them with expressions.

4. **Pending Exports**<br /> Each change is recorded as a **Pending Export** rather than sent to the target system straight away, so administrators can review queued changes before they are applied.

### Writing Back to a Source System

An outbound Synchronisation Rule may target the same Connected System an inbound rule reads from. This is how attribute writeback works: HR supplies the identity, JIM derives a value (an email address, an account name), and the derived value is written back into HR. Rules targeting the system being synchronised are evaluated during that system's own synchronisation run, exactly like rules targeting any other system.

What prevents this from looping is value-level change detection, not a special case: before staging a Pending Export, JIM compares each flowed value against what the target Connected System Object already holds, and a value the target already has is not staged. Importing a value and flowing it straight back to the same attribute therefore produces no export at all, while a genuine writeback (a value the system does not yet hold) is staged normally. One configuration deserves care: mapping the same attribute in both directions with transformations that do not mirror each other (for example, importing a lower-cased copy while exporting an upper-cased one) can cause the two systems to re-assert their own form on every run; keep bidirectional mappings symmetric, or map the derived value to an attribute that does not flow back inbound.

### Full Sync vs Delta Sync

- **Full Synchronisation** processes every Connected System Object, changed or not. Use it for a new Connected System, after upgrading JIM, after a change only a Full Synchronisation applies, and to correct anything a Delta Synchronisation missed. It is slower, so schedule Delta Synchronisation for routine runs.
- **Delta Synchronisation** processes the Connected System Objects that have changed since they were last synchronised, and every object of an Object Type awaiting synchronisation. It is faster, and is the normal operational mode. A change an import commits while a synchronisation of the same Connected System is running, for example in the same Schedule parallel group, is never lost: if the running synchronisation does not process it, the next one does.

Most saved configuration changes do not wait for a Full Synchronisation: the next synchronisation of the Connected System, Delta included, applies an import rule change, and the next synchronisation of any Connected System applies an export scope change. [When a change takes effect](../configuration/configuration-changes.md#when-a-change-takes-effect) says which waits for what.

## :material-export: Phase 3: Export

Export sends Pending Exports from the Connector Space to the target Connected System. Each one is a create, update, or delete.

### What Happens During Export

1. JIM reads the Pending Exports for the Connected System.
2. For each one, the Connector sends the change to the external system.
3. A successful export is confirmed, usually by the next import, and its Pending Export is cleared.
4. A failed export stays a Pending Export, with its error recorded on the run's Activity, and is attempted again at every export run until the cause is fixed; [Troubleshooting Synchronisation](../administration/troubleshooting-synchronisation.md#operation-failed-export-failed) explains each error.
5. The run's Activity records how many changes were exported.

### Batching and Parallelism

Exports are sent in batches, several at once where the Connector supports it; [Export Performance](../configuration/export-performance.md) explains how many, and how to raise or limit it.

### References That Cannot Resolve Yet

A Reference attribute (a person's manager, a group's members) is written as the referenced object's own identifier in the target Connected System, and that identifier only exists once the referenced object has itself been exported. Exports therefore naturally run in passes: a reference whose target is not addressable yet is left behind and filled in automatically once it is.

An export does not wait whole for its references. Everything that can be written is written now; a new starter still gets their account on time even though their manager has not been provisioned yet, and the manager link follows when it can. The Pending Export remains, carrying only the references still owed, and its detail page explains each one: whether the target is awaiting its own export, or has no object in the target system at all (usually because it is out of scope for provisioning there). The latter cannot resolve as things stand, and is reported on the run according to the Connected System's [Unresolved Reference Handling](../configuration/unresolved-references.md), the same setting that governs the import side. The Pending Exports list shows how many references each export is still owed.

### Pre-Export Reconciliation

JIM reconciles Pending Exports before they are sent. If an object is created and then deleted before the export runs, for example, the redundant Pending Exports cancel each other out, and the target system sees neither.

### Safeguards

An Export Run Profile can limit how many creates, updates and deletes one run may attempt, so a broken import filter or a mistaken Synchronisation Rule change cannot turn one run into a mass write; [Run Profiles > Safeguards](../configuration/run-profiles.md#safeguards) covers the limits and what a run that exceeds one does.

### Applying Exported Changes Straight Away

When an export to a Connected System succeeds, JIM applies the exported attribute values to its own record of that Connected System Object immediately, rather than waiting for the next import to bring them back. The confirming import still runs as usual, comparing what the Connected System reports against what JIM expects, but for a successfully exported object it typically finds nothing left to do, which keeps it fast at large scale.

## :material-sitemap-outline: Putting It Together

A typical synchronisation cycle follows this pattern:

--8<-- "assets/diagrams/sync-cycle.svg"

<p class="jim-diagram-caption">One identity's journey from HR to Active Directory; the numbered steps match the list below.<span class="jimdg-caption-motion"> A moving dot traces the identity making the journey.</span></p>

1. **Import** from the HR system brings employee records into the HR system's Connector Space.
2. **Inbound sync** joins or projects those records to Metaverse Objects, and flows attributes inward.
3. **Outbound sync** evaluates the Metaverse Objects against the Active Directory Synchronisation Rules, and creates Pending Exports.
4. **Export** sends the Pending Exports to Active Directory, creating accounts and updating attributes.

[Schedules](../configuration/schedules.md) run the cycle automatically, by cron expression or interval.

Before running the real cycle for one object, [Sync Preview](../configuration/sync-preview.md) answers what steps 2 to 4 would do with it: whether it would join or project, what would flow, and what would export, without staging or changing anything.

## :material-rocket-launch-outline: Initialising JIM

Initialise JIM once in a new deployment, and again after a significant change to apply in order across Connected Systems, such as new [Object Matching Rules](../configuration/object-matching-rules.md) or [Attribute Priority](attribute-priority.md). Initialising means running every Connected System through the pipeline in a fixed order before returning to delta cycles.

**The order:**

1. **Full Import every Connected System.** Joins, scoping and Attribute Flows are only correct when every Connector Space holds current data.
2. **Full Synchronisation every Connected System, sources before targets.** Sources project and join first, so the Metaverse is complete before targets are evaluated. A target that also supplies values (for example a higher-priority import Attribute Flow that keeps an existing account's name) contributes them during its own synchronisation, which cancels any Pending Export that would have overwritten them. Where a Synchronisation Rule [derives a Metaverse Attribute](../configuration/derived-attributes.md) from one another Connected System contributes, synchronise that other system first, so the derived value is right in the same pass.
3. **Review the Pending Exports.** Use [Sync Preview](../configuration/sync-preview.md) to check a sample. This is the last point before anything leaves JIM.
4. **Export.**

**Why not delta runs.** A delta run only sees objects that changed since the last run. On initialisation everything is new to JIM, so every object has to be processed.

**Existing accounts in a target.** An Object Matching Rule joins accounts JIM did not create to the Metaverse Objects your sources project. That join can only form after the target has been imported, which is why Full Import comes first. Joining does not decide which value wins: to keep an existing account's value (an existing `sAMAccountName`, for example), add an import Attribute Flow from that target at a higher priority than the flow that would otherwise supply it. See [Generated values](../configuration/generated-values.md) for how this works with generated Account Names. Keep the Export Run Profile's [safeguards](../configuration/run-profiles.md#safeguards) in place for the first export, so a rule or matching mistake cannot turn it into a mass write.

**After initialisation,** return to normal delta cycles (see [Schedules](../configuration/schedules.md#common-workflows)).

## :material-play-circle-outline: Run Profiles

Each phase runs as a **Run Profile**, an operation configured on a Connected System, started from the portal, the REST API or PowerShell, or by a Schedule. [Run Profiles](../configuration/run-profiles.md#run-types) lists the run types.
