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
- [Connected Systems](connected-systems.md#configuration-changes-pending-a-full-synchronisation) -- the changed-since indicator
- [Service Settings](service-settings.md) -- switching configuration change tracking on or off
