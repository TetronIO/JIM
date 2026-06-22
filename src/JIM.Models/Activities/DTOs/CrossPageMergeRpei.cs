// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// Pairs an already-persisted RPEI (with its SyncOutcomes) against the id of its existing
/// <see cref="Staging.MetaverseObjectChange"/>, when one exists. Returned by cross-page
/// reference resolution to merge new reference Attribute Flow into the existing RPEI/MvoChange
/// pair rather than creating duplicates.
/// </summary>
public class CrossPageMergeRpei
{
    /// <summary>
    /// The persisted RPEI, materialised with its <see cref="ActivityRunProfileExecutionItem.SyncOutcomes"/>
    /// list populated.
    /// </summary>
    public ActivityRunProfileExecutionItem Rpei { get; set; } = null!;

    /// <summary>
    /// Id of the <see cref="Staging.MetaverseObjectChange"/> already persisted for this RPEI,
    /// or <c>null</c> if no MvoChange has been written yet (e.g. an AttributeFlow-only RPEI from a
    /// prior page with no scalar attribute changes on the parent).
    /// </summary>
    public Guid? ExistingMvoChangeId { get; set; }
}
