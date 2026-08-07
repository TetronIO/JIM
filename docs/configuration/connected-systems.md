---
title: Connected Systems
---

# Connected Systems

A **Connected System** is any external directory, database, or file that JIM synchronises identity data with. Connected Systems are the endpoints of JIM's hub-and-spoke architecture: they provide source data (e.g. an HR system) and receive provisioned data (e.g. an LDAP directory).

Every Connected System is associated with a [connector](../connectors/index.md) that knows how to talk to its kind of external store, and holds a connector space of imported objects, a discovered schema, and (where applicable) a partition and container hierarchy.

## What a Connected System contains

- **Connection details**<br /> How to reach the external system: server address, credentials, file path, and other connector-specific settings. The Settings tab groups these into a collapsible accordion by category (Connectivity, General, Export, and so on) so dense connector configuration stays easy to scan.
- **Discovered schema**<br /> The object types and attributes available in the external system, populated on first contact.
- **Connector space**<br /> A staging area that holds JIM's local copy of the external system's data.
- **Run Profiles**<br /> Configured operations (import, sync, export) that can be executed against the system.
- **Synchronisation Rules**<br /> The rules that govern how data flows between this system and the metaverse.

## The connector space

The connector space is a critical concept. It is a staging area between the external system and the metaverse: when JIM imports data from a Connected System, it does not write directly to the metaverse. Instead, it creates or updates **Connected System Objects (CSOs)** in the connector space; the metaverse is only updated during the explicit synchronisation phase.

--8<-- "assets/diagrams/sync-pipeline.svg"

<p class="jim-diagram-caption">Imported data is staged in the connector space as Connected System Objects; the Metaverse is only touched during the synchronisation phase, and exports stage the same way in reverse.<span class="jimdg-caption-motion"> Moving dots trace data through the pipeline.</span></p>

This two-stage approach gives you:

- **Isolation**<br /> Problems during import do not corrupt the metaverse.
- **Visibility**<br /> Administrators can inspect imported data before it affects identities.
- **Comparison**<br /> JIM can detect what has changed between imports.
- **Rollback potential**<br /> The metaverse is only updated in the sync phase.

### Opening the Connector Space

A Connected System's page carries two buttons above its tabs, each showing how much is there: **Connector Space** opens the Connected System Objects staged for this system, and **Pending Exports** opens the changes waiting to be written back to it. Both sit above the tabs rather than on one of them, so they are reachable from wherever you are on the page; the Pending Exports count is highlighted whenever changes are waiting.

### Connected System Objects (CSOs)

A **CSO** is JIM's local representation of an object in an external system. Each CSO holds:

- **Distinguished name or anchor**<br /> A unique identifier that maps to the external object.
- **Attributes**<br /> The attribute values as imported from the external system.
- **Link to metaverse**<br /> If the CSO has been joined or projected, it links to a Metaverse Object (MVO).
- **Pending Exports**<br /> Changes queued to be sent back to the external system.

CSOs have a lifecycle:

1. **Created** during import when a new object is discovered in the external system
2. **Updated** during subsequent imports when attribute values change
3. **Joined** or **projected** during synchronisation, to link with an MVO
4. **Obsoleted** when the object no longer exists in the external system

## Partitions and containers

A **partition** is a top-level logical division of a connector space that mirrors a boundary defined by the external system. Partitions exist in JIM primarily to service LDAP-style directories and their naming contexts (NCs): the discrete directory trees that an LDAP server hosts. The separate domain partitions within an Active Directory forest, or the distinct naming contexts exposed by an OpenLDAP server, each surface as a partition in JIM.

Most Connected Systems do not support partitions. A flat file, a SQL table, or a SCIM endpoint has no concept of multiple naming contexts, so its connector space has no partitions.

Inside a partition, or directly inside the connector space of a connector that does not support partitions, you can have **containers**. Containers are a separate, lower-order logical construct that sits beneath partitions; they exist mainly to support LDAP organisational units (OUs) and similar hierarchical groupings. Containers can be nested arbitrarily deep, and JIM loads the full hierarchy so administrators can select nested containers (for example `OU=Contractors,OU=Users,DC=company,DC=local`) for import or export.

!!! note "Partitions and OUs are different concepts"
    Partitions and organisational units (OUs) are distinct. A partition is a top-level boundary on the external system; an OU is a sub-tree within a partition and is modelled in JIM as a container.

| Construct | Scope | Example | Available on |
|-----------|-------|---------|--------------|
| **Partition** | Top-level boundary defined by the external system; discovered, not invented, by JIM | An Active Directory domain naming context (`DC=company,DC=local`) | LDAP-style connectors only |
| **Container** | Sub-tree within a partition, or within the connector space of a non-partitioned system | An OU (`OU=Users,DC=company,DC=local`) | Most connectors that expose hierarchy |

In practice, selecting a partition brings an entire naming context into scope, while selecting containers narrows what is imported within that partition (or within the connector space for connectors that have no partitions).

### What your selections mean

Selection is how you tell JIM which parts of a system it manages, and it binds everywhere:

- A [Run Profile](run-profiles.md) that targets a deselected partition is refused rather than run. The Run Profiles tab marks it, and the property is available over REST and PowerShell so you can find every affected Run Profile at once.
- Exports are refused outside the selected containers, honouring each container's [Container Scope](../connectors/jim-ldap-connector.md#container-scope). Selection means the scope JIM manages, not merely the scope it reads: writing an object where JIM cannot import it back leaves the change unconfirmed and the object treated as deleted on the next Full Import, so JIM would end up churning an object it had just exported. A container set to One Level is not a licence to write anywhere beneath it, only directly within it, because that is exactly what the next import will return. The export fails for that object, naming the Distinguished Name, and the rest of the run continues. A container created by the Connector during the run is in scope, because JIM selects it as soon as the run ends.
- Objects in a deselected partition or container fall out of import scope. A Full Import treats anything it does not find as deleted from the system, so narrowing scope makes the corresponding Connected System Objects obsolete and, on the next synchronisation, disconnects them and recalls the attribute values they contributed. Widen scope again before running a Full Import if that is not what you intended.

### Previewing a partition or container change

Because narrowing scope is silently destructive, the Partitions & Containers tab offers a **Preview Changes** button beside **Save Changes**. It answers what your edited selection would do, without saving it.

The preview reports:

| Transition | What it means |
|---|---|
| Would fall out of scope | Connected System Objects that leave import scope and are not joined to anything. Nothing in the Metaverse changes as a result. |
| Would disconnect from a Metaverse Object | Objects that leave import scope and *are* joined. Each takes the attribute values it contributed out of the Metaverse Object with it. |
| Would become eligible for deletion | Metaverse Objects that those disconnections would leave satisfying their [deletion rule](metaverse.md#deletion-behaviour). These are deletions your selection would set in motion. |
| Would fall in scope | Objects JIM still holds from scope you are re-selecting. |

The counts honour each container's [Container Scope](../connectors/jim-ldap-connector.md#container-scope): beneath a One Level container an import returns nothing, so objects a level deeper are already out of scope and deselecting it takes nothing further away.

Two limits are worth knowing, and the preview states both where they apply:

- **Objects JIM has never imported cannot be counted.** Selecting new scope makes the next Full Import discover objects that are not in the connector space yet, and there is nothing to count until it runs.
- **Some objects cannot be placed.** An object imported before JIM recorded partitions, or one whose Connector cannot say what container an object is in, is left out of the counts entirely rather than guessed at in either direction.

Save after previewing and the confirmation states the preview's counts alongside the properties changing, and the change's [Activity](activities.md) records which preview informed it. Edit the selection after previewing and the preview is marked stale and contributes nothing, because it now describes a different change.

The same evaluation is available to automation: [`New-JIMConfigurationChangePreview -ConnectedSystemId`](../powershell/previews.md) in PowerShell, or `POST connected-systems/{id}/scope-selection/preview` in the [REST API](../../api/reference/). Send the whole proposed selection rather than one flag: what a deselection costs depends on the rest of the selection, because an object leaves scope only when nothing else still covers it.

See [Configuration changes](configuration-changes.md#previewing-a-change-before-you-make-it) for how previews work generally.

### Renames and moves in the source system

JIM identifies partitions and containers by the system's own immutable identifier where one exists (`objectGUID` on Active Directory, `entryUUID` on OpenLDAP), not by their Distinguished Name. Renaming an organisational unit, or moving one to a different parent, therefore keeps your selection intact: the next hierarchy refresh reports it as a rename or a move rather than as one container disappearing and another appearing.

Containers selected before this behaviour shipped record their identifier at their next hierarchy refresh, and continue to be matched on Distinguished Name until then. Refresh the hierarchy once after upgrading to pick it up.

A container that genuinely disappears from the source system is still reported as removed, and the Partitions & Containers tab warns when a removed container was one you had selected.

## Unresolved reference handling

When an import stages a reference attribute value (for example a group member's Distinguished Name) that does not correspond to any object in the connector space, JIM cannot resolve the reference. The most common cause is the referenced object sitting outside the configured [Container Scope](#partitions-and-containers), which can be entirely deliberate: excluding foreign or out-of-remit objects from import is a normal scoping decision.

Each Connected System has an **Unresolved Reference Handling** setting that controls what happens when this occurs during import:

| Mode | Behaviour |
|------|-----------|
| **Error** (default) | Each affected object's Run Profile execution item is marked with an Unresolved Reference error, and the Activity completes with a warning status showing the errored items. Choose this when every reference is expected to resolve. |
| **Warn** | No per-object errors are raised. The Activity completes with a warning carrying a summary of how many references could not be resolved. Choose this when unresolved references are worth a glance but should not read as failures. |
| **Ignore** | No per-object errors and no Activity warning; the import completes successfully. Choose this when unresolved references are expected and benign. |

Whichever mode is selected, genuine data-quality issues remain discoverable:

- **Connected System Objects**<br /> Unresolved reference values stay stored on the affected objects, so they can be inspected on the object's detail page at any time.
- **PowerShell**<br /> `Get-JIMConnectedSystemUnresolvedReferenceCount` reports how many unresolved references a Connected System currently holds.
- **Service log**<br /> Every unresolved reference is logged (at Warning level in Warn mode, Debug level in Ignore mode), along with a summary count at the end of reference resolution.

Set the mode from the **Import Behaviour** panel on the Connected System's Settings tab, with `Set-JIMConnectedSystem -UnresolvedReferenceHandling`, or via the REST API.

## Attribute writability

When JIM retrieves a Connected System's schema, each discovered attribute is recorded with how the system will let JIM write to it. You can see this in the Schema tab's **Writability** column, filter the attribute list by it, and read it from the REST API and PowerShell as the attribute's `writability` value. It is discovered, never set by an administrator: it reflects what the Connected System told JIM.

There are three states.

| Shown as | `writability` | What it means |
|----------|---------------|---------------|
| Writable | `Writable` | The Connected System accepts writes to this attribute. An export Attribute Flow can target it and keep it up to date. |
| Read-Only | `ReadOnly` | The Connected System will not accept writes at all. The attribute can still be imported (`whenCreated` and `objectSid` are useful to hold in the Metaverse), but no export Attribute Flow may target it; JIM refuses the mapping when you try to create it. |
| Set on creation only | `WritableOnCreate` | The Connected System accepts a value only as part of creating the object. An export Attribute Flow may target it, and usually should: without one the object cannot be provisioned. JIM sends the value with the Create Pending Export and never sends it again. |

### Why "Set on creation only" exists

Some attributes are what the Connected System uses to identify the object. A relational table's primary key is the clearest case: JIM has to supply it when it inserts the row, and from then on it is what ties the Connected System Object to that row. Rewriting it later would not update the row, it would point JIM at a different one, and the object JIM thought it was managing would be orphaned. A directory's relative distinguished name has the same shape, being changed by a rename operation rather than by an ordinary attribute write.

JIM therefore treats these attributes as write-once, and enforces it on the export path rather than trusting the configuration to be right:

- **Provisioning**<br /> The value flows normally. It is part of the Create Pending Export, exactly like any other mapped attribute.
- **Updates**<br /> The attribute is excluded from every Update Pending Export, **even when the Metaverse value has changed**. Nothing is sent, and no error is raised: this is the intended behaviour, not a failure.
- **Drift Correction**<br /> A value that has diverged in the Connected System is not treated as drift and is not corrected, because correcting it would mean rewriting the identifier.

If a source value feeding one of these attributes genuinely does change (an employee number is reissued, say), JIM will not chase it into the Connected System. That is deliberate: re-identifying an existing object is a decision for an administrator, not something a synchronisation run should do quietly.

The Attribute Flow editor marks an export mapping whose target is set on creation only, so it is clear at a glance which mappings apply during provisioning alone, and a Connected System Object's detail page marks the attribute itself, so the same is obvious when looking at a single object's values.

## Credential attributes are never managed

Some attributes hold credential material, or a hash of it. JIM will never import them, never let you select them for management, and never let you name them as the source or target of an Attribute Flow:

`unicodePwd`, `userPassword`, `dBCSPwd`, `ntPwdHistory`, `lmPwdHistory`, `supplementalCredentials`, `unixUserPassword`, `msDS-ManagedPassword`

There are two reasons. Most of these cannot be read back meaningfully (a directory returns nothing at all for `unicodePwd`, and opaque blobs for the history attributes), so anything imported would be empty or meaningless and every subsequent synchronisation would see a spurious change. The rest hold live credential material, and anything that reaches the Metaverse is replicated onward to every other Connected System in scope, written into change history, and rendered in the portal.

Passwords are synchronised through JIM's dedicated password channel instead. That channel writes a password to a Connected System and never reads it back, so it is never held in the Metaverse. For LDAP and Active Directory the LDAP Connector writes `unicodePwd` itself, with the correct encoding; see [Setting Passwords](../connectors/jim-ldap-connector.md#setting-passwords) for the connection requirements.

What you will see:

- **Schema refresh**<br /> Credential attributes found in the Connected System are reported as blocked. They are counted as neither added nor removed, because neither is true.
- **Attribute selection**<br /> The Selected switch is disabled, with a tooltip explaining why. Selecting one through the REST API or PowerShell is rejected.
- **Attribute Flow**<br /> Credential attributes do not appear in the source or target attribute lists, and naming one through the REST API or PowerShell is rejected.
- **Upgrades**<br /> If a credential attribute was selected on an existing deployment, the next schema refresh deselects and locks it rather than deleting it, so any Synchronisation Rule that references it stays intact. Remove those Attribute Flows and use the password channel instead.

Attributes that merely *look* credential-bearing, such as `pwdLastSet`, `badPwdCount` and `pwdProperties`, are unaffected and remain fully selectable; they carry no credential material.

## Password policy and the password channel

Where a Connected System can accept passwords, its Schema tab carries a Password Channel panel. It has two jobs: showing you the password rules JIM read from the system itself, and letting you check the channel works before you rely on it.

[Passwords](../concepts/passwords.md) explains the channel as a whole: why passwords do not travel through attribute flow, what discovery can and cannot tell you, and how a refused password is resolved.

### Discovered password policy

JIM reads the target's password policy whenever it retrieves or refreshes the Connected System's schema, and records it, so that configuring a generated password does not mean retyping rules the system already publishes. If a policy is missing, **Refresh Schema** on the Schema tab reads it again. What is shown depends on what the system exposes: minimum length, whether complexity is required and how many character categories that means, password history length, and maximum and minimum password age.

**A discovered policy is a floor, not a guarantee.** Two things routinely make the real rule stricter than the published one:

- **Policies that apply to only some accounts.** Active Directory calls these Fine-Grained Password Policies. Reading them normally requires privileges JIM's service account should not hold, so JIM detects whether any exist rather than enumerating them, and reports one of three answers: none exist, some exist, or it could not tell. "Could not tell" is shown as its own state rather than being treated as "none", because an empty result from a directory is exactly what a caller with no rights over them receives. Where the panel says policies exist or that it could not tell, treat the figures shown as a minimum.
- **Custom password filters.** A system can run its own password rules that are exposed over no protocol at all and cannot be discovered by anything. A password that satisfies everything on this panel can still be refused.

For that reason, handling a refusal is part of how the password channel works rather than an error case, and no amount of discovery removes the need for it.

Only Active Directory and Samba AD publish a policy a client can read. Other directories keep their password rules in configuration that an ordinary connection cannot see, and there is no cross-vendor standard for exposing them, so JIM reports that it found nothing rather than implying the system has no rules.

### Checking the password channel

The **Check password channel** button runs a read-only preflight. It sets no password on anything, so it is safe to run against production at any time.

It reports on four things:

| Check | What it means |
|-------|---------------|
| **Encryption** | Whether the connection is encrypted. A warning rather than a failure, because JIM permits an unencrypted password channel; Active Directory refuses one itself, so there it is very likely to be what stops a password set. |
| **Password mechanism** | Whether the mechanism JIM would use is available: writing `unicodePwd` for Active Directory, or the LDAP Password Modify extended operation elsewhere. A system offering neither is reported as a failure, because JIM will not fall back to writing a password attribute directly. |
| **Reset rights** | Whether the account JIM connects as may reset passwords in each container it manages. Reported per container, since directories grant rights per part of the tree. |
| **Policy discovery** | Whether the password policy could be read, so the generator can be pre-filled from it. Never a failure: an unreadable policy means you configure the rules by hand. |

Each check returns passed, warning, failed, or **could not tell**, and the last of those is deliberately distinct. A directory withholds what a caller may not see by omitting it, not by refusing: an attribute simply absent from a result, or a search returning no rows, both with a success code. Reporting either as a failure would tell you an account lacks rights it demonstrably has. Where the panel says JIM could not tell, the answer has to be confirmed at the system itself.

A preflight is not stored. Reachability, permissions and policy all change without JIM being told, so a result kept on file would go on reassuring you long after it stopped being true.

!!! note "The reset rights check needs somewhere to look"
    Rights are checked in the containers this Connected System manages, by reading the permissions of one ordinary account in each. Select the containers to manage on the Partitions and Containers tab first, or the check has nowhere to look and says so. Accounts held in a directory's privileged groups are skipped: directories periodically overwrite their permissions from a template and switch off inheritance, so a delegation made on the container does not apply to them and sampling one would report the whole container as denied.

### Setting the password on one account

Open a Connected System Object from the connector space and, where the Connector can set passwords, the object carries a **Set Password** button. This writes the password straight to the Connected System: it is not staged as a Pending Export, not retried, and not stored anywhere in JIM.

Use it for the new starter about to sign in for the first time, the account whose provisioning password was refused, and the reset that has to happen now. Routine initial passwords belong on the [Synchronisation Rule](synchronisation-rules.md) that provisions the account, where they happen without anybody watching.

The dialog is built around one rule: **the password is masked from the moment it is generated, and copying it does not require showing it.**

- **Generate** produces a password satisfying the discovered policy, and puts it straight behind a mask.
- **Copy** works while the value is masked, so handing a password to the person who needs it never means putting it on a screen somebody else can read.
- **Reveal** is the secondary action, for reading a password aloud or checking a transcription. It hides itself again after thirty seconds.
- You can type your own password instead of generating one.

Choose what happens to the password once it is set (requiring a change at the next sign-in is the default, and the right one for a password somebody else chose), and whether to enable the account at the same time. Leaving the enable switch off leaves the account's enabled state exactly as it was, which is what a reset on a working account should do.

A Connected System that refuses the password says why, and the dialog stays open carrying its own words so you can try another one. Every attempt is recorded as an Activity against the object, whether it succeeded or not; the Activity records that a password was set, never the password.

!!! warning "This resets the password on whichever account you point it at"
    Anyone who can reach this action can reset the password of any account in this connector space, up to and including privileged ones, subject only to what the Connected System's own service account is permitted to do. Grant the Administrator role accordingly, and scope the service account's rights to the containers JIM manages.

!!! note "Copying and your operating system's clipboard"
    Copying needs an HTTPS connection: browsers deny clipboard access over plain HTTP, and the button says so rather than silently doing nothing. JIM clears the clipboard when the dialog closes where the browser allows it, but your operating system may keep the value in its own clipboard history, which no web page can reach.

The same action is available to automation through `Set-JIMConnectedSystemObjectPassword` and the REST API, which can either take a password you supply or generate one against the discovered policy. A generated password is returned to the caller, once, because they asked for it; nothing is stored either way.

### One password across several Connected Systems

A person often has accounts in more than one place, and conveying a different password for each is both more work and worse for them: four different passwords on a first morning end up on a sticky note. Open a person from the portal and the same **Set Password** action appears there, listing every account they have whose Connector can set a password.

Choose some or all of them and JIM sets one password across them, writing to each Connected System in turn. **Nothing is selected by default**, so resetting a forgotten password in one system never silently resets the others.

The password is generated to satisfy the strictest of the selected systems' rules: the longest minimum length any of them demands, and the character categories all of them count. A category only one system recognises cannot help satisfy another system's complexity rule, so JIM counts only what they have in common. Where a selected system has never published a policy, JIM says so rather than assuming it will accept anything.

Progress runs left to right along the same stepped rail a Run Profile execution uses, one step per Connected System.

!!! warning "There is no transaction across Connected Systems"
    Each write is independent. A run routinely ends with some accounts changed and others not, which leaves the person with a different password in the systems that refused it. JIM says which, in as many words, and offers to retry only the accounts that failed, reusing the password already in hand.

    Where a system refused the **password itself**, retrying it unchanged will fail identically. The guidance on that result offers a fresh password for every account instead, including the ones that already succeeded, because replacing it only where it failed would leave the person with two.

Each account's failure carries guidance you can open, specific to what went wrong: a refused password and an unreachable directory need opposite responses, and the guidance says which of the two you have and whether retrying is worth anything at all.

Every account gets its own Activity, grouped under one parent so the whole action is findable afterwards. Setting a password on a single account records no parent, because a group of one says nothing.

For automation, `Set-JIMMetaverseObjectPassword` does the same thing over the per-account REST endpoint. You must name the Connected Systems, or pass `-AllAccounts`; there is no default, for the same reason the portal preselects nothing.

## Directory Capabilities

The Details tab carries a Directory Capabilities card: read-only facts the Connector has detected about the target system, shown for reference. These are read from data JIM already captured during a previous connection, so viewing the card never opens a new connection. Before the first successful connection, the card shows a hint rather than an error.

Today only the [JIM LDAP Connector](../connectors/jim-ldap-connector.md#directory-capabilities-card) detects and surfaces capabilities (directory type, vendor, DNS host name, paging support, and, where a domain controller has been pinned, the pinned server and its invocation ID); for Connectors that cannot detect capabilities, the card is not shown at all.

## Pending Exports

Changes destined for the Connected System that have been computed by synchronisation but not yet written back. Run an export Run Profile to flush them. Inspecting Pending Exports is the right place to look when you want to know "what is JIM about to change in this system?"

## Configuration changes pending a Full Synchronisation

Most configuration changes do not take effect the moment you save them. Scoping criteria, Attribute Flow, Object
Matching Rules and schema selection all describe what synchronisation *should* do; the change reaches your data only
when synchronisation next runs over the objects it affects. Until then the portal and the configuration are ahead of
reality.

JIM tracks this for you. A Connected System whose configuration has changed in a way that affects synchronisation
outcomes shows an indicator in the Connected Systems list, and a notice on the Connected System page saying how many
changes are waiting and when the last Full Synchronisation ran.

**Only consequential changes count.** Renaming a Connected System or editing a description changes nothing about what
synchronisation does, so it never raises the indicator. Changes are classified as they are recorded, and only the two
classes that alter outcomes are counted:

| Class | Examples | How it shows |
|-------|----------|--------------|
| Sync-affecting | Scoping criteria, Attribute Flow, Object Matching Rules, schema selection | Amber, with the number of changes |
| Destructive | Outbound Deprovision Action, deletion rules, deselecting an Object Type or partition | Red, because applying it can cascade deletions or mass deprovisioning |

**Attribution is precise.** Editing a Metaverse Attribute raises the indicator only on the Connected Systems whose
Synchronisation Rules actually reference that attribute, not on every system. Deleting a Synchronisation Rule raises
it on the system that rule belonged to.

**Two states mean "JIM cannot tell", not "up to date":**

- **Never synchronised.** The Connected System has never completed a Full Synchronisation, so no configuration has
  ever been applied in full and there is nothing to compare against.
- **Unknown.** Configuration change tracking is switched off (see
  [configuration change history](activities.md#configuration-change-history)), so JIM holds no record of what changed.

Both are shown distinctly rather than as a clean result, because reporting a settled configuration JIM cannot vouch
for would be worse than reporting nothing.

!!! note "The reference point is when the run started"
    A change made while a long Full Synchronisation was still running may not have been picked up by it, so it is
    counted as still pending. You may occasionally be prompted for a re-run you did not strictly need; the alternative
    would be hiding a change that really was missed.

Read the same status from automation with `(Get-JIMConnectedSystem -Id <id>).ConfigurationDrift`, or from the
`configurationDrift` object on the REST Connected System response. In both cases, check `IsDeterminable` before
treating `HasPendingChanges` as `false`.

## Confirming a configuration change

Changing a Connected System's settings, schema, or partition selection is confirmed before it saves where the change affects synchronisation. Deselecting an Object Type or a partition is treated as destructive: the Connected System Objects imported through it become obsolete, and whatever they are joined to is deprovisioned on the next synchronisation. See [Configuration changes](configuration-changes.md).

## Common workflows

**Setting up a new Connected System:**

1. Choose the connector type (the connector defines how JIM talks to the external store)
2. Create the Connected System with the chosen connector
3. Configure connector settings (credentials, base DN, file paths, etc.)
4. Import the schema to discover object types and attributes
5. Select the object types and attributes you care about
6. Configure partitions and containers if the connector exposes hierarchy
7. Create [Run Profiles](run-profiles.md) for import, sync, and export operations
8. Add [Synchronisation Rules](synchronisation-rules.md) to define how data flows between this system and the metaverse

**Removing a Connected System:**

1. Run a deletion preview to understand the impact (which Metaverse Objects become disconnected, which Synchronisation Rules become invalid)
2. Delete the Connected System. Small systems are removed immediately; larger systems, or a system with a running sync, are queued and run as a background activity.

Deleting a Connected System records a final snapshot of its configuration in the [configuration change history](activities.md#configuration-change-history), so a decommissioned system's last-known state, and who removed it, remain auditable after it is gone. You can attach an optional reason in the admin portal delete dialog, with `Remove-JIMConnectedSystem -ChangeReason`, or via the REST API. As with all such snapshots, connector secrets are recorded as changed but never stored.

## Manage Connected Systems

- **JIM portal**<br /> Connected Systems area of the admin UI
- **PowerShell**<br /> [Connected Systems cmdlets](../powershell/connected-systems.md) (`Get-JIMConnectedSystem`, `New-JIMConnectedSystem`, `Set-JIMConnectedSystem`, etc.)
- **REST API**<br /> Connected Systems endpoints in the [interactive API reference](../../api/reference/)

## See also

- [Connectors](../connectors/index.md) -- the connector types JIM ships with, and what each one does
- [Concepts: Architecture](../concepts/architecture.md) -- how Connected Systems fit into JIM's hub-and-spoke model
- [Run Profiles](run-profiles.md) -- the operations executed against a Connected System
- [Synchronisation Rules](synchronisation-rules.md) -- how data flows between a Connected System and the metaverse
