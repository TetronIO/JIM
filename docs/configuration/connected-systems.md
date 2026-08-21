---
title: Connected Systems
---

# Connected Systems

A **Connected System** is any external directory, database, or file that JIM synchronises identity data with. Connected Systems are the endpoints of JIM's hub-and-spoke architecture: they provide source data (e.g. an HR system) and receive provisioned data (e.g. an LDAP directory).

Every Connected System is associated with a [connector](../connectors/index.md) that knows how to talk to its kind of external store, and holds a connector space of imported objects, a discovered schema, and (where applicable) a partition and container hierarchy.

## What a Connected System contains

- **Connection details**<br /> How to reach the external system: server address, credentials, file path, and other connector-specific settings. The Settings tab groups these into a collapsible accordion by category (Connectivity, General, Export, and so on) so dense connector configuration stays easy to scan.

    The Schema, Partitions &amp; Containers and Matching tabs stay unavailable until the required settings are filled in, because none of them can do anything useful without them. That gate is about the settings themselves, not about the external system being reachable: saving the Settings tab also tests the connection and tells you what it found, but a system that is down for maintenance does not take those tabs away, and you can keep working on the configuration while it is.
- **Discovered schema**<br /> The object types and attributes available in the external system, populated on first contact.
- **Connector space**<br /> A staging area that holds JIM's local copy of the external system's data.
- **Run Profiles**<br /> Configured operations (import, sync, export) that can be executed against the system.
- **Synchronisation Rules**<br /> The rules that govern how data flows between this system and the metaverse.

## The connector space

The connector space is a critical concept. It is a staging area between the external system and the metaverse: when JIM imports data from a Connected System, it does not write directly to the metaverse. Instead, it creates or updates **Connected System Objects (CSOs)** in the connector space; the metaverse is only updated during the explicit synchronisation phase.

--8<-- "assets/diagrams/sync-pipeline.svg"

<p class="jim-diagram-caption">Every Connected System has its own connector space, named for it here; imported data is staged there as Connected System Objects, the Metaverse is only touched during the synchronisation phase, and exports stage the same way in reverse. The systems shown are illustrative.<span class="jimdg-caption-motion"> Moving dots trace data through the pipeline.</span></p>

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

### How many objects each container holds

Each container row shows how many objects it holds, so you can tell a container worth managing from an empty one before you tick anything.

The figure is read from the Connected System itself, not from what JIM has already imported, so it is there the first time you open the tab on a brand new Connected System. That is the moment it matters most: you are deciding what to manage, and JIM holds nothing yet.

- **The figure follows the container's [Container Scope](../connectors/jim-ldap-connector.md#container-scope).** A container set to This and below reports what its whole branch holds; one narrowed to This level reports only what sits directly in it. Hover the number to see both, and which of them is on screen.
- **Only the Object Types you have selected are counted**, so the number matches what a Full Import would actually bring back. Select them on the Schema tab first; nothing is counted until you have.
- **Zero and blank mean different things.** Zero is a container that was searched and found empty. A blank means nobody has counted it: either the Connector cannot report counts, the hierarchy has not been retrieved since this feature shipped, or counting was cut short, in which case the hierarchy refresh's Activity says so and why.
- **Selections and exclusions are ignored.** The figure says what is in the container, not what JIM would import from it once your exclusions apply. [Preview Changes](#previewing-a-partition-or-container-change) answers that second question.

Counts are gathered as part of **Retrieve Hierarchy**, so refreshing the hierarchy refreshes the numbers, and the tab tells you when it last ran. Counting is bounded: if it takes longer than a minute, or the directory stops the search at its own size or time limit, JIM discards that partition's figures rather than showing numbers that are quietly short of the truth, and the refresh's Activity completes with a warning naming the partition and what stopped the count. Raising the directory's limit, or narrowing the selected Object Types, is the usual fix. The hierarchy itself still arrives either way.

!!! note "This reads your directory"
    Counting means retrieving the matching entries, because LDAP has no count operation. JIM asks for names only, which is far lighter than an import, and runs one search per partition rather than one per container. It is still a read against your production directory, so it happens when you retrieve the hierarchy and at no other time.

### What your selections mean

Selection is how you tell JIM which parts of a system it manages, and it binds everywhere:

- A [Run Profile](run-profiles.md) that targets a deselected partition is refused rather than run. The Run Profiles tab marks it, and the property is available over REST and PowerShell so you can find every affected Run Profile at once.
- Exports are refused outside the selected containers, honouring each container's [Container Scope](../connectors/jim-ldap-connector.md#container-scope). Selection means the scope JIM manages, not merely the scope it reads: writing an object where JIM cannot import it back leaves the change unconfirmed and the object treated as deleted on the next Full Import, so JIM would end up churning an object it had just exported. A container set to One Level is not a licence to write anywhere beneath it, only directly within it, because that is exactly what the next import will return. The export fails for that object, naming the Distinguished Name, and the rest of the run continues. A container created by the Connector during the run is in scope, because JIM selects it as soon as the run ends.
- Objects in a deselected partition or container fall out of import scope. A Full Import treats anything it does not find as deleted from the system, so narrowing scope makes the corresponding Connected System Objects obsolete and, on the next synchronisation, disconnects them and recalls the attribute values they contributed. Widen scope again before running a Full Import if that is not what you intended.

### Stating Container Scope as text (Advanced Mode)

The Partitions & Containers tab offers two ways to edit the same Container Scope, switched with **Simple** and **Advanced**:

- **Simple** is the tree: tick the Containers you manage, and set each one's [Container Scope](../connectors/jim-ldap-connector.md#container-scope).
- **Advanced** is the same scope written out, one statement per line. It is for the hierarchy that is impractical to click through, and for keeping a scope under version control, reviewing it as a diff, or copying it between Connected Systems.

```text
include OU=Corp,DC=example,DC=com
exclude OU=Service Accounts,OU=Corp,DC=example,DC=com
include OU=App1,OU=Service Accounts,OU=Corp,DC=example,DC=com
```

Each line is a directive, an optional `one-level`, then the Container's path:

| Statement | Means |
|---|---|
| `include <path>` | Manage this Container and everything beneath it. `+` is accepted as shorthand. |
| `include one-level <path>` | Manage the objects held directly in this Container, and no Container beneath it. |
| `exclude <path>` | Carve this Container out of the selection an ancestor made. `-` is accepted as shorthand. |
| `exclude one-level <path>` | Carve out the objects held directly in this Container, leaving the Containers beneath it as their ancestors had them. |

Blank lines are ignored, and so is any line beginning with `#`. Comments are whole-line only, because a Distinguished Name may itself contain a `#`.

The text states **the whole** of Container Scope, not a change to it. A Container the text does not name states nothing, so removing a line is how a Container is deselected, and empty text clears the scope entirely. Partition selection is left alone, except that naming a Container selects the partition holding it.

Nothing is applied by halves. Each of these is refused, naming the line at fault, with the scope left exactly as it was:

- a path that names no Container JIM has discovered (retrieve the hierarchy if the Container is new);
- the same Container stated twice, because a Container states one thing about itself;
- a statement an ancestor already makes, which would change nothing.

**Apply** edits the selection, exactly as ticking a box does; nothing reaches the Connected System until you **Save Changes**, so Advanced Mode gets the same preview and the same confirmation as the tree. Switching back to **Simple** applies the text first rather than discarding it, and every scope expressible one way is expressible the other, so nothing is lost in either direction.

Automation has the same surface: [`Get-JIMConnectedSystemContainerScopeText`](../powershell/connected-systems.md) and [`Set-JIMConnectedSystemContainerScopeText`](../powershell/connected-systems.md) in PowerShell, or `GET`/`PUT connected-systems/{id}/container-scope-text` in the [REST API](../../api/reference/).

### Previewing a partition or container change

Because narrowing scope is silently destructive, the Partitions & Containers tab offers a **Preview Changes** button beside **Save Changes**. It answers what your edited selection would do, without saving it.

The preview reports:

| Transition | What it means |
|---|---|
| Leaves import scope | Connected System Objects that leave import scope and are not joined to anything. Nothing in the Metaverse changes as a result. |
| Disconnects from its Metaverse Object | Objects that leave import scope and *are* joined. Each takes the attribute values it contributed out of the Metaverse Object with it. |
| Becomes eligible for deletion | Metaverse Objects that those disconnections would leave satisfying their [deletion rule](metaverse.md#deletion-behaviour). These are deletions your selection would set in motion. |
| Enters import scope | Objects JIM still holds from scope you are re-selecting. |

The counts honour each container's [Container Scope](../connectors/jim-ldap-connector.md#container-scope): beneath a One Level container an import returns nothing, so objects a level deeper are already out of scope and deselecting it takes nothing further away.

Two limits are worth knowing, and the preview states both where they apply:

- **Objects JIM has never imported cannot be counted.** Selecting new scope makes the next Full Import discover objects that are not in the connector space yet, and there is nothing to count until it runs.
- **Some objects cannot be placed.** An object imported before JIM recorded partitions, or one whose Connector cannot say what container an object is in, is left out of the counts entirely rather than guessed at in either direction.

Save after previewing and the confirmation opens with the preview's own sentence, alongside the properties changing, and the change's [Activity](activities.md) records which preview informed it. Edit the selection after previewing and the preview is marked stale and contributes nothing, because it now describes a different change.

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

The same setting also governs **ambiguous references**: a reference value that matches objects of more than one Object Type, where the attribute does not declare which Object Type it points at. Two Object Types may legitimately share an anchor value space (a view over a table has the table's keys by construction), so JIM never resolves such a reference by guessing; it is reported per the mode above, with a message naming the candidate Object Types. Where the Connector's schema can declare the reference's target Object Type (the [SQL Connector](../connectors/jim-sql-connector.md)'s `referencesObjectType`), declaring it removes the ambiguity entirely: the reference resolves within the declared Object Type alone.

Whichever mode is selected, genuine data-quality issues remain discoverable:

- **Connected System Objects**<br /> Unresolved reference values stay stored on the affected objects, so they can be inspected on the object's detail page at any time.
- **PowerShell**<br /> `Get-JIMConnectedSystemUnresolvedReferenceCount` reports how many unresolved references a Connected System currently holds.
- **Service log**<br /> Every unresolved reference is logged (at Warning level in Warn mode, Debug level in Ignore mode), along with a summary count at the end of reference resolution.

Set the mode from the **Import Behaviour** panel on the Connected System's Settings tab, with `Set-JIMConnectedSystem -UnresolvedReferenceHandling`, or via the REST API.

### On export

The same setting governs the export side. When JIM writes a reference attribute (a manager, a group's members) it needs the referenced object's own identifier in the target Connected System, which it can only have once that object exists there. A Pending Export whose references cannot all be resolved yet is not held back whole: everything that can be written is written now (a Create inserts the row without the reference columns; a group gains the members that can be resolved), and the Pending Export stays pending, carrying only the reference values still owed, until they resolve. It is retried on the deferred cadence and finished when the referenced objects can be addressed.

JIM tells two situations apart at export time:

| Situation | What it means | What happens |
|-----------|---------------|--------------|
| **Awaiting anchor** | The referenced object has a Connected System Object in this Connected System, but its own export has not been executed or confirmed yet, so it has no anchor to point at. | Ordinary ordering. Nothing is reported; the reference is written on a later run. |
| **Not in this Connected System** | The referenced object has no Connected System Object in this Connected System at all: it is out of scope for every Synchronisation Rule into the system, or has not been provisioned. | The reference cannot be written as things stand, and is reported per the mode above: **Error** marks the referring object's Run Profile execution item with an Unresolved Reference error naming the attribute and the referenced object, and the Activity completes with a warning; **Warn** completes the Activity with a warning carrying a summary count; **Ignore** logs only. |

An export that wrote in part is counted as succeeded on the Activity ("43 succeeded (4 written in part, awaiting references)"), because something was written; the Pending Export detail page lists each reference still owed with its reason, and the same detail is available from `Get-JIMPendingExport -Id` and the REST API as `unresolvedReferences`. A partial write that the target refuses (a reference column declared `NOT NULL`, say) fails that object with an ordinary export error, and is retried like any other failure.

## What deselecting means

The Schema tab's ticks decide what JIM **reads**. That is narrower than it looks, and worth being precise about, because deselecting has no visible effect at all: nothing fails, nothing is deleted, and nothing is disconnected.

| What you deselect | What actually happens |
|---|---|
| An **Object Type** | JIM stops importing it. The Connected System Objects already imported from it are left exactly as they are: still joined to their Metaverse Objects, and still contributing the values they last imported, which stop being refreshed. Nothing is obsoleted and nothing is deprovisioned. |
| An **attribute** | JIM stops fetching it. The values already held for it stay on the Connected System Objects, and any Attribute Flow reading it goes on flowing them, without them ever being refreshed again. |

Neither is the same as deselecting a partition or a container. Those take objects out of an import that still runs, so the next Full Import does not find them, marks them obsolete, and the following synchronisation disconnects them. Deselecting an Object Type removes it from that comparison altogether, so its objects are never looked for.

Selection is also an **import-side** idea only. Synchronisation and export do not consult it: an export Attribute Flow whose target attribute is deselected still writes it.

So if your intent is to take a type genuinely out of management, deselecting it is not sufficient on its own. Disable or delete the Synchronisation Rules that manage it too, or its objects will go on contributing stale values indefinitely.

### Obsoletion and contributed values

Each Object Type carries **Remove Contributed Attributes On Obsoletion**, which decides what happens to the Metaverse values one of its objects contributed when that object is obsoleted:

- **On** (the default) withdraws them. Where another Connected System still contributes the attribute it is handed over to that source; where none does, the value is cleared. A [deletion grace period](metaverse.md#deletion-behaviour) preserves a value with no surviving contributor until the grace window resolves.
- **Off** leaves them on the Metaverse Object. They stop tracking anything from that point, and nothing reports them as stale.

### Previewing a schema change

The Schema tab offers a **Preview Changes** button beside **Save Changes**, which answers what your edited selection would do without saving it.

The preview reports:

| Transition | What it means |
|---|---|
| Stops being imported, stays joined | Connected System Objects that would stop being imported. Where the row names an attribute, only that attribute freezes; otherwise the whole object does. |
| Imported again | Objects, or attribute values, that would start tracking the Connected System again. |
| Contributed values withdrawn | Metaverse Objects that would have this system's contributed values withdrawn when their obsolete objects are next synchronised. |
| Contributed values kept | The inverse: values that would be left in place instead. |

Two things the preview is deliberately careful about:

- **Only the objects that hold a value are counted for an attribute.** An object with nothing stored for a deselected attribute has nothing to freeze, so counting it would inflate the answer with objects the change does not touch.
- **The obsoletion toggle is counted against the objects already obsolete and still joined**, which are the only ones whose fate it changes now. Objects obsoleted in future are governed by the setting too, but there is no population to count yet.

Validation names what would go on running over the frozen data: Synchronisation Rules still bound to an Object Type you are deselecting, and Attribute Flow mappings still reading an attribute you are deselecting. Deselecting an External ID is refused outright.

Save after previewing and the confirmation opens with the preview's own sentence, and the change's [Activity](activities.md) records which preview informed it. Edit the selection after previewing and the preview is marked stale and contributes nothing, because it now describes a different change.

The same evaluation is available to automation: [`New-JIMConfigurationChangePreview -ConnectedSystemId -SchemaObjectType`](../powershell/previews.md) in PowerShell, or `POST connected-systems/{id}/schema-selection/preview` in the [REST API](../../api/reference/). Every omission means "leave this as it stands", so a request changing one flag cannot accidentally propose deselecting a whole Object Type.

See [Configuration changes](configuration-changes.md#previewing-a-change-before-you-make-it) for how previews work generally.

## Refreshing the schema

The Schema tab's **Refresh Schema** button retrieves the latest object types and attributes from the Connected System. The first retrieval simply records what it finds; a refresh with a schema already in place shows you a **preview** of what changed before anything is applied, so a source system that has drifted never rewrites JIM's configuration behind your back.

The preview reports, per object type:

| Change | What applying it does |
|---|---|
| Object types or attributes **added** | Recorded and available for selection. Additions cannot affect anything that already works. |
| Object types or attributes **no longer reported** | **Retained** in JIM; nothing is deleted by a refresh. Their values stop refreshing from that point, and any Synchronisation Rule or Attribute Flow reading them works from stale data, so the preview flags them for your attention. |
| Attribute **definitions changed** (data type or plurality) | The new definition is recorded. A mapping validated against the old definition may no longer behave as intended, so these are flagged too. A data type you [overrode yourself](#overriding-an-inferred-type) is never overwritten, and never appears here. |

You then choose:

- **Apply Schema Changes** records the refresh, exactly as previewed, under an ImportSchema [Activity](activities.md).
- **Discard** drops it; JIM's schema stays exactly as it was. Where the preview found removals or definition changes, discarding means JIM's configuration no longer matches the Connected System, and the next synchronisation runs against that mismatch; you are asked to confirm that you understand. Discarding additions alone needs no confirmation, because the next refresh simply finds them again.

Watch the preview's **discovery warnings** before applying: a Connected System identity without permission to read the full schema produces a partial read, which can make object types or attributes appear removed when they are not.

The same flow is available to automation: `Import-JIMConnectedSystemSchema -Preview` in [PowerShell](../powershell/connected-systems.md#import-jimconnectedsystemschema) returns the preview result (its `HasRemovalsOrDefinitionChanges` property flags the changes that matter), and the REST API offers `POST connected-systems/{id}/import-schema/preview` beside the committing `import-schema` endpoint; see the [REST API reference](../../api/reference/).

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

## Attribute data types

Every discovered attribute is recorded with a JIM data type, shown in the Schema tab's **Type** column. An Attribute Flow requires its source and target to be the same type, so this is what decides which Metaverse Attributes an attribute can be mapped to.

Most of the time the Connected System states its type unambiguously and JIM simply records it. A directory's schema and a SCIM service provider's schema are both definitive, so their attribute types are fixed and cannot be changed.

### When the source cannot say

Two cases leave JIM inferring rather than reading.

A delimited file names no types at all, so the File Connector has always asked you to choose.

A relational database states a type for every column, but not every database distinguishes types the way JIM does. Microsoft SQL Server does: `int` is a whole number, `bigint` a 64-bit whole number, `decimal(9,4)` a fractional figure, and JIM records each accordingly. **Oracle has a single numeric type.** An employee identifier, a large counter and a fractional figure are all `NUMBER`, distinguished only by the precision and scale the column was declared with.

JIM therefore reads that declaration and picks the narrowest type guaranteed to hold every value the column permits:

| Declared | Becomes | Why |
|----------|---------|-----|
| `NUMBER(p,0)`, p up to 9 | Number | The widest such column holds 999,999,999, which fits a 32-bit whole number. |
| `NUMBER(p,0)`, p from 10 to 18 | Long Number | Ten digits already exceed a 32-bit whole number, so the ordinary sequence-backed key lands here. |
| `NUMBER(p,0)`, p of 19 or more | Decimal | Nineteen digits can exceed a 64-bit whole number, so narrowing would risk losing a value. |
| `NUMBER(p,s)` with a scale | Decimal | The column is genuinely fractional. |
| `NUMBER` with no precision | Decimal | The declaration states no width, so JIM assumes the widest. |

`NUMBER(1)` is a whole number unless you switch on **Treat NUMBER(1) Columns as Boolean** on the Connected System, which is opt-in because a single-digit column is just as often a small number as a flag.

### Overriding an inferred type

Where a Connector's schema cannot state a type definitively, the Schema tab shows an **Edit** control on each attribute row. Choose the type the column is actually for and the attribute is recorded with it.

The attribute's **Description** states the source column type it was built from (`Source column type: NUMBER(10).`), so you can see what the inference was based on before deciding whether to disagree with it.

This is how an Oracle `NUMBER(10)` employee identifier is pointed at the built-in `Employee Number` Metaverse Attribute, which is a Number. Use the built-in attributes wherever they fit: a custom attribute created only to work around a type is one no other Connected System will match on.

!!! warning "Set the type before you build on it"
    An override is refused once the attribute is referenced by a Synchronisation Rule or already holds values, because changing it then would reinterpret data that was imported under the previous type. If you need to change it later, remove the references, or clear the Connected System Objects, first.

From PowerShell:

```powershell
Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 5 -AttributeId 10 -Type Integer
```

`Integer` is the friendly name for the Number type, matching `New-JIMMetaverseAttribute`. There is no bulk equivalent: the bulk attribute endpoint refuses a request carrying a data type rather than ignoring it, so a scripted build cannot appear to succeed having changed nothing.

The same field is available on the REST API's attribute update, `PUT api/v1/synchronisation/connected-systems/{connectedSystemId}/object-types/{objectTypeId}/attributes/{attributeId}`.

An override survives a schema refresh. JIM records that the type was chosen rather than inferred, so a refresh restates everything the Connector discovered (writability, plurality, the source column type) and leaves your choice alone. To go back to the inferred type, set it back yourself; a refresh will not do it for you.

!!! note "Existing Connected Systems"
    Improvements to how JIM infers a type apply when a schema is next retrieved. An existing Connected System keeps the types it already holds until you refresh its schema.

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

## Password Synchronisation

Setting a password, above, is something you ask for one account at a time. Password Synchronisation is the standing arrangement: one password change for a person reaching every system they have an account in.

It is configured on the Connected System's **Passwords** tab, which appears only where the connector can set passwords at all. Systems whose connector has no password channel do not show the tab, because there is nothing to configure rather than something to switch on later.

| Setting | What it does |
|---|---|
| **Deliver password changes to this Connected System** | Whether queued password changes are delivered. Separate from the configuration existing, so a system can be set up ahead of a change window and switched on during one. |
| **Object Type holding user accounts** | Which Connected System Object Type receives passwords. Only Object Types you have selected for synchronisation are offered: an unselected one holds no objects, so choosing it would queue passwords for accounts that never appear. |
| **Maximum attempts** | How many delivery attempts JIM makes before it stops and asks you to look. Leave it at 0 to use JIM's default of five. |
| **First retry after** | How long to wait before the first retry. Each further attempt waits twice as long as the one before. |
| **Only send passwords over an encrypted connection** | Whether JIM refuses to transmit rather than warning, where it cannot confirm the connection is encrypted. |

Two things follow from the enable toggle being separate from the configuration:

- **Switching a system off does not discard anything.** Password changes for identities with an account there accumulate, and switching it back on delivers what accumulated. That is what makes it safe to switch off for a maintenance window.
- **There is no way to remove a configuration, only to disable it.** Removing one would throw away everything queued against it, so JIM does not offer that. This is true of the REST API and PowerShell too.

How long a queued change waits before JIM expires it rather than delivering a password that has since been superseded is the Connected System's **initial password time to live**, on the Settings tab. It is shared with initial password provisioning deliberately: the question both are asking is how long this system may be unavailable before JIM stops trying, and the answer is a property of the system rather than of the deployment.

Every change to these settings reaches the Connected System's configuration change history, so switching Password Synchronisation on or off is attributable afterwards.

For automation, `Get-JIMConnectedSystemPasswordSynchronisation` and `Set-JIMConnectedSystemPasswordSynchronisation` do the same over the REST API; `ConnectorSupportsPasswordSet` on the response tells you whether a system can be configured at all.

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
| Destructive | Outbound Deprovision Action, deletion rules, deselecting an Object Type or partition | Red, because applying it can cascade deletions or mass deprovisioning, or leave objects joined and contributing values that never refresh |

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
