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
    /// <summary>The row's unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The summary group this delta was counted in.</summary>
    public Guid GroupId { get; set; }

    /// <summary>What would happen to this object.</summary>
    public ActivityRunProfileExecutionItemSyncOutcomeType TransitionType { get; set; }

    /// <summary>
    /// The Metaverse Object concerned, where the transition names one: a deletion eligibility change does, and so
    /// does an Attribute Flow change.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// The Connected System Object concerned, where the transition names one: a scope change does, and so does an
    /// Attribute Flow change.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>The Connected System the Connected System Object belongs to, where there is one.</summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The object's display name as it was when the preview ran, so it still reads correctly for an object about
    /// to be deleted or disconnected.
    /// </summary>
    /// <remarks>
    /// Snapshotted deliberately: a preview often concerns objects that are about to be deleted or disconnected, and
    /// a joined view would render blank rows for exactly the objects most worth seeing.
    /// </remarks>
    public string? ObjectDisplayName { get; set; }

    /// <summary>The object's type name as it was when the preview ran.</summary>
    public string? ObjectTypeName { get; set; }

    /// <summary>The attribute whose value would change, for an Attribute Flow change; null otherwise.</summary>
    public string? AttributeName { get; set; }

    /// <summary>The attribute's value before the change, for an Attribute Flow change; null where it has none.</summary>
    public string? OldValue { get; set; }

    /// <summary>The attribute's value after the change, for an Attribute Flow change; null where it would have none.</summary>
    public string? NewValue { get; set; }

    /// <summary>
    /// What kind of edit this row describes, taking the same values as a group's own key. Per row, so a group that
    /// covers a mixture of edits can still be read by the kind each object makes. Null where nothing recognised it.
    /// </summary>
    public string? PatternKey { get; set; }

    /// <summary>
    /// For a row predicting an ambiguous match, the objects it would find, in recorded order: at most ten, each
    /// carrying the full count in <c>candidateTotal</c>. Empty for every other row. The same shape as an Activity
    /// item's <c>matchCandidates</c>.
    /// </summary>
    public List<MatchCandidateDto> MatchCandidates { get; set; } = [];

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
            PatternKey = delta.PatternKey,
            MatchCandidates = [.. delta.MatchCandidates.OrderBy(c => c.Ordinal).Select(MatchCandidateDto.FromModel)]
        };
    }
}
