// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// What an Activity has to do with a Connected System Object Type that a synchronisation processes in full. For a
/// configuration change Activity, the changes it saved to the type and whether a synchronisation has applied them yet. For
/// a synchronisation run, the changes it claimed and how many objects of the type it processed because of them. A
/// Delta Synchronisation processes changed objects only; these changes are why it processed the whole type.
/// </summary>
public class ActivityObjectTypeSynchronisationDto
{
    /// <summary>
    /// The unique identifier of the Connected System whose synchronisation applies the changes.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The name of that Connected System.
    /// </summary>
    public string ConnectedSystemName { get; set; } = string.Empty;

    /// <summary>
    /// The unique identifier of the Connected System Object Type.
    /// </summary>
    public int ConnectedSystemObjectTypeId { get; set; }

    /// <summary>
    /// The name of the Connected System Object Type.
    /// </summary>
    public string ObjectTypeName { get; set; } = string.Empty;

    /// <summary>
    /// For a synchronisation run, how many Connected System Objects of the type exist, which is how many the run processed
    /// because of the changes. Null for a configuration change Activity. It is counted when the Activity is read, not
    /// stored by the run, so it can differ from the figure at the time if objects have since been added or removed.
    /// </summary>
    public int? ObjectCount { get; set; }

    /// <summary>
    /// Whether every one of the changes has been applied by a synchronisation. For a run that did not complete, false: a
    /// run applies its changes only as it completes, and the next synchronisation takes them over.
    /// </summary>
    public bool IsApplied { get; set; }

    /// <summary>
    /// When the last of the changes was applied (UTC). Null unless the changes have been applied.
    /// </summary>
    public DateTime? AppliedAt { get; set; }

    /// <summary>
    /// The synchronisation run that applied the changes. Null unless the changes have been applied, and null once that
    /// run's Activity has been removed.
    /// </summary>
    public Guid? AppliedByActivityId { get; set; }

    /// <summary>
    /// The changes, oldest first. At most 500 are listed across the Activity.
    /// </summary>
    public List<AwaitingSynchronisationChangeDto> Changes { get; set; } = new();

    /// <summary>
    /// Creates a DTO from the application-layer group.
    /// </summary>
    public static ActivityObjectTypeSynchronisationDto FromGroup(ActivityObjectTypeSynchronisation group)
    {
        return new ActivityObjectTypeSynchronisationDto
        {
            ConnectedSystemId = group.ConnectedSystemId,
            ConnectedSystemName = group.ConnectedSystemName,
            ConnectedSystemObjectTypeId = group.ConnectedSystemObjectTypeId,
            ObjectTypeName = group.ObjectTypeName,
            ObjectCount = group.ObjectCount,
            IsApplied = group.IsApplied,
            AppliedAt = group.AppliedAt,
            AppliedByActivityId = group.AppliedByActivityId,
            Changes = group.Changes.Select(AwaitingSynchronisationChangeDto.FromSummary).ToList()
        };
    }
}
