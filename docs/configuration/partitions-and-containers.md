---
title: Partitions and Containers
description: "Choose which partitions and containers of a Connected System JIM manages, and what a change to that scope does."
type: object
audience: administrator
---

# Partitions and Containers

Partitions and containers are chosen on the Connected System's **Scope** tab, which appears only where the Connector has them: the tab is where you choose what JIM manages in the system, whatever shape that takes.

```jim-diagram
archetype: anatomy
title: A partition and a container in one Distinguished Name
desc: "In OU=Users,DC=company,DC=local, DC=company,DC=local is the partition, a naming context the directory defines, and OU=Users is a container within it, an organisational unit; containers can be nested arbitrarily deep. Partitions exist on LDAP-style Connectors only; containers on most Connectors that expose hierarchy. What you select is what JIM manages, for import and export alike, and deselecting a partition or container makes its objects obsolete at the next Full Import."
subject:
  kind: value
  parts:
    - { text: "OU=Users,", callout: "Container: an OU within it" }
    - { text: "DC=company,DC=local", strong: true, callout: "Partition: a naming context" }
properties:
  - { name: Partitions, value: LDAP-style Connectors only }
  - { name: Containers, value: nested arbitrarily deep }
  - { name: Selected, value: managed for import and export }
  - { name: Deselected, value: its objects obsoleted by a Full Import }
```

A **partition** is a top-level logical division of a Connector Space that mirrors a boundary defined by the external system. Partitions exist in JIM primarily to service LDAP-style directories and their naming contexts (NCs): the discrete directory trees that an LDAP server hosts. The separate domain partitions within an Active Directory forest, or the distinct naming contexts exposed by an OpenLDAP server, each surface as a partition in JIM.

Most Connected Systems do not support partitions. A flat file, a SQL table, or a SCIM endpoint has no concept of multiple naming contexts, so its Connector Space has no partitions.

Inside a partition, or directly inside the Connector Space of a connector that does not support partitions, you can have **containers**. Containers are a separate, lower-order logical construct that sits beneath partitions; they exist mainly to support LDAP organisational units (OUs) and similar hierarchical groupings. Containers can be nested arbitrarily deep, and JIM loads the full hierarchy so administrators can select nested containers (for example `OU=Contractors,OU=Users,DC=company,DC=local`) for import or export.

!!! note "Partitions and OUs are different concepts"
    Partitions and organisational units (OUs) are distinct. A partition is a top-level boundary on the external system; an OU is a sub-tree within a partition and is modelled in JIM as a container.

| Construct | Scope | Example | Available on |
|-----------|-------|---------|--------------|
| **Partition** | Top-level boundary defined by the external system; discovered, not invented, by JIM | An Active Directory domain naming context (`DC=company,DC=local`) | LDAP-style connectors only |
| **Container** | Sub-tree within a partition, or within the Connector Space of a non-partitioned system | An OU (`OU=Users,DC=company,DC=local`) | Most connectors that expose hierarchy |

In practice, selecting a partition brings an entire naming context into scope, while selecting containers narrows what is imported within that partition (or within the Connector Space for connectors that have no partitions).

## :material-chart-box-outline: How many objects each container holds

Each container row shows how many objects it holds, so you can tell a container worth managing from an empty one before you tick anything.

The figure is read from the Connected System itself, not from what JIM has already imported, so it is there the first time you open the tab on a brand new Connected System. That is the moment it matters most: you are deciding what to manage, and JIM holds nothing yet.

- **The figure follows the container's [Container Scope](../connectors/jim-ldap-connector-topology.md#container-scope).** A container set to This and below reports what its whole branch holds; one narrowed to This level reports only what sits directly in it. Hover the number to see both, and which of them is on screen.
- **Only the Object Types you have selected are counted**, so the number matches what a Full Import would actually bring back. Select them on the Schema tab first; nothing is counted until you have.
- **Zero and blank mean different things.** Zero is a container that was searched and found empty. A blank means nobody has counted it: either the Connector cannot report counts, the hierarchy has not been retrieved since this feature shipped, or counting was cut short, in which case the hierarchy refresh's Activity says so and why.
- **Selections and exclusions are ignored.** The figure says what is in the container, not what JIM would import from it once your exclusions apply. [Preview Changes](#previewing-a-partition-or-container-change) answers that second question.

Counts are gathered as part of **Retrieve Hierarchy**, so refreshing the hierarchy refreshes the numbers, and the tab tells you when it last ran. Counting is bounded: if it takes longer than a minute, or the directory stops the search at its own size or time limit, JIM discards that partition's figures rather than showing numbers that are quietly short of the truth, and the refresh's Activity completes with a warning naming the partition and what stopped the count. Raising the directory's limit, or narrowing the selected Object Types, is the usual fix. The hierarchy itself still arrives either way.

!!! note "This reads your directory"
    Counting means retrieving the matching entries, because LDAP has no count operation. JIM asks for names only, which is far lighter than an import, and runs one search per partition rather than one per container. It is still a read against your production directory, so it happens when you retrieve the hierarchy and at no other time.

## :material-gesture-tap-hold: What your selections mean

Selection is how you tell JIM which parts of a system it manages, and it binds everywhere:

- A [Run Profile](run-profiles.md) that targets a deselected partition is refused rather than run. The Run Profiles tab marks it, and the property is available over REST and PowerShell so you can find every affected Run Profile at once.
- Exports are refused outside the selected containers, honouring each container's [Container Scope](../connectors/jim-ldap-connector-topology.md#container-scope). Selection means the scope JIM manages, not merely the scope it reads: writing an object where JIM cannot import it back leaves the change unconfirmed and the object treated as deleted on the next Full Import, so JIM would end up churning an object it had just exported. A container set to One Level is not a licence to write anywhere beneath it, only directly within it, because that is exactly what the next import will return. The export fails for that object, naming the Distinguished Name, and the rest of the run continues. A container created by the Connector during the run is in scope, because JIM selects it as soon as the run ends.
- Objects in a deselected partition or container fall out of import scope. A Full Import treats anything it does not find as deleted from the system, so narrowing scope makes the corresponding Connected System Objects obsolete and, on the next synchronisation, disconnects them and recalls the attribute values they contributed. Widen scope again before running a Full Import if that is not what you intended.
- Deselecting a partition also clears its Delta Import watermark. Once you select it again, its next Delta Import reads every object in it, as a Full Import does, so its objects come back whether or not they changed while it was out of scope, and records a new watermark. A Delta Import that also reads other partitions reads every object in all of them on that run.

## :material-map-marker-outline: Which partition an object is in

JIM records each Connected System Object in the partition it is in, however JIM first came to hold it: imported by a Run Profile that targets no partition, or one that targets another partition, or provisioned by JIM. The Connector says where the object is. The LDAP Connector goes by the object's distinguished name, which ends with the naming context of the partition it is in; where naming contexts are nested (one directory tree hosted beneath another), JIM records the deepest selected partition that holds the object.

```jim-diagram
archetype: decision
title: Which partition JIM records for a Connected System Object
desc: "Whenever an import reads an object, or an export creates or renames one, JIM asks the Connector which partition the object is in. Where a selected partition holds it, JIM records the deepest selected partition that does. Where only a deselected partition holds it, JIM records that partition. Where no partition holds it, JIM keeps the partition it already holds for the object, and a new object takes the partition its Run Profile targets, or none."
start: { label: Connected System Object }
questions:
  - { id: selected, label: "In a selected partition?", sub: by its distinguished name }
  - { id: other, label: "In a deselected partition?" }
outcomes:
  - { id: deepest, label: Recorded in the deepest one, good: true }
  - { id: deselected, label: Recorded in that partition }
  - { id: kept, label: Kept as it was, sub: "a new object takes its Run Profile's" }
answers:
  - { from: selected, to: deepest, label: "yes" }
  - { from: selected, to: other, label: "no" }
  - { from: other, to: deselected, label: "yes" }
  - { from: other, to: kept, label: "no" }
```

A Run Profile that targets a partition therefore covers every object in it:

- An import of the partition that reads every object (a Full Import, or a Delta Import with no watermark to read from) marks as deleted any object in it that the Connected System no longer returns.
- The Run Profile's [deletion limits](run-profiles.md#full-import) apply to every object in the partition, so **Max detected deletions percent** is a share of all of them.
- A create JIM exported that such an import does not find is [sent again](connected-systems.md#pending-exports) at the next export run.
- `Get-JIMConnectedSystemObject -Count -PartitionId` counts every object in the partition.

Every import that reads an object records its partition again, so an object moved to another partition is recorded there once an import reads it. An export that creates or renames an object records the partition it wrote to, before any import confirms it.

An object in none of the Connected System's partitions keeps the partition JIM already holds for it; a new one takes the partition its Run Profile targets, or none for a Run Profile that targets no partition. After an import that read any such object, the Worker logs a warning counting them. It usually means a partition is missing from the Connected System: run **Retrieve Hierarchy**, and the next import records each object in the partition it is in.

## :material-history: Where each partition's next Delta Import starts

Each partition keeps its own Delta Import watermark: the point its next Delta Import reads changes after, recorded by the last import that read it. The Scope tab shows it under each selected partition, so you can tell before a run whether that partition's next Delta Import reads its changes or every object in it:

- **Next Delta Import reads changes after** *position* **· Recorded** *time ago*<br /> JIM holds a watermark for the partition. The position is in the directory's own terms (an Update Sequence Number, an access log time or a change log entry number; [Import Watermarks](import-watermarks.md) lists them), and the time is when an import last recorded it, which it does even when nothing has changed, so it also says when the partition was last read.
- **No watermark recorded. The next Delta Import reads every object in this partition, as a Full Import does, and records one.**<br /> The partition has never been imported, or its watermark was cleared by deselecting the partition or by [clearing the Connector Space](connector-space.md#clearing-the-connector-space).

Where the recorded watermark cannot be read, the line says so, and the next Delta Import reads every object in the partition and records a new one. A deselected partition shows none of these. On OpenLDAP and 389 Directory Server, expect the next Delta Import of a partition whose watermark was recorded longer ago than the accesslog's purge age, or the changelog's maximum age, to be refused, asking for a Full Import that reads the partition ([LDAP Connector Delta Import](../connectors/jim-ldap-connector-delta-import.md) explains why); [Service Account Permissions](../connectors/jim-ldap-connector-permissions.md) says how long to keep the log.

Automation reads the same: [`Get-JIMConnectedSystemPartition`](../powershell/partitions-and-containers.md#get-jimconnectedsystempartition) and `GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/partitions` return each partition's `deltaImportWatermark`, with its `value` and when it was recorded (`recordedAt`, in UTC), or empty when none is recorded.

## :material-text-box-edit-outline: Stating Container Scope as text (Advanced Mode)

The Scope tab offers two ways to edit the same Container Scope, switched with **Simple** and **Advanced**:

- **Simple** is the tree: tick the Containers you manage, and set each one's [Container Scope](../connectors/jim-ldap-connector-topology.md#container-scope).
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

## :material-eye-outline: Previewing a partition or container change

Because narrowing scope is silently destructive, the Scope tab offers a **Preview Changes** button beside **Save Changes**. It answers what your edited selection would do, without saving it.

The preview reports:

| Transition | What it means |
|---|---|
| Leaves import scope | Connected System Objects that leave import scope and are not joined to anything. Nothing in the Metaverse changes as a result. |
| Disconnects from its Metaverse Object | Objects that leave import scope and *are* joined. Each takes the attribute values it contributed out of the Metaverse Object with it. |
| Becomes eligible for deletion | Metaverse Objects that those disconnections would leave satisfying their [deletion rule](metaverse.md#deletion-behaviour). These are deletions your selection would set in motion. |
| Enters import scope | Objects JIM still holds from scope you are re-selecting. |

The counts honour each container's [Container Scope](../connectors/jim-ldap-connector-topology.md#container-scope): beneath a One Level container an import returns nothing, so objects a level deeper are already out of scope and deselecting it takes nothing further away.

Two limits are worth knowing, and the preview states both where they apply:

- **Objects JIM has never imported cannot be counted.** Selecting new scope makes the next Full Import discover objects that are not in the Connector Space yet, and there is nothing to count until it runs.
- **Some objects cannot be placed.** An object JIM holds in no partition (one no import has read since before JIM recorded partitions, or one [outside them all](#which-partition-an-object-is-in) first imported by a Run Profile that targets no partition), or one whose Connector cannot say what container an object is in, is left out of the counts entirely rather than guessed at in either direction.

Save after previewing and the confirmation opens with the preview's own sentence, alongside the properties changing, and the change's [Activity](activities.md) records which preview informed it. Edit the selection after previewing and the preview is marked stale and contributes nothing, because it now describes a different change.

The same evaluation is available to automation: [`New-JIMConfigurationChangePreview -ConnectedSystemId`](../powershell/previews.md) in PowerShell, or `POST connected-systems/{id}/scope-selection/preview` in the [REST API](../../api/reference/). Send the whole proposed selection rather than one flag: what a deselection costs depends on the rest of the selection, because an object leaves scope only when nothing else still covers it.

See [Configuration changes](configuration-changes.md#previewing-a-change-before-you-make-it) for how previews work generally.

## :material-swap-horizontal: Renames and moves in the source system

JIM identifies partitions and containers by the system's own immutable identifier where one exists (`objectGUID` on Active Directory, `entryUUID` on OpenLDAP), not by their Distinguished Name. Renaming an organisational unit, or moving one to a different parent or partition, therefore keeps your selection intact, even when it moves into a partition or beneath a container that the same refresh discovers: the next hierarchy refresh reports it as a rename or a move rather than as one container disappearing and another appearing. A move between partitions names both partitions. A container is only in scope while its partition is selected, so a selected container that moves into a partition that is not selected selects that partition, and the refresh reports each partition it selected this way with the containers that moved into it.

Containers selected before this behaviour shipped record their identifier at their next hierarchy refresh, and continue to be matched on Distinguished Name until then. Refresh the hierarchy once after upgrading to pick it up.

A container that genuinely disappears from the source system is still reported as removed, and the Scope tab warns when a removed container was one you had selected.

## :material-play-circle-outline: When a refresh removes a partition a Run Profile targets

**Retrieve Hierarchy** removes the partitions and containers the Connected System no longer has. A partition a [Run Profile](run-profiles.md) targets is the exception: removing it would leave the Run Profile reading every selected partition, so a Full Import scoped to one partition would look for every object in the Connected System and treat any it did not find as deleted. JIM saves nothing from such a refresh until you choose:

- **Disable Run Profiles** applies the refresh and disables each Run Profile that targets a removed partition, recording the partition's name. Each keeps its other settings, and stays disabled until you [enable it again](run-profiles.md#disable-a-run-profile) and choose the partition it reads, or all selected partitions. The refresh's Activity completes with a warning naming each Run Profile it disabled.
- **Cancel** applies nothing from the refresh: no partition or container is added, removed or renamed, and every Run Profile is left as it was. The refresh's Activity completes with a warning saying so, naming the Run Profiles. Point them at another partition, then retrieve the hierarchy again.

The portal asks, listing each Run Profile and the partition it targets. `Import-JIMConnectedSystemHierarchy` disables them unless you pass `-RunProfileAction Cancel`, and warns about each one it disabled. Over the REST API, `POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/import-hierarchy` takes `runProfileAction` (`Disable`, the default, or `Cancel`); a cancelled refresh answers `409 Conflict`, naming the Run Profiles.
