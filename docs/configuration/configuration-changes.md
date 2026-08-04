# Configuration changes

Some configuration edits are harmless. Others decide whether accounts are deleted. The two sit side by side on the same pages, so JIM judges every save by the properties that actually changed, and reacts only where it matters.

## How JIM classifies a change

Every configuration property carries one of three classes:

| Class | What it means | What JIM does when you save |
|---|---|---|
| Cosmetic | Labels and display: a name, a description, an icon, a page size. | Nothing. The save goes straight through. |
| Sync-affecting | Changes what synchronisation does: scope, mappings, matching, connector settings, enabling or disabling a rule. | Asks you to confirm, listing exactly what is changing and from which value to which. |
| Destructive | Decides whether objects are removed: Deprovisioning Actions, Object Type, partition and container selection, deletion rules. | As above, and additionally states in plain terms what the change will do. |

The class is worked out from the properties you actually changed, not from the page you changed them on. Renaming a Synchronisation Rule saves in silence even though the Deprovisioning Action sits on the same form; changing both in one save is treated as destructive.

## Confirming a change before you save it

Where a change is sync-affecting or destructive, JIM shows a confirmation before saving. It lists what is changing, leads with the most consequential change, and names the object being changed. Where a destructive property is involved it also says what that property governs and what your particular change will do; switching a Deprovisioning Action *to* Delete and switching it *back* are both confirmed, but they are described differently, because they are opposite consequences.

Cancelling the confirmation abandons the save. Nothing is written.

!!! note "Saving is not applying"

    A saved configuration change does not reach existing objects until synchronisation runs again, so the confirmation ends by recommending a Full Synchronisation. The Connected System pages show a [changed-since indicator](connected-systems.md#configuration-changes-pending-a-full-synchronisation) so you can see at a glance which systems have configuration waiting for one.

    Metaverse Object Type deletion settings are the exception: they take effect immediately. Objects that already satisfy a new deletion rule become eligible for deletion on the next synchronisation or housekeeping pass, with no further change made to them. The confirmation says so.

## Previewing a change before you make it

A confirmation tells you *what* you are changing. A **Configuration Change Preview** tells you what it would *do*: JIM evaluates the proposed change against the objects already in the metaverse and reports which of them would be affected, changing nothing.

Previews are available where a surface has an evaluator for it. The first is a Metaverse Object Type's [deletion settings](metaverse.md#previewing-a-deletion-settings-change), which is the change most worth asking about, because it is the one that can make existing objects eligible for deletion the moment it is saved.

A preview answers in stages, and each appears as it completes:

| Stage | What it tells you |
|---|---|
| Validation | Whether the proposal can be applied at all. A blocking finding stops the preview; nothing further is evaluated, because counting the objects a change would affect is a statement about a change that will happen. |
| Objects affected | How many objects would move through each transition. Exact counts over the whole population. |
| Summary | The counts broken down by transition, object type, and the attribute and values involved. |
| Object detail | The individual objects behind each summary row. |

Three things are worth knowing before you act on one:

- **A preview that failed shows nothing.** A part-way evaluation has seen an arbitrary subset of the objects, so its counts are real numbers about the wrong population. JIM withholds them rather than presenting them with a caveat beside them.
- **Object detail may be a sample.** Each summary row keeps a capped number of detail rows by default; the row's own count is always exact. Where the cap applied, the drill-down is labelled as a sample, and you can ask for the full set when you start the preview.
- **A preview describes the data as it stood.** An import that runs afterwards can move the answer. The panel says when the preview was evaluated so you can judge whether that matters.

In the portal, previewing leaves the change unsaved: read the result, then save (or not). If you save, the confirmation states the preview's counts alongside the properties changing, and the change's [Activity](activities.md) records which preview informed it. Edit the settings after previewing and the preview is marked stale and contributes nothing to the confirmation, because it now describes a different change.

Automation gets the same evaluation. Start a preview with [`New-JIMConfigurationChangePreview`](../powershell/previews.md), or `POST` to the surface's own preview endpoint in the [REST API](../../api/reference/), then pass the preview's Activity id back on the change itself so the audit records the link.

## Where the confirmation appears

| Surface | Changes that can trigger it |
|---|---|
| [Synchronisation Rules](synchronisation-rules.md) | Direction, enabling, provisioning and projection, scope, Attribute Flow, Object Matching Rules; **destructive:** Deprovisioning Action, Inbound Out-of-Scope Action |
| [Connected Systems](connected-systems.md) (Details, Settings, Schema, Partitions & Containers) | Connector settings, matching mode, unresolved reference handling, attribute selection, selecting a container; **destructive:** deselecting an Object Type, a partition or a container |
| [Metaverse Object Types](metaverse.md#deletion-behaviour) | Attribute bindings; **destructive:** deletion rule, grace period, deletion trigger systems. The Deprovisioning Action dropdown on this page edits Synchronisation Rules and is confirmed the same way. |
| [Metaverse Attributes](metaverse.md#attributes) | Data type, plurality, Object Type bindings |
| [Service Settings](service-settings.md) | The few settings that steer synchronisation; nearly all Service Settings are operational and save in silence |

## When JIM stays silent

The confirmation appears only when JIM can tell what changed, which means [configuration change tracking](service-settings.md) must be switched on. With tracking off there is no recorded baseline to compare against, so JIM says nothing rather than guessing. Creating a new object is never confirmed either: there is no prior state, so nothing existing is at risk.

Changes made through the REST API and PowerShell are not prompted. An automated call names the property it is setting, so consent is already explicit; the change is classified and recorded in the configuration change history exactly as a portal change is.

## See also

- [Activities](activities.md) -- every configuration change is recorded as an Activity, with a versioned before-and-after snapshot
- [Preview cmdlets](../powershell/previews.md) -- starting, reading and cancelling a preview from PowerShell
- [Metaverse](metaverse.md#previewing-a-deletion-settings-change) -- previewing a change to deletion settings
- [Connected Systems](connected-systems.md#configuration-changes-pending-a-full-synchronisation) -- the changed-since indicator
- [Service Settings](service-settings.md) -- switching configuration change tracking on or off
