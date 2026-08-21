// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JIM.Models.Staging;

namespace JIM.Models.Activities;

/// <summary>
/// Records a single outcome node in the causal graph for a Run Profile Execution Item.
/// Outcomes form a tree structure: a root outcome (e.g., Projected) can have children
/// (e.g., AttributeFlow) which can in turn have children (e.g., PendingExportCreated).
/// This gives administrators the full story of what happened to a single CSO during processing.
/// </summary>
public class ActivityRunProfileExecutionItemSyncOutcome
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// FK to the parent Run Profile Execution Item that this outcome belongs to.
    /// </summary>
    public Guid ActivityRunProfileExecutionItemId { get; set; }
    public ActivityRunProfileExecutionItem ActivityRunProfileExecutionItem { get; set; } = null!;

    /// <summary>
    /// Self-referential tree structure. Null for root outcomes.
    /// </summary>
    public Guid? ParentSyncOutcomeId { get; set; }
    public ActivityRunProfileExecutionItemSyncOutcome? ParentSyncOutcome { get; set; }
    public List<ActivityRunProfileExecutionItemSyncOutcome> Children { get; set; } = [];

    /// <summary>
    /// Whether this outcome hangs off another outcome, rather than being a root of its RPEI's tree.
    /// </summary>
    /// <remarks>
    /// The two parent links are populated at different times and never together, so neither one alone
    /// answers this question. While a tree is being built in memory the builder sets the navigation and
    /// the FK is still null (it is only resolved at bulk-insert flattening); once a tree is read back
    /// from the database the FK is set and the navigation is not loaded (outcomes are fetched as a flat
    /// list per RPEI). Testing the FK alone therefore silently classifies every freshly-built child as a
    /// root, which is what flattened the export outcomes onto the root of the causality tree (#1428).
    /// Ask this property rather than either link, unless a caller genuinely means one specific link.
    /// </remarks>
    [NotMapped]
    public bool IsChildOutcome => ParentSyncOutcomeId.HasValue || ParentSyncOutcome != null;

    /// <summary>
    /// The type of outcome that occurred (e.g., Projected, AttributeFlow, PendingExportCreated).
    /// </summary>
    public ActivityRunProfileExecutionItemSyncOutcomeType OutcomeType { get; set; }

    /// <summary>
    /// Target entity context — the MVO ID, target CSO ID, or Connected System ID relevant to this outcome.
    /// </summary>
    public Guid? TargetEntityId { get; set; }

    /// <summary>
    /// Snapshot description for display without joins (e.g., Connected System name, MVO display name).
    /// </summary>
    public string? TargetEntityDescription { get; set; }

    /// <summary>
    /// The id of the Synchronisation Rule attributed to this outcome, when one was determinable at
    /// decision time; for example the rule whose scope a Connected System Object fell out of for
    /// DisconnectedOutOfScope outcomes, or the rule that caused a Projected or Provisioned outcome.
    /// Stored as a plain scalar (no foreign key), matching the snapshot approach of
    /// <see cref="TargetEntityId"/>, so the attribution survives later rule deletion; resolve
    /// against live rules at display time and fall back to <see cref="SyncRuleName"/>.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// Snapshot of the attributed Synchronisation Rule's name at the time the outcome was recorded.
    /// Preserved for display without joins even if the rule is later renamed or deleted, matching
    /// the <see cref="TargetEntityDescription"/> snapshot pattern.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// Quantitative detail (e.g., "12 attributes flowed", "3 attributes exported").
    /// </summary>
    public int? DetailCount { get; set; }

    /// <summary>
    /// Optional context message providing additional detail about the outcome.
    /// </summary>
    public string? DetailMessage { get; set; }

    /// <summary>
    /// Ordering among siblings in the tree, for consistent display order.
    /// </summary>
    public int Ordinal { get; set; }

    /// <summary>
    /// Optional FK to a ConnectedSystemObjectChange that captures the attribute-level detail
    /// for this outcome. Used by PendingExportCreated outcomes to persist a snapshot of the
    /// Pending Export's attribute changes at sync time, before the PendingExport is deleted
    /// during export confirmation.
    /// </summary>
    public Guid? ConnectedSystemObjectChangeId { get; set; }
    public ConnectedSystemObjectChange? ConnectedSystemObjectChange { get; set; }
}
