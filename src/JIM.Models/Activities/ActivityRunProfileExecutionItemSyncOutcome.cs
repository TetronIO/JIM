using System.ComponentModel.DataAnnotations;

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
    /// The type of outcome that occurred (e.g., Projected, AttributeFlow, PendingExportCreated).
    /// </summary>
    public ActivityRunProfileExecutionItemSyncOutcomeType OutcomeType { get; set; }

    /// <summary>
    /// Target entity context — the MVO ID, target CSO ID, or connected system ID relevant to this outcome.
    /// </summary>
    public Guid? TargetEntityId { get; set; }

    /// <summary>
    /// Snapshot description for display without joins (e.g., connected system name, MVO display name).
    /// </summary>
    public string? TargetEntityDescription { get; set; }

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
}
