// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// Wraps a MetaverseObject with optional per-attribute metadata.
/// When loaded with <see cref="MvoAttributeLoadStrategy.CappedMva"/>, includes
/// total value counts per attribute so consumers know when values were capped.
/// </summary>
public class MvoDetailResult
{
    public MetaverseObject MetaverseObject { get; set; } = null!;

    /// <summary>
    /// Per-attribute total value counts. Only populated when the load strategy
    /// caps MVA values (e.g. <see cref="MvoAttributeLoadStrategy.CappedMva"/>).
    /// Key is the attribute name; value is the total count in the database.
    /// </summary>
    public Dictionary<string, int> AttributeValueTotalCounts { get; set; } = new();

    /// <summary>
    /// Total number of change-history records for this MVO. Surfaced separately so
    /// detail callers can render a count badge without eager-loading the full change
    /// graph. Change rows are paged via a dedicated repository call.
    /// </summary>
    public int ChangeCount { get; set; }

    /// <summary>
    /// Initiator metadata of the earliest change (used for "Created By"). Null when the MVO has no change history.
    /// </summary>
    public MvoChangeInitiatorSummary? EarliestChangeInitiator { get; set; }

    /// <summary>
    /// Initiator metadata of the most recent change (used for "Last Updated By"). Null when the MVO has no change history.
    /// </summary>
    public MvoChangeInitiatorSummary? LatestChangeInitiator { get; set; }
}

/// <summary>
/// Minimal initiator information sourced from a single MetaverseObjectChange row.
/// Carried on <see cref="MvoDetailResult"/> so the detail page can render
/// Created By / Last Updated By without loading the change graph.
/// </summary>
public class MvoChangeInitiatorSummary
{
    public DateTime ChangeTime { get; set; }
    public JIM.Models.Activities.ActivityInitiatorType InitiatedByType { get; set; }
    public Guid? InitiatedById { get; set; }
    public string? InitiatedByName { get; set; }
}
