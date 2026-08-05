// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Preview;

namespace JIM.Web.Models.Api;

/// <summary>
/// What a proposed configuration would do to one object.
///
/// **Old and new values are attribute values, and therefore personal data.** They carry the same posture as change
/// data on a Run Profile Execution Item: never logged, never emitted in diagnostics, and removed by the same
/// retention housekeeping.
/// </summary>
public class ConfigurationChangePreviewDeltaResponse
{
    public Guid Id { get; set; }

    /// <summary>The summary group this delta was counted in.</summary>
    public Guid GroupId { get; set; }

    /// <summary>What would happen to this object.</summary>
    public ActivityRunProfileExecutionItemSyncOutcomeType TransitionType { get; set; }

    public Guid? MetaverseObjectId { get; set; }

    public Guid? ConnectedSystemObjectId { get; set; }

    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The object's display name and type name as they were when the preview ran. Snapshotted deliberately: a
    /// preview often concerns objects that are about to be deleted or disconnected, and a joined view would render
    /// blank rows for exactly the objects most worth seeing.
    /// </summary>
    public string? ObjectDisplayName { get; set; }

    public string? ObjectTypeName { get; set; }

    public string? AttributeName { get; set; }

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    /// <summary>
    /// What kind of edit this row describes, taking the same values as a group's own key. Per row, so a group that
    /// covers a mixture of edits can still be read by the kind each object makes. Null where nothing recognised it.
    /// </summary>
    public string? PatternKey { get; set; }

    public static ConfigurationChangePreviewDeltaResponse FromEntity(ConfigurationChangePreviewDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        return new ConfigurationChangePreviewDeltaResponse
        {
            Id = delta.Id,
            GroupId = delta.GroupId,
            TransitionType = delta.TransitionType,
            MetaverseObjectId = delta.MetaverseObjectId,
            ConnectedSystemObjectId = delta.ConnectedSystemObjectId,
            ConnectedSystemId = delta.ConnectedSystemId,
            ObjectDisplayName = delta.ObjectDisplayName,
            ObjectTypeName = delta.ObjectTypeName,
            AttributeName = delta.AttributeName,
            OldValue = delta.OldValue,
            NewValue = delta.NewValue,
            PatternKey = delta.PatternKey
        };
    }
}
