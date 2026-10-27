---
title: Removing a Connected System
description: "What deleting a Connected System does to the objects and values it managed, and how to preview the impact first."
type: object
audience: administrator
---

# Removing a Connected System

Deleting a Connected System asks what should happen to everything it managed, with the impact quantified in the delete dialog before you choose:

- **Deprovision through synchronisation** (the default on every surface): each of the system's objects is processed as though it had disconnected through a normal synchronisation. Contributed values are recalled or handed to surviving contributors per [Attribute Priority](../concepts/attribute-priority.md), except for an Object Type with [Remove Contributed Attributes On Obsoletion](connected-system-schema.md#obsoletion-and-contributed-values) switched off, whose values stay on the Metaverse Object exactly as a normal disconnection would leave them. Each Metaverse Object's Deletion Rule is evaluated as after any disconnection, so an object is scheduled for deletion when this system was its last connector, or an authoritative source its Object Type's rule names (see [Authoritative source trigger modes](metaverse.md#authoritative-source-trigger-modes)); downstream systems receive corrective exports; an object whose recalled values take it out of an export Synchronisation Rule's scope has its account in that system deprovisioned according to the rule's [Deprovisioning Action](deprovisioning.md). The run is queued and Activity-tracked; monitor it from the Operations page via the link the dialog surfaces, and the system is deleted as the run's final step. While it runs, the system is fenced: excluded from scheduled runs and read-only.
- **Delete immediately and keep contributed data**: the system is removed at once. Values it contributed remain in place with no record of where they came from and nothing will ever recall them; Deletion Rules are evaluated in bulk, on the same terms, but no per-object synchronisation processing occurs and downstream systems are not corrected. The dialog's warning states this before you confirm. Small systems are removed synchronously; larger systems, or a system with a running sync, are queued as a background activity.

If a deprovisioning run stops before completing, the system stays fenced and consistent: deleting it again with deprovisioning resumes from where the run stopped, and deleting immediately finishes the removal at once (keeping whatever contributed data remained). A half-deprovisioned system never returns to service.

Before deleting a system that other systems depend on, [preview the deletion](#previewing-a-deletion): the counts in the delete dialog say how much the system holds, while the preview says what deleting it would do to everything else.

Deleting a Connected System records a final snapshot of its configuration in the [Configuration Change History](activities.md#configuration-change-history), so a decommissioned system's last-known state, and who removed it, remain auditable after it is gone. You can attach an optional reason in the admin portal delete dialog, with `Remove-JIMConnectedSystem -ChangeReason`, or via the REST API. As with all such snapshots, connector secrets are recorded as changed but never stored.

## :material-eye-outline: Previewing a deletion

Deprovisioning through synchronisation reaches well beyond the system being deleted: another system takes over the values it also contributes, values nothing else contributes are cleared, people whose Deletion Rule it triggers become eligible for deletion, and every downstream system is corrected to match. A **deletion impact preview** works all of that out against the objects as they stand, changing nothing, so you can read it before you commit.

Open the Connected System's **Danger Zone** tab and choose **Preview deletion impact**. The preview runs in the background like any other [Configuration Change Preview](configuration-changes.md#previewing-a-change-before-you-make-it); you can leave the page and come back to it, and the tab picks up the latest one rather than starting another. It leads with the worst consequence and breaks the rest down beneath:

| Consequence | What it means |
|---|---|
| Becomes eligible for deletion | A Metaverse Object its Object Type's Deletion Rule schedules for deletion once this system goes: under **When Last Connector Disconnected**, one whose last connector this is; under **When Authoritative Source Disconnected**, one this system is a selected source for, and in **All sources disconnect** mode the last selected source it still holds. Its grace period then applies, as after any disconnection. |
| Value cleared | A Metaverse attribute value this system contributed that no other Connected System contributes, so nothing replaces it. |
| New contributor, value changes | Another Connected System also contributes the attribute, so its value takes over, following [Attribute Priority](../concepts/attribute-priority.md). Each row names the system the new value would come from. |
| New contributor, same value | As above, but the value is identical, so only its source changes and nothing is exported. These are counted but never lead the summary. |
| Updated in the target system | A correction staged for a downstream Connected System because a value it holds would change or be cleared. |
| Pending Export changes withdrawn | A change already queued for a downstream Connected System, not yet exported, that would be withdrawn because the account already holds the value the deletion leaves the Metaverse Object with. The account keeps its value, and anything else queued for it still exports. Its values read **Queued**, the change that was waiting, and **Target keeps**, the value the account already holds. |
| Removed from the target system | An account a downstream system would delete, because the identity it belongs to is deleted or leaves that system's export scope, and the [Deprovisioning Action](deprovisioning.md) there is Delete. |
| Disconnects from its Metaverse Object | The same two situations where the Deprovisioning Action is Disconnect: the account stays in the downstream system and JIM stops managing it. |
| Provisioning cancelled | An account JIM was about to create in a downstream system, but has not yet exported, that would no longer be created. |

The preview describes **deprovisioning through synchronisation** only. Deleting immediately keeps every contributed value and sends no exports, so there is nothing for it to evaluate beyond the deletion rules, and the delete dialog says so instead of repeating the preview's figures.

The delete dialog shows the latest preview beside the deletion choices: the first few lines of its summary when it is current, its progress while it is still running, and a prompt to preview first when there is none. A preview goes **stale** when anything that could change its answer happens after it ran. That is either data moving (a Run Profile running, housekeeping deleting Metaverse Objects, a Connector Space being cleared, or another Connected System being deleted) or a configuration change that affects synchronisation. A stale preview says what overtook it and offers to run again.

The deletion's [Activity](activities.md) records whether a preview informed it. A finished preview is recorded, and the Activity shows it as "Informed by a preview run" with its summary lines and a link to the full preview. If the preview was stale when you deleted, the Activity also says it was out of date and what overtook it, so the audit trail neither claims more than the preview could show nor says you went ahead without looking. A deletion with no preview, or with one still running or one that did not finish, is recorded as "Went ahead without a preview".

Where a [derived Attribute Flow](derived-attributes.md) reads a Metaverse attribute this deletion clears or changes, the preview carries a warning naming the attribute and the systems hosting those flows. The preview shows the change to the attribute itself, not the derived values that follow from it: the deletion marks the objects affected, and those flows recompute at the next synchronisation of the system hosting them.

Automation gets the same evaluation: [`New-JIMConfigurationChangePreview -ConnectedSystemId <id> -Deletion`](../powershell/previews.md#new-jimconfigurationchangepreview) in PowerShell, or `POST connected-systems/{id}/deletion/preview` in the [REST API](../../api/reference/). Pass the preview's Activity id to [`Remove-JIMConnectedSystem -PreviewActivityId`](../powershell/connected-systems.md#remove-jimconnectedsystem), or as `previewActivityId` on the REST deletion, to record it; JIM refuses a deletion citing anything other than a deletion preview of that same system. A deletion citing a stale preview goes ahead, records that the preview was out of date, and returns a warning saying what overtook it.
