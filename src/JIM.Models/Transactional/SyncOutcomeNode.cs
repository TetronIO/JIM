// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Transactional;

/// <summary>
/// One node of a speculative sync outcome tree (#288, PRD decision D4): the unpersisted counterpart of
/// <see cref="ActivityRunProfileExecutionItemSyncOutcome"/>, carrying exactly the display fields the real
/// tree renders from and nothing persistence-shaped (no keys, no parent pointer, no entity references), so a
/// preview result serialises for API return without EF navigation cycles (PRD requirement 3).
/// </summary>
/// <remarks>
/// <see cref="FromSyncOutcome"/> is the one shared mapping between a really-recorded outcome and this shape:
/// the preview builds its tree as these nodes directly, the display path can map a real Run Profile Execution
/// Item's outcomes through it, and the fidelity paired test (PRD requirement 9) diffs the two, so preview and
/// reality cannot drift apart silently.
/// </remarks>
public class SyncOutcomeNode
{
    /// <summary>
    /// The type of outcome (for example Projected, AttributeFlow, PendingExportCreated); the same enum the
    /// real tree records, so the two render through the same component.
    /// </summary>
    public ActivityRunProfileExecutionItemSyncOutcomeType OutcomeType { get; set; }

    /// <summary>
    /// Target entity context: the MVO id, target CSO id, or Connected System id relevant to this outcome.
    /// </summary>
    public Guid? TargetEntityId { get; set; }

    /// <summary>
    /// Snapshot description for display without joins (for example the Connected System name).
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
    /// Child outcomes, in display order. Children only; a node carries no parent pointer, so the tree is
    /// acyclic by construction and serialises with default settings.
    /// </summary>
    public List<SyncOutcomeNode> Children { get; set; } = [];

    /// <summary>
    /// Maps a really-recorded outcome (and its descendants, siblings ordered by
    /// <see cref="ActivityRunProfileExecutionItemSyncOutcome.Ordinal"/>) into the speculative node shape.
    /// The one shared mapping between preview and reality; see the class remarks.
    /// </summary>
    /// <param name="outcome">The recorded outcome to map, with its Children loaded.</param>
    public static SyncOutcomeNode FromSyncOutcome(ActivityRunProfileExecutionItemSyncOutcome outcome) => new()
    {
        OutcomeType = outcome.OutcomeType,
        TargetEntityId = outcome.TargetEntityId,
        TargetEntityDescription = outcome.TargetEntityDescription,
        SyncRuleId = outcome.SyncRuleId,
        SyncRuleName = outcome.SyncRuleName,
        DetailCount = outcome.DetailCount,
        DetailMessage = outcome.DetailMessage,
        Ordinal = outcome.Ordinal,
        Children = outcome.Children.OrderBy(c => c.Ordinal).Select(FromSyncOutcome).ToList()
    };
}
