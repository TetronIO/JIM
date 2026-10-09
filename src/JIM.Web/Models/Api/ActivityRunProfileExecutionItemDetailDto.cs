// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Enums;

namespace JIM.Web.Models.Api;

/// <summary>
/// One Run Profile Execution Item: what happened to one object in a Run Profile execution, and, when it failed,
/// why (#2032). The list endpoint returns headers, which carry the error type but not the message; this carries the
/// message and stack trace, the snapshots taken when the item was recorded, and the ids of what it is about.
/// </summary>
public class ActivityRunProfileExecutionItemDetailDto
{
    /// <summary>The item's unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The Run Profile Activity the item belongs to.</summary>
    public Guid ActivityId { get; set; }

    /// <summary>What happened to the object. Serialised as the enum member name.</summary>
    public ObjectChangeType ObjectChangeType { get; set; }

    /// <summary>Why nothing changed, when nothing did. Serialised as the enum member name.</summary>
    public NoChangeReason? NoChangeReason { get; set; }

    /// <summary>The Connected System Object the item is about, when it still exists.</summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>The Metaverse Object the item is about, where it records one (kept as history; it may since have been deleted).</summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>The Pending Export the item is about, for an export.</summary>
    public Guid? PendingExportId { get; set; }

    /// <summary>The object's external ID when the item was recorded.</summary>
    public string? ExternalIdSnapshot { get; set; }

    /// <summary>The object's display name when the item was recorded.</summary>
    public string? DisplayNameSnapshot { get; set; }

    /// <summary>The object's type name when the item was recorded.</summary>
    public string? ObjectTypeSnapshot { get; set; }

    /// <summary>The kind of failure; <c>NotSet</c> or null when the object did not fail. Serialised as the enum member name.</summary>
    public ActivityRunProfileExecutionItemErrorType? ErrorType { get; set; }

    /// <summary>What went wrong, in words; null when the object did not fail.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>The stack trace, when the failure came from an exception.</summary>
    public string? ErrorStackTrace { get; set; }

    /// <summary>How many Metaverse attributes changed alongside a join, projection or disconnection.</summary>
    public int? AttributeFlowCount { get; set; }

    /// <summary>Outcome types with counts, for example <c>Projected:1,AttributeFlow:12</c>.</summary>
    public string? OutcomeSummary { get; set; }

    /// <summary>
    /// Creates the detail from an item's own row; no navigation property is read, so the light retrieval suffices.
    /// </summary>
    public static ActivityRunProfileExecutionItemDetailDto FromEntity(ActivityRunProfileExecutionItem entity)
    {
        return new ActivityRunProfileExecutionItemDetailDto
        {
            Id = entity.Id,
            ActivityId = entity.ActivityId,
            ObjectChangeType = entity.ObjectChangeType,
            NoChangeReason = entity.NoChangeReason,
            ConnectedSystemObjectId = entity.ConnectedSystemObjectId,
            MetaverseObjectId = entity.MetaverseObjectId,
            PendingExportId = entity.PendingExportId,
            ExternalIdSnapshot = entity.ExternalIdSnapshot,
            DisplayNameSnapshot = entity.DisplayNameSnapshot,
            ObjectTypeSnapshot = entity.ObjectTypeSnapshot,
            ErrorType = entity.ErrorType,
            ErrorMessage = entity.ErrorMessage,
            ErrorStackTrace = entity.ErrorStackTrace,
            AttributeFlowCount = entity.AttributeFlowCount,
            OutcomeSummary = entity.OutcomeSummary
        };
    }
}
