// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic.DTOs;

/// <summary>
/// The outcome of an Attribute Flow mapping deletion (#1537): the scale of the mapping's contributed values
/// at decision time and whether they were kept, so callers (REST responses, PowerShell output, portal
/// messages) can describe what happened without a second query. Mapping deletion never queues work: the
/// default recall is the shipped deferred mechanism (the next Full Synchronisation of the contributing
/// system), and keep severs the values' provenance synchronously before the row is deleted.
/// </summary>
public class SyncRuleMappingDeletionResult
{
    /// <summary>
    /// How many Metaverse attribute values the mapping's (Synchronisation Rule, target attribute) pair had
    /// contributed at decision time. Zero for export mappings and mappings with no target Metaverse attribute,
    /// which have nothing to recall or keep.
    /// </summary>
    public int AffectedValueCount { get; set; }

    /// <summary>
    /// How many distinct Metaverse Objects held at least one of those values at decision time.
    /// </summary>
    public int AffectedObjectCount { get; set; }

    /// <summary>
    /// True when keep was chosen and values were present, so their Synchronisation Rule provenance was severed
    /// before the row deletion; false when the default (recall) applied or there was nothing to keep.
    /// </summary>
    public bool ContributedValuesKept { get; set; }
}
