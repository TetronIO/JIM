// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Transactional;

namespace JIM.Web.Models.Api;

/// <summary>
/// One node of a Sync Preview's speculative outcome tree: the unpersisted counterpart of a really-recorded
/// Run Profile Execution Item outcome, in the same shape so a caller can render both through the same
/// component.
/// </summary>
public class SyncOutcomeNodeDto
{
    /// <summary>
    /// The type of outcome (for example Projected, AttributeFlow, PendingExportCreated); the same enum a
    /// really-recorded outcome carries.
    /// </summary>
    public ActivityRunProfileExecutionItemSyncOutcomeType OutcomeType { get; set; }

    /// <summary>
    /// Target entity context: the Metaverse Object id, target Connected System Object id, or Connected
    /// System id relevant to this outcome.
    /// </summary>
    public Guid? TargetEntityId { get; set; }

    /// <summary>
    /// Snapshot description for display without further lookups (for example the Connected System name).
    /// </summary>
    public string? TargetEntityDescription { get; set; }

    /// <summary>
    /// The id of the Synchronisation Rule attributed to this outcome, when one was determinable.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// Snapshot of the attributed Synchronisation Rule's name.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// The kind of change a staged Pending Export would represent, when this outcome is an export.
    /// </summary>
    public PendingExportChangeType? StagedChangeType { get; set; }

    /// <summary>
    /// Quantitative detail (for example "12 attributes flowed").
    /// </summary>
    public int? DetailCount { get; set; }

    /// <summary>
    /// Optional context message providing additional detail about the outcome.
    /// </summary>
    public string? DetailMessage { get; set; }

    /// <summary>
    /// Ordering among siblings, for consistent display order.
    /// </summary>
    public int Ordinal { get; set; }

    /// <summary>
    /// Child outcomes, in display order.
    /// </summary>
    public List<SyncOutcomeNodeDto> Children { get; set; } = [];

    public static SyncOutcomeNodeDto FromModel(SyncOutcomeNode model) => new()
    {
        OutcomeType = model.OutcomeType,
        TargetEntityId = model.TargetEntityId,
        TargetEntityDescription = model.TargetEntityDescription,
        SyncRuleId = model.SyncRuleId,
        SyncRuleName = model.SyncRuleName,
        StagedChangeType = model.StagedChangeType,
        DetailCount = model.DetailCount,
        DetailMessage = model.DetailMessage,
        Ordinal = model.Ordinal,
        Children = [.. model.Children.Select(FromModel)]
    };
}
