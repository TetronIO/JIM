---
title: Connected Systems
description: What a Connected System is and holds, and where to configure its Connector Space, scope, schema, passwords and removal.
type: object
audience: administrator
validated: v0.17.0
---

# Connected Systems

A **Connected System** is any external directory, database, or file that JIM synchronises identity data with. Connected Systems are the endpoints of JIM's hub-and-spoke architecture: they provide source data (e.g. an HR system) and receive provisioned data (e.g. an LDAP directory).

```jim-diagram
archetype: anatomy
title: What a Connected System holds
desc: "A Connected System holds its connection details, its discovered schema of Object Types and attributes, its Connector Space of imported objects, partitions and containers where its Connector has them, the Run Profiles executed against it, the Synchronisation Rules that govern how data flows between it and the Metaverse, and its Pending Exports: changes computed by synchronisation but not yet written back. Each is associated with a Connector, which knows how to talk to its kind of external store."
subject:
  kind: object
  label: Connected System
  parts:
    - { label: Connection details, detail: how to reach the external system }
    - { label: Discovered schema, detail: Object Types and attributes }
    - { label: Connector Space, detail: JIM's local copy of its data }
    - { label: Partitions and containers, detail: where its Connector has them }
    - { label: Run Profiles, detail: "import, sync and export" }
    - { label: Synchronisation Rules, detail: how data flows with the Metaverse }
    - { label: Pending Exports, detail: changes not yet written back }
properties:
  - { name: Connector, value: knows how to talk to it }
```

Every Connected System is associated with a [connector](../connectors/index.md) that knows how to talk to its kind of external store, and holds a Connector Space of imported objects, a discovered schema, and (where applicable) a partition and container hierarchy.

## :material-server-network-outline: What a Connected System contains

- **Connection details**<br /> How to reach the external system: server address, credentials, file path, and other connector-specific settings. The Settings tab groups these into a collapsible accordion by category (Connectivity, General, Export, and so on) so dense connector configuration stays easy to scan.

    The Schema, Scope and Matching tabs stay unavailable until the required settings are filled in, because none of them can do anything useful without them. That gate is about the settings themselves, not about the external system being reachable: saving the Settings tab also tests the connection and tells you what it found, but a system that is down for maintenance does not take those tabs away, and you can keep working on the configuration while it is.
- **Discovered schema**<br /> The object types and attributes available in the external system, populated on first contact.
- **Connector Space**<br /> A staging area that holds JIM's local copy of the external system's data.
- **Run Profiles**<br /> Configured operations (import, sync, export) that can be executed against the system.
- **Synchronisation Rules**<br /> The rules that govern how data flows between this system and the Metaverse.

## :material-database-outline: The Connector Space

Each Connected System has a **Connector Space**: a staging area between the external system and the Metaverse, holding a Connected System Object for each object JIM has imported. [Connector Space](connector-space.md) covers what it holds, opening it, objects awaiting deprovisioning, and clearing it.

## :material-folder-network-outline: Partitions and containers

Where a Connector has partitions and containers, such as the LDAP Connector, the **Scope** tab is where you choose which of them JIM manages. [Partitions and Containers](partitions-and-containers.md) covers the choices, stating them as text, and previewing a change before you make it.

## :material-link-lock: Unresolved reference handling

A reference to an object JIM does not hold, such as a group member outside the Container Scope, stays unresolved, and the Connected System's **Unresolved Reference Handling** decides how a run reports it. [Unresolved References](unresolved-references.md) covers the setting, finding out why a reference is unresolved, and the export side.

## :material-table-column: Schema and attributes

The **Schema** tab's selections decide what JIM reads from the system. [Connected System Schema](connected-system-schema.md) covers what deselecting means, refreshing the schema, and an object that changes Object Type; [Connected System Attributes](connected-system-attributes.md) covers each attribute's writability and data type, and why credential attributes are never managed.

## :material-form-textbox-password: Passwords

Where the system accepts passwords, its **Passwords** tab shows the password policy JIM read from it, checks the password channel, and sets Password Synchronisation. [Connected System Passwords](connected-system-passwords.md) covers each.

## :material-eye-check-outline: Detected facts

At the top of the Details tab, above the form, a **Detected** strip lists read-only facts the Connector has detected about the target system, shown for reference. These are read from data JIM already captured during a previous connection, so viewing them never opens a new connection. Before the first successful connection, the strip says nothing has been detected yet.

Of the built-in Connectors, only the [LDAP Connector](../connectors/jim-ldap-connector.md#detected-facts) detects and surfaces facts (directory type, vendor, DNS host name, paging support, and, where a domain controller has been pinned, the pinned server and its invocation ID); for Connectors that cannot detect facts, the strip is not shown at all.

## :material-tray-full: Pending Exports

Changes destined for the Connected System that have been computed by synchronisation but not yet written back. Run an export Run Profile to flush them. Inspecting Pending Exports is the right place to look when you want to know "what is JIM about to change in this system?"

The list filters by change type (**Create**, **Update** or **Delete**) and by status. Each Pending Export has a status:

| Status | What it means |
|---|---|
| **Pending** | Waiting for the next export run. |
| **Executing** | Being written by an export run now. |
| **Exported** | Written to the Connected System, and waiting for an import to confirm it. |
| **Export Not Confirmed** | Written, but an import did not show every value in place; the next export run sends those values again. |
| **Parked** | Held for your decision about a Generated Value the Connected System refused; see [When a target rejects a value](generated-values-in-target-systems.md#when-a-target-rejects-a-value). |

**JIM never gives up on a Pending Export.** One the Connected System refuses stays **Pending** and is attempted again at every export run, and reported on every run, until it is written or no longer needed. Its **Failed Attempts** count and **Failing Since** time show how long it has been failing; turn on **Failing only** to list just those, and the Connected System's page shows a **Failing Exports** count, with how long the longest has been failing, while there are any. The cause is nearly always something to fix in the Connected System (a permission, a schema rule); once it is fixed, the next export run writes the change with nothing to reset. [Troubleshooting Synchronisation](../administration/troubleshooting-synchronisation.md) explains each refusal and its fix.

**The next import confirms an export.** After an export run writes a change, its Pending Export stays **Exported** until an import from the Connected System shows the values in place, and then clears. Values the import does not show are sent again at the next export run, however many times they have been sent, and reported on every import that misses them (see [Troubleshooting Synchronisation](../administration/troubleshooting-synchronisation.md#export-pending-we-exported-a-change-but-did-not-get-confirmation-of-it)). The File and SQL Connectors confirm their own writes instead, so their Pending Exports clear as soon as the export succeeds. A Pending Export left **Executing** by a Worker that stopped part-way returns, when the Worker starts again, to **Exported** if any of it was sent and to **Pending** if none was.

**Cancelling an export run keeps what it wrote.** The changes already written are recorded, with their Run Profile Execution Items, and the changes not yet written go back to the status they had, to be sent by the next run, without counting a failed attempt against them. The SQL and SCIM Connectors finish the change they are writing (with SCIM bulk operations, the whole bulk request), and give up on it if it is still going 30 seconds later, counting one failed attempt; the LDAP Connector finishes the batch it is writing first. A batch that fails as a whole (the Connected System stops answering part-way through, say) counts one failed attempt for each change in it, and each change gets a Run Profile Execution Item naming its object.

A Pending Export's detail page names two distinct things about each attribute change, side by side: **Attribute Flow** is the outbound Synchronisation Rule that staged the change, and **Value from** is where the underlying Metaverse value came from (a Connected System through an import Attribute Flow, a computed expression, or a Generated Value), linking into that attribute's inspector on the source Metaverse Object. Troubleshooting a wrong exported value usually needs both: which rule sent it, and which source put it on the Metaverse Object in the first place.

## :material-database-clock-outline: Changes awaiting synchronisation

A saved configuration change reaches existing objects only when a synchronisation next runs over them, so for a while the portal and the configuration are ahead of your data. A Connected System shows that wait in two ways, because they ask different things of you: a change the next synchronisation applies, and a change that is pending a Full Synchronisation. [When a change takes effect](configuration-changes.md#when-a-change-takes-effect) says which change is which.

### Changes the next synchronisation applies

Saving an import Synchronisation Rule or an Object Matching Rule, or changing an Attribute Priority order, records the change as awaiting synchronisation for the Connected System Object Type it affects. The Connected System's page shows one notice per Object Type, **Changes awaiting synchronisation: {Type}**, listing each change (linked to its Activity, with when it was saved and by whom). **Run now** opens the queue dialog with the Delta Synchronisation Run Profile chosen, or the Full Synchronisation one where the system has no Delta; you can pick another. The Connected Systems list marks the system with a chip showing how many changes are waiting, and the rule shows an **Awaiting synchronisation** chip.

The Connected System's next synchronisation, Delta or Full, processes every object of that Object Type and only the changed objects of the others, then marks the changes applied:

- **Several changes, one run**<br /> Every change saved before a run starts is applied by that run.
- **A change saved during a run waits for the next one**<br /> The running synchronisation carries on with the rules it started with.
- **A failed or cancelled run applies nothing**<br /> The next run processes the Object Type again, and the notice stays until a run completes.
- **The cost is the whole Object Type**<br /> On a large Object Type, the next Delta Synchronisation takes as long as processing every object of that type. The notice gives the object count: run it in a quiet period, or choose **Run now** to start it straight away.
- **Export follows on its own**<br /> Every Metaverse Object the Object Type joins or projects is processed too, so export evaluation runs on them as usual.

The synchronisation's Activity has a section, **Processed every {Type} object**, listing the changes behind it and whether it applied them. Each change's own Activity says **Applied by** that run, or **Awaiting the next synchronisation** of the system.

### Changes pending a Full Synchronisation

Some changes no ordinary synchronisation applies by itself. A Connected System with such a change since its last Full Synchronisation shows an indicator in the Connected Systems list, and a notice on its page saying how many changes are waiting and when the last Full Synchronisation ran.

**Only consequential changes count.** Renaming a Connected System or editing a description changes nothing about what synchronisation does, so it never raises the indicator. Changes are classified as they are recorded, and only the two classes that alter outcomes are counted:

| Class | Examples | How it shows |
|-------|----------|--------------|
| Sync-affecting | An export Attribute Flow, a Metaverse Attribute, schema selection | Amber, with the number of changes |
| Destructive | Deletion rules, deselecting an Object Type or partition | Red, because applying it can cascade deletions or mass deprovisioning |

A change to an import Synchronisation Rule or an Object Matching Rule is not counted: the next synchronisation applies it. Nor is a change to an export rule's scope or Deprovisioning Action: the next synchronisation of any Connected System reviews every Metaverse Object against the rule as it now stands. Only what no synchronisation applies by itself counts, so a save that changes an export rule's scope and its Attribute Flow raises the indicator for the Attribute Flow edit alone.

**Attribution is precise.** Editing a Metaverse Attribute raises the indicator only on the Connected Systems whose Synchronisation Rules actually reference that attribute, not on every system. Deleting a Synchronisation Rule raises it on the system that rule belonged to.

**Two states mean "JIM cannot tell", not "up to date":**

- **Never synchronised.** The Connected System has never completed a Full Synchronisation, so no configuration has
  ever been applied in full and there is nothing to compare against.
- **Unknown.** Configuration change tracking is switched off (see
  [Configuration Change History](activities.md#configuration-change-history)), so JIM holds no record of what changed.

Both are shown distinctly rather than as a clean result, because reporting a settled configuration JIM cannot vouch
for would be worse than reporting nothing. Changes awaiting synchronisation are reported in both states.

!!! note "The reference point is when the run started"
    A change made while a long Full Synchronisation was still running may not have been picked up by it, so it is
    counted as still pending. You may occasionally be prompted for a re-run you did not strictly need; the alternative
    would be hiding a change that really was missed.

Read both from automation with `(Get-JIMConnectedSystem -Id <id>).ConfigurationDrift`, or the `configurationDrift` object on the REST Connected System response. `AwaitingSynchronisation` lists the Object Types waiting, each with its changes and object count. Check `IsDeterminable` before treating `HasPendingChanges` as `false`.

## :material-eye-outline: Previewing a Full Synchronisation

A Full Synchronisation processes every object in the Connected System, and can provision, update, disconnect or delete accounts
across every Connected System the run reaches. A **Full Synchronisation preview** works out what a Full Synchronisation
of this Connected System would do, against the objects as they stand, changing nothing, so you can read the
consequences before you run it.

**Starting it.** Choose **Preview Full Synchronisation** on the notice about configuration changes waiting for a Full
Synchronisation, or the preview action (the eye) beside a Full Synchronisation Run Profile on the **Run Profiles** tab.
Import, Export and Delta Synchronisation Run Profiles have no preview action, because a preview of the whole population
says nothing true about a run that processes something else. The preview opens on the **Details** tab and runs in the
background like any other [Configuration Change Preview](configuration-changes.md#previewing-a-change-before-you-make-it):
you can cancel it, or leave the page and come back, and the tab picks up the latest preview until a Full
Synchronisation of the system has run since it.

**Large systems.** Above the size at which JIM asks how much detail a preview should keep, the same question also says
roughly how long the preview will take, so you decide before it starts. The estimate is never faster than a
conservative reference rate, and slower where this system's last Full Synchronisation was.

**Reading it.** The preview leads with the worst consequence, then lists each change by Connected System and attribute
under **What would change**, including updates, provisioning and deprovisioning in the target systems the run reaches,
Drift Corrections, and objects the run would fail. Updates already queued for a target that would no longer be exported,
because the target already holds the value, are listed as **Pending Export changes withdrawn**. Objects the run would
leave as they are are counted on one line under the list rather than as rows of it. Select a change to see the objects
it applies to, with each value **Now** and **After the synchronisation**; for withdrawn changes the columns are
**Queued**, the change that was waiting to be exported, and **Target keeps**, the value the target already holds and
keeps. Select an object to see everything the run would do to it, as its own
[Sync Preview](sync-preview.md) shows it. Where every object would stay as it is, the preview says the Full
Synchronisation would change nothing.

**Running it.** **Run Full Synchronisation** on the preview queues the run, and the run's Activity records that it was
informed by the preview. If data or configuration has changed since the preview started, the panel says so and names
what changed, for example "Run Profile 'Delta Import' ran on Connected System 'HR Import'". Any run on any Connected
System counts, so with a schedule running this is common. Running from such a preview asks first: **Preview again**
gives you a current answer, and **Run anyway** queues the run. A run started anyway still records the preview you read,
and its Activity says the preview was out of date and what overtook it.

**Automating it.** Scripts get the same evaluation:
[`New-JIMConfigurationChangePreview -ConnectedSystemId <id> -FullSynchronisation`](../powershell/previews.md#new-jimconfigurationchangepreview)
in PowerShell, or `POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/full-synchronisation/preview` in the [REST API](../../api/reference/).
The start result's estimated duration says roughly how long to wait, and `-Wait` waits that long by default. Pass the
finished preview's Activity id to
[`Start-JIMRunProfile -PreviewActivityId`](../powershell/run-profiles.md#start-jimrunprofile), or as `previewActivityId`
when executing the Run Profile through the REST API, to record it on the run; JIM refuses a run citing anything other
than a completed Full Synchronisation preview of that same system, and queues nothing. A run citing a preview that
something has overtaken still queues: its Activity records that the preview was out of date, and PowerShell writes a
warning saying what overtook it (the REST response carries it in `warnings`).

## :material-pencil-lock-outline: Confirming a configuration change

Changing a Connected System's settings, schema, or partition selection is confirmed before it saves where the change affects synchronisation. Deselecting an Object Type or a partition is treated as destructive: the next Full Import marks the Connected System Objects imported through it obsolete, and the following synchronisation disconnects them and deprovisions whatever they are joined to. See [Configuration changes](configuration-changes.md).

## :material-playlist-check: Common workflows

**Setting up a new Connected System:**

1. Choose the connector type (the connector defines how JIM talks to the external store)
2. Create the Connected System with the chosen connector
3. Configure connector settings (credentials, base DN, file paths, etc.)
4. Import the schema to discover object types and attributes
5. Select the object types and attributes you care about
6. Configure partitions and containers if the connector exposes hierarchy
7. Create [Run Profiles](run-profiles.md) for import, sync, and export operations
8. Add [Synchronisation Rules](synchronisation-rules.md) to define how data flows between this system and the Metaverse

**Removing a Connected System:** deleting one asks what should happen to everything it managed, with the impact counted before you choose. [Removing a Connected System](removing-a-connected-system.md) explains both choices, and how to preview the impact on every other system first.

## :material-cog-outline: Manage Connected Systems

- **JIM portal**<br /> Connected Systems area of the admin UI
- **PowerShell**<br /> [Connected Systems cmdlets](../powershell/connected-systems.md) (`Get-JIMConnectedSystem`, `New-JIMConnectedSystem`, `Set-JIMConnectedSystem`, etc.)
- **REST API**<br /> Connected Systems endpoints in the [interactive API reference](../../api/reference/)

## :material-book-open-variant-outline: See also

- [Connectors](../connectors/index.md): the connector types JIM ships with, and what each one does
- [Concepts: Architecture](../concepts/architecture.md): how Connected Systems fit into JIM's hub-and-spoke model
- [Run Profiles](run-profiles.md): the operations executed against a Connected System
- [Synchronisation Rules](synchronisation-rules.md): how data flows between a Connected System and the Metaverse
