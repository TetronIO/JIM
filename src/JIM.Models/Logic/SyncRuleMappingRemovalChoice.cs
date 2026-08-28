// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// Carries one staged Attribute Flow mapping removal's recall-or-keep choice through a whole-rule save
/// (#1537). The portal's Attribute Flow editor removes mappings from the rule in memory and persists the
/// removals when the rule is saved, so the deletion-time choice made in the editor's dialog must travel with
/// the save: each named mapping is deleted properly (its row and sources removed), and where keep was chosen
/// its contributed Metaverse attribute values have their Synchronisation Rule provenance severed BEFORE the
/// row deletion, permanently exempting them from the orphan recall. A staged removal not named here behaves
/// as the default (recall).
/// </summary>
public class SyncRuleMappingRemovalChoice
{
    /// <summary>
    /// The id of the mapping being removed. It must be absent from the rule's
    /// <see cref="SyncRule.AttributeFlowRules"/> at save time (the removal has been staged) and must still
    /// exist in the database under the rule; anything else is refused as a caller defect.
    /// </summary>
    public int MappingId { get; set; }

    /// <summary>
    /// True to keep the values the mapping contributed: their provenance is severed so the next Full
    /// Synchronisation does not recall them, and the choice is recorded on the save's Activity. False (the
    /// default everywhere) leaves provenance intact, so the shipped orphan recall withdraws the values at the
    /// next Full Synchronisation of the contributing system.
    /// </summary>
    public bool KeepContributedValues { get; set; }

    /// <summary>
    /// The target Metaverse Attribute of the removed mapping, recorded by the portal at choice time so the
    /// Attribute Flow preview can state each removal's chosen behaviour (the staged rule no longer holds the
    /// mapping to read it from). Advisory and display-side only: the save resolves the authoritative target
    /// from the persisted mapping and never trusts this value.
    /// </summary>
    public int? TargetMetaverseAttributeId { get; set; }
}
