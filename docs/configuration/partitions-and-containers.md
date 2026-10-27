---
title: Partitions and Containers
description: "Choose which partitions and containers of a Connected System JIM manages, and what a change to that scope does."
type: object
audience: administrator
---

# Partitions and Containers

Partitions and containers are chosen on the Connected System's **Scope** tab, which appears only where the Connector has them: the tab is where you choose what JIM manages in the system, whatever shape that takes.

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
- **Some objects cannot be placed.** An object imported before JIM recorded partitions, or one whose Connector cannot say what container an object is in, is left out of the counts entirely rather than guessed at in either direction.

Save after previewing and the confirmation opens with the preview's own sentence, alongside the properties changing, and the change's [Activity](activities.md) records which preview informed it. Edit the selection after previewing and the preview is marked stale and contributes nothing, because it now describes a different change.

The same evaluation is available to automation: [`New-JIMConfigurationChangePreview -ConnectedSystemId`](../powershell/previews.md) in PowerShell, or `POST connected-systems/{id}/scope-selection/preview` in the [REST API](../../api/reference/). Send the whole proposed selection rather than one flag: what a deselection costs depends on the rest of the selection, because an object leaves scope only when nothing else still covers it.

See [Configuration changes](configuration-changes.md#previewing-a-change-before-you-make-it) for how previews work generally.

## :material-swap-horizontal: Renames and moves in the source system

JIM identifies partitions and containers by the system's own immutable identifier where one exists (`objectGUID` on Active Directory, `entryUUID` on OpenLDAP), not by their Distinguished Name. Renaming an organisational unit, or moving one to a different parent, therefore keeps your selection intact: the next hierarchy refresh reports it as a rename or a move rather than as one container disappearing and another appearing.

Containers selected before this behaviour shipped record their identifier at their next hierarchy refresh, and continue to be matched on Distinguished Name until then. Refresh the hierarchy once after upgrading to pick it up.

A container that genuinely disappears from the source system is still reported as removed, and the Scope tab warns when a removed container was one you had selected.
