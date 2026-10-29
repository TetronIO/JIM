// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;
using JIM.Models.Staging;

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// One execution item as the Causality Explorer (#2100) lists it: what happened to an object in one run, with the run
/// named. An object's causal timeline is a page of these, and so are the runs that queued a Pending Export.
/// </summary>
/// <remarks>
/// A flat projection of the item and its Activity, read in one statement for a whole page. The run is named from the
/// Activity's own snapshots, taken when it ran, so a Run Profile or Connected System deleted or renamed since still
/// reads as it did at the time.
/// </remarks>
public class CausalTimelineEntry
{
    public Guid RunProfileExecutionItemId { get; init; }

    public Guid ActivityId { get; init; }

    /// <summary>
    /// When the item's Activity ran (UTC). Items have no time of their own; everything one run did shares this.
    /// </summary>
    public DateTime Executed { get; init; }

    /// <summary>
    /// What the Activity was: a Run Profile execution for almost every item, and otherwise the housekeeping or
    /// administrative work that recorded it.
    /// </summary>
    public ActivityTargetType ActivityTargetType { get; init; }

    /// <summary>
    /// The Activity's target as it recorded it: for a Run Profile execution, the Run Profile's name.
    /// </summary>
    public string? ActivityTargetName { get; init; }

    /// <summary>
    /// The Activity's context as it recorded it: for a Run Profile execution, the Connected System's name.
    /// </summary>
    public string? ActivityTargetContext { get; init; }

    public int? ConnectedSystemId { get; init; }

    public ConnectedSystemRunType? RunType { get; init; }

    public ObjectChangeType ObjectChangeType { get; init; }

    /// <summary>
    /// The item's outcomes as counted for list views, for example "Projected:1,AttributeFlow:12". Null for items
    /// recorded before outcomes were, or while outcome tracking is switched off.
    /// </summary>
    public string? OutcomeSummary { get; init; }

    public ActivityRunProfileExecutionItemErrorType? ErrorType { get; init; }

    /// <summary>
    /// The object's name as the item recorded it.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// The object's type as the item recorded it, or, where it recorded none, the type of the Connected System Object it
    /// processed while that still exists.
    /// </summary>
    public string? ObjectType { get; init; }

    /// <summary>
    /// The Connected System Object the item processed, while it exists; its deletion nulls this.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; init; }

    /// <summary>
    /// The external ID as the item recorded it, which survives the object's deletion.
    /// </summary>
    public string? ExternalId { get; init; }

    /// <summary>
    /// The Metaverse Object the item records directly, where it records one rather than a Connected System Object
    /// (an Export Review).
    /// </summary>
    public Guid? MetaverseObjectId { get; init; }
}
